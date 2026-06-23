using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using SQLite;
using Newtonsoft.Json;
using PdvPadaria.Models;

namespace PdvPadaria.Services
{
    public class SyncService
    {
        public string LastError { get; private set; } = string.Empty;
        private readonly SQLiteConnection _dbConnection;
        private readonly HttpClient _httpClient;
        private string _supabaseUrl = string.Empty;
        private string _supabaseAnonKey = string.Empty;

        public SyncService(SQLiteConnection dbConnection)
        {
            _dbConnection = dbConnection;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
            
            CarregarConfiguracao();
        }

        // Carrega configurações diretamente do arquivo .env local para falar com o Supabase
        private void CarregarConfiguracao()
        {
            _supabaseUrl = EnvService.Get("SUPABASE_URL");
            _supabaseAnonKey = EnvService.Get("SUPABASE_ANON_KEY");
        }

        #region Métodos de Push (Envio de Vendas locais para a Nuvem)

        /// <summary>
        /// Envia todas as vendas locais pendentes diretamente para o banco do Supabase
        /// </summary>
        public async Task<bool> PushSalesAsync(string tenantId, string storeId)
        {
            if (string.IsNullOrEmpty(_supabaseUrl) || string.IsNullOrEmpty(_supabaseAnonKey))
            {
                System.Diagnostics.Debug.WriteLine("[Push Error]: Credenciais do Supabase ausentes no .env");
                return false;
            }

            try
            {
                // 1. Busca vendas locais que não foram sincronizadas
                var pendingSales = _dbConnection.Table<Sale>()
                    .Where(s => !s.IsSynced)
                    .ToList();

                if (pendingSales.Count == 0) return true;

                // 2. Coleta itens e movimentos relacionados
                var salesToSend = new List<Sale>();
                var itemsToSend = new List<SaleItem>();
                var movementsToSend = new List<StockMovement>();

                foreach (var sale in pendingSales)
                {
                    salesToSend.Add(sale);

                    var items = _dbConnection.Table<SaleItem>()
                        .Where(i => i.SaleId == sale.Id)
                        .ToList();
                    itemsToSend.AddRange(items);

                    var movements = _dbConnection.Table<StockMovement>()
                        .Where(m => m.SaleId == sale.Id)
                        .ToList();
                    movementsToSend.AddRange(movements);
                }

                // 3. Envia os dados sequencialmente respeitando a integridade das tabelas (Pai -> Filhas)
                // Envia para a tabela Sale
                bool saleSuccess = await PostToSupabaseAsync("Sale", salesToSend);
                if (!saleSuccess) return false;

                // Envia para a tabela SaleItem
                if (itemsToSend.Count > 0)
                {
                    bool itemsSuccess = await PostToSupabaseAsync("SaleItem", itemsToSend);
                    if (!itemsSuccess) return false;
                }

                // Envia para a tabela StockMovement
                if (movementsToSend.Count > 0)
                {
                    bool movementsSuccess = await PostToSupabaseAsync("StockMovement", movementsToSend);
                    if (!movementsSuccess) return false;
                }

                // 4. Se tudo deu certo, atualiza os status no SQLite local de forma transacional e atômica
                _dbConnection.RunInTransaction(() =>
                {
                    foreach (var sale in pendingSales)
                    {
                        sale.IsSynced = true;
                        sale.SyncedAt = DateTime.Now;
                        _dbConnection.Update(sale);

                        var movements = _dbConnection.Table<StockMovement>()
                            .Where(m => m.SaleId == sale.Id)
                            .ToList();
                        
                        foreach (var m in movements)
                        {
                            m.IsSynced = true;
                            m.SyncedAt = DateTime.Now;
                            _dbConnection.Update(m);
                        }
                    }
                });

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Push Error]: {ex.Message}");
                return false;
            }
        }

        // Método genérico para fazer POST diretamente na API REST do Supabase
        private async Task<bool> PostToSupabaseAsync(string tableName, object payload)
        {
            try
            {
                // Mapeia classes C# (PascalCase) para colunas do Postgres (camelCase)
                var settings = new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                };
                var requestBody = JsonConvert.SerializeObject(payload, settings);
                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

                var url = $"{_supabaseUrl.TrimEnd('/')}/rest/v1/{tableName}";
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = content;
                    request.Headers.Add("apikey", _supabaseAnonKey);
                    request.Headers.Add("Authorization", $"Bearer {_supabaseAnonKey}");
                    request.Headers.Add("Prefer", "resolution=merge-duplicates"); // Resolve colisões se retransmitir

                    var response = await _httpClient.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync();
                        LastError = $"Erro HTTP {response.StatusCode} no POST {tableName}: {errorBody}";
                        System.Diagnostics.Debug.WriteLine($"[Supabase POST Error]: {LastError}");
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                System.Diagnostics.Debug.WriteLine($"[Supabase POST Error for {tableName}]: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Métodos de Pull (Recebimento de cadastros e estoques da nuvem)

        /// <summary>
        /// Puxa atualizações cadastrais do Supabase e atualiza a base SQLite local
        /// </summary>
        public async Task<bool> PullUpdatesAsync(string tenantId, string storeId)
        {
            if (string.IsNullOrEmpty(_supabaseUrl) || string.IsNullOrEmpty(_supabaseAnonKey))
            {
                System.Diagnostics.Debug.WriteLine("[Pull Error]: Credenciais do Supabase ausentes no .env");
                return false;
            }

            try
            {
                // 1. Prepara as tasks em paralelo para performance máxima de rede
                var categoriesTask = GetFromSupabaseAsync<Category>($"Category?tenantId=eq.{Uri.EscapeDataString(tenantId)}");
                var productsTask = GetFromSupabaseAsync<Product>($"Product?tenantId=eq.{Uri.EscapeDataString(tenantId)}&active=eq.true");
                var storeProductsTask = GetFromSupabaseAsync<StoreProductDto>($"StoreProduct?storeId=eq.{Uri.EscapeDataString(storeId)}");
                var usersTask = GetFromSupabaseAsync<User>($"User?tenantId=eq.{Uri.EscapeDataString(tenantId)}&active=eq.true");
                var breadConfigsTask = GetFromSupabaseAsync<BreadConfig>($"BreadConfig?storeId=eq.{Uri.EscapeDataString(storeId)}&active=eq.true");

                // Aguarda a conclusão simultânea de todas as tarefas de rede
                await Task.WhenAll(categoriesTask, productsTask, storeProductsTask, usersTask, breadConfigsTask);

                var categories = await categoriesTask;
                var products = await productsTask;
                var storeProducts = await storeProductsTask;
                var users = await usersTask;
                var breadConfigs = await breadConfigsTask;

                // Se alguma consulta crítica falhar de forma geral por rede, cancela para não salvar dados incompletos
                if (products == null || users == null)
                {
                    System.Diagnostics.Debug.WriteLine("[Pull Error]: Falha ao carregar tabelas de produtos ou usuários da nuvem.");
                    return false;
                }

                // 2. Mescla as informações de estoque físico da loja nos produtos recebidos
                if (storeProducts != null && products != null)
                {
                    var stockMap = storeProducts.ToDictionary(sp => sp.ProductId, sp => sp);
                    foreach (var prod in products)
                    {
                        if (stockMap.TryGetValue(prod.Id, out var sp))
                        {
                            prod.LocalStockQuantity = sp.Quantity;
                            prod.MinStock = sp.MinStock;
                        }
                        else
                        {
                            prod.LocalStockQuantity = 0;
                            prod.MinStock = 0;
                        }
                    }
                }

                // 3. Insere/Atualiza os cadastros locais de forma transacional e atômica
                _dbConnection.RunInTransaction(() =>
                {
                    // Sincroniza Categorias
                    if (categories != null)
                    {
                        foreach (var cat in categories)
                        {
                            _dbConnection.InsertOrReplace(cat);
                        }
                    }

                    // Sincroniza Produtos e seus estoques
                    if (products != null)
                    {
                        foreach (var prod in products)
                        {
                            _dbConnection.InsertOrReplace(prod);
                        }
                    }

                    // Sincroniza Operadores e Usuários
                    if (users != null)
                    {
                        foreach (var u in users)
                        {
                            _dbConnection.InsertOrReplace(u);
                        }
                    }

                    // Sincroniza Regras de Preço de Pão
                    if (breadConfigs != null && breadConfigs.Count > 0)
                    {
                        _dbConnection.InsertOrReplace(breadConfigs[0]);
                    }
                });

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Pull Error]: {ex.Message}");
                return false;
            }
        }

        // Método genérico para fazer GET diretamente na API REST do Supabase
        private async Task<List<T>?> GetFromSupabaseAsync<T>(string urlQuery)
        {
            try
            {
                var url = $"{_supabaseUrl.TrimEnd('/')}/rest/v1/{urlQuery}";
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Add("apikey", _supabaseAnonKey);
                    request.Headers.Add("Authorization", $"Bearer {_supabaseAnonKey}");

                    var response = await _httpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        var responseText = await response.Content.ReadAsStringAsync();
                        // Deserializa camelCase do Postgres para PascalCase do C#
                        var settings = new JsonSerializerSettings
                        {
                            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                        };
                        return JsonConvert.DeserializeObject<List<T>>(responseText, settings);
                    }
                    else
                    {
                        string errorBody = await response.Content.ReadAsStringAsync();
                        LastError = $"Erro HTTP {response.StatusCode} no GET {urlQuery}: {errorBody}";
                        System.Diagnostics.Debug.WriteLine($"[Supabase GET Error]: {LastError}");
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                System.Diagnostics.Debug.WriteLine($"[Supabase GET Error for {urlQuery}]: {ex.Message}");
                return null;
            }
        }

        #endregion
    }

    #region Classes Auxiliares de Transferência de Dados (DTO)

    public class StoreProductDto
    {
        public string ProductId { get; set; } = string.Empty;
        public string StoreId { get; set; } = string.Empty;
        public double Quantity { get; set; }
        public double MinStock { get; set; }
    }

    #endregion
}
