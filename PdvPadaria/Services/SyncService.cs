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
    public class SyncService : IDisposable
    {
        public string LastError { get; private set; } = string.Empty;
        private readonly SQLiteConnection _dbConnection;

        // HttpClient ÚNICO e compartilhado entre todos os syncs. Um SyncService é criado a cada
        // ciclo de 60s; criar um HttpClient por ciclo esgota sockets (TIME_WAIT). Estático resolve.
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private string _supabaseUrl = string.Empty;
        private string _supabaseAnonKey = string.Empty;

        public SyncService(SQLiteConnection dbConnection)
        {
            _dbConnection = dbConnection;
            // Espera até 5s por locks (concorrência com a conexão de vendas) em vez de falhar na hora.
            _dbConnection.BusyTimeout = TimeSpan.FromSeconds(5);

            CarregarConfiguracao();
        }

        // Fecha a conexão SQLite criada para este ciclo de sync (evita vazar handles).
        public void Dispose()
        {
            try { _dbConnection?.Close(); } catch { }
            try { _dbConnection?.Dispose(); } catch { }
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

                // 2b. Movimentos que não vieram de venda (ajuste manual, perda, reposição)
                //     não têm SaleId, então o laço acima nunca os alcança. Sem isto eles
                //     ficariam IsSynced=false para sempre e o dono perderia o MOTIVO de
                //     cada baixa — justamente o que permite conferir desvio. O saldo já
                //     subia pelo push_estoque; o que faltava era o histórico.
                //     SQL cru de propósito: o LINQ do sqlite-net traduz "== null" de forma
                //     frágil, e aqui um erro silencioso significaria histórico perdido.
                var avulsos = _dbConnection.Query<StockMovement>(
                    "SELECT * FROM StockMovement WHERE IsSynced = 0 AND (SaleId IS NULL OR SaleId = '')");
                movementsToSend.AddRange(avulsos);

                // Nada pendente dos dois lados: encerra sem chamar a rede.
                if (salesToSend.Count == 0 && movementsToSend.Count == 0) return true;

                // 3. Envia tudo num único payload para a função RPC server-side.
                //    push_vendas grava as 3 tabelas em uma transação (Pai -> Filhas),
                //    ignorando RLS (security definer). A anon key não escreve direto.
                bool pushSuccess = await PushVendasRpcAsync(salesToSend, itemsToSend, movementsToSend);
                if (!pushSuccess) return false;

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

                    // Marca também os movimentos avulsos que foram no mesmo payload.
                    foreach (var m in avulsos)
                    {
                        m.IsSynced = true;
                        m.SyncedAt = DateTime.Now;
                        _dbConnection.Update(m);
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

        // Envia vendas/itens/movimentos num único payload para a função RPC push_vendas.
        // A função grava server-side (security definer), então a anon key não precisa
        // de permissão de escrita direta nas tabelas — fica só com EXECUTE na função.
        private async Task<bool> PushVendasRpcAsync(List<Sale> sales, List<SaleItem> items, List<StockMovement> movements)
        {
            try
            {
                // Token da loja: o servidor carimba storeId/tenantId a partir dele (ignora o payload).
                string storeToken = EnvService.Get("STORE_SYNC_TOKEN");
                if (string.IsNullOrEmpty(storeToken))
                {
                    LastError = "STORE_SYNC_TOKEN ausente no .env desta máquina.";
                    return false;
                }

                // Mapeia classes C# (PascalCase) para colunas do Postgres (camelCase)
                var settings = new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                };
                var body = new
                {
                    p_payload = new
                    {
                        sales = sales,
                        items = items,
                        movements = movements
                    },
                    p_token = storeToken
                };
                var requestBody = JsonConvert.SerializeObject(body, settings);
                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

                var url = $"{_supabaseUrl.TrimEnd('/')}/rest/v1/rpc/push_vendas";
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = content;
                    request.Headers.Add("apikey", _supabaseAnonKey);
                    request.Headers.Add("Authorization", $"Bearer {_supabaseAnonKey}");

                    var response = await _httpClient.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync();
                        LastError = $"Erro HTTP {response.StatusCode} no push_vendas: {errorBody}";
                        System.Diagnostics.Debug.WriteLine($"[Supabase RPC Error]: {LastError}");
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                System.Diagnostics.Debug.WriteLine($"[Supabase RPC push_vendas Error]: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Métodos de Push de Estoque (envia a "foto" do estoque local da loja para a nuvem)

        /// <summary>
        /// Envia o estoque local atual desta loja para a nuvem (upsert em StoreProduct via RPC push_estoque).
        /// Como cada loja tem 1 caixa, o estoque local é a fonte da verdade dela — o dono lê esses
        /// valores no painel consolidado. É uma "foto" absoluta (não um delta), então é idempotente
        /// e nunca duplica baixa, mesmo que rode várias vezes. Produtos que não existem na nuvem
        /// são ignorados server-side (a função filtra por FK), evitando erro com itens só-locais.
        /// </summary>
        public async Task<bool> PushStockSnapshotAsync(string tenantId, string storeId)
        {
            if (string.IsNullOrEmpty(_supabaseUrl) || string.IsNullOrEmpty(_supabaseAnonKey))
                return false;
            if (string.IsNullOrEmpty(storeId))
                return false;

            // Token secreto da loja (autoriza a escrita do estoque na nuvem; o servidor deriva a loja
            // dele, ignorando qualquer storeId do payload). Máquina ainda sem token no .env pula sem erro.
            string storeToken = EnvService.Get("STORE_SYNC_TOKEN");
            if (string.IsNullOrEmpty(storeToken))
                return true;

            try
            {
                var products = _dbConnection.Table<Product>().ToList();
                if (products.Count == 0) return true;

                var snapshot = products.Select(p => new
                {
                    ProductId = p.Id,
                    Quantity = p.LocalStockQuantity
                }).ToList();

                var settings = new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                };
                var body = new { p_payload = new { stock = snapshot }, p_token = storeToken };
                var requestBody = JsonConvert.SerializeObject(body, settings);
                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

                var url = $"{_supabaseUrl.TrimEnd('/')}/rest/v1/rpc/push_estoque";
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = content;
                    request.Headers.Add("apikey", _supabaseAnonKey);
                    request.Headers.Add("Authorization", $"Bearer {_supabaseAnonKey}");

                    var response = await _httpClient.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync();
                        LastError = $"Erro HTTP {response.StatusCode} no push_estoque: {errorBody}";
                        System.Diagnostics.Debug.WriteLine($"[Supabase RPC push_estoque Error]: {LastError}");
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                System.Diagnostics.Debug.WriteLine($"[Push Estoque Error]: {ex.Message}");
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
                // Puxa TODOS os produtos (ativos e inativos). O flag Active é propagado para o
                // SQLite local, então um produto desativado na nuvem (excluir_produto / dashboard)
                // vira Active=false aqui e some das telas que filtram por Active. Isso dá o
                // "apaguei na web → sumiu no PDV" sem hard delete (preserva FK das vendas locais).
                var productsTask = GetFromSupabaseAsync<Product>($"Product?tenantId=eq.{Uri.EscapeDataString(tenantId)}");
                var storeProductsTask = GetFromSupabaseAsync<StoreProductDto>($"StoreProduct?storeId=eq.{Uri.EscapeDataString(storeId)}");
                var breadConfigsTask = GetFromSupabaseAsync<BreadConfig>($"BreadConfig?storeId=eq.{Uri.EscapeDataString(storeId)}&active=eq.true");

                // Aguarda a conclusão simultânea de todas as tarefas de rede.
                // Usuários NÃO são mais puxados: o login virou server-side (RPC login_caixa)
                // e a anon key não tem leitura na tabela User. O login offline passa a
                // depender do admin semeado localmente e dos operadores que já logaram online.
                await Task.WhenAll(categoriesTask, productsTask, storeProductsTask, breadConfigsTask);

                var categories = await categoriesTask;
                var products = await productsTask;
                var storeProducts = await storeProductsTask;
                var breadConfigs = await breadConfigsTask;

                // Se a consulta crítica de produtos falhar por rede, cancela para não salvar dados incompletos
                if (products == null)
                {
                    System.Diagnostics.Debug.WriteLine("[Pull Error]: Falha ao carregar a tabela de produtos da nuvem.");
                    return false;
                }

                // 2. Mapa de estoque da nuvem — usado SOMENTE para semear produtos novos.
                //    A loja é a dona do próprio estoque: produto que já existe localmente
                //    NÃO tem o estoque sobrescrito pela nuvem (senão a venda offline "voltava"
                //    a cada pull). A aplicação real desse mapa acontece na transação abaixo.
                var stockMap = (storeProducts ?? new List<StoreProductDto>())
                    .GroupBy(sp => sp.ProductId)
                    .ToDictionary(g => g.Key, g => g.First());

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

                    // Sincroniza Produtos. O ESTOQUE é tratado à parte para a loja ser a fonte da verdade:
                    //  - produto JÁ existente: atualiza só os campos de CADASTRO (nome, preço, etc.) via
                    //    UPDATE seletivo — NUNCA reescreve a coluna de estoque (elimina a corrida com a
                    //    venda que rodava read-modify-write numa conexão separada);
                    //  - produto NOVO: insere semeando o estoque com o valor da nuvem desta loja.
                    if (products != null)
                    {
                        foreach (var prod in products)
                        {
                            var existing = _dbConnection.Find<Product>(prod.Id);
                            if (existing != null)
                            {
                                _dbConnection.Execute(
                                    "UPDATE Product SET Name=?, Barcode=?, PriceSale=?, PriceCost=?, Type=?, " +
                                    "UnitMeasure=?, Active=?, ImageUrl=?, CategoryId=?, TenantId=?, UpdatedAt=? WHERE Id=?",
                                    prod.Name, prod.Barcode, prod.PriceSale, prod.PriceCost, prod.Type,
                                    prod.UnitMeasure, prod.Active, prod.ImageUrl, prod.CategoryId, prod.TenantId,
                                    DateTime.Now, prod.Id);
                            }
                            else
                            {
                                // Produto novo que já chega inativo da nuvem = foi excluído antes
                                // mesmo de existir aqui. Não insere (evita poluir a base local).
                                if (!prod.Active) continue;

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
                                _dbConnection.Insert(prod);
                            }
                        }
                    }

                    // Sincroniza Regras de Preço de Pão.
                    // A nuvem é a dona da tabela de faixas. Antes só inseríamos a linha da nuvem,
                    // que passava a conviver com a semente local "config-pao-test" criada no
                    // primeiro boot. Como a leitura pegava uma linha qualquer, o caixa podia
                    // continuar com a faixa antiga depois de o dono mudar o preço no painel.
                    // Apagar as outras linhas desta loja deixa uma só, e ela é a da nuvem.
                    if (breadConfigs != null && breadConfigs.Count > 0)
                    {
                        _dbConnection.Execute(
                            "DELETE FROM BreadConfig WHERE StoreId = ? AND Id <> ?",
                            storeId, breadConfigs[0].Id);
                        _dbConnection.InsertOrReplace(breadConfigs[0]);
                    }
                });

                // 4. Aplica ajustes de estoque feitos pelo DONO para esta loja (controle remoto).
                //    Cada ajuste é aplicado UMA única vez — não reverte venda offline a cada pull.
                await ApplyOwnerAdjustmentsAsync(storeId);

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Pull Error]: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Baixa os ajustes de estoque que o dono lançou na nuvem para ESTA loja e os aplica
        /// no estoque local, marcando cada um como aplicado (idempotente pelo id do ajuste).
        /// </summary>
        private async Task ApplyOwnerAdjustmentsAsync(string storeId)
        {
            try
            {
                var ajustes = await GetFromSupabaseAsync<OwnerStockAdjustmentDto>(
                    $"OwnerStockAdjustment?storeId=eq.{Uri.EscapeDataString(storeId)}&order=createdAt.asc");
                if (ajustes == null || ajustes.Count == 0) return;

                _dbConnection.RunInTransaction(() =>
                {
                    foreach (var aj in ajustes)
                    {
                        if (string.IsNullOrEmpty(aj.Id)) continue;
                        // Já aplicado antes? pula (uma vez só).
                        if (_dbConnection.Find<AppliedOwnerAdjustment>(aj.Id) != null) continue;

                        var prod = _dbConnection.Find<Product>(aj.ProductId);
                        if (prod != null)
                        {
                            // O número lançado pelo dono vale para o INSTANTE em que ele lançou.
                            // Se a loja estava offline e vendeu depois disso, essas vendas
                            // precisam continuar descontadas — senão o ajuste "devolveria" ao
                            // estoque itens que já saíram, quebrando a conferência de caixa.
                            // Vendas anteriores ao lançamento NÃO são descontadas: elas já
                            // estavam refletidas no número que o dono informou.
                            double vendidoDepois = 0;
                            try
                            {
                                vendidoDepois = _dbConnection.ExecuteScalar<double>(
                                    @"SELECT COALESCE(SUM(si.Quantity), 0)
                                        FROM SaleItem si
                                        JOIN Sale s ON s.Id = si.SaleId
                                       WHERE si.ProductId = ?
                                         AND s.PaymentStatus = 'APROVADO'
                                         AND s.SaleDate > ?",
                                    aj.ProductId, aj.CreatedAt);
                            }
                            catch (Exception exVendas)
                            {
                                // Sem conseguir medir, é mais seguro aplicar o número puro do
                                // que travar o ajuste inteiro.
                                System.Diagnostics.Debug.WriteLine($"[Ajuste: soma de vendas falhou]: {exVendas.Message}");
                            }

                            double saldoFinal = Math.Max(0, aj.Quantity - vendidoDepois);

                            if (aj.MinStock.HasValue)
                                _dbConnection.Execute(
                                    "UPDATE Product SET LocalStockQuantity=?, MinStock=? WHERE Id=?",
                                    saldoFinal, aj.MinStock.Value, aj.ProductId);
                            else
                                _dbConnection.Execute(
                                    "UPDATE Product SET LocalStockQuantity=? WHERE Id=?",
                                    saldoFinal, aj.ProductId);
                        }

                        _dbConnection.Insert(new AppliedOwnerAdjustment { Id = aj.Id });
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApplyOwnerAdjustments Error]: {ex.Message}");
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

    // Ajuste de estoque que o DONO fez para esta loja na nuvem; aplicado uma única vez.
    public class OwnerStockAdjustmentDto
    {
        public string Id { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
        public double Quantity { get; set; }
        public double? MinStock { get; set; }
        // Momento em que o dono lançou o número. É o que permite descontar apenas as
        // vendas ocorridas DEPOIS do lançamento (ver ApplyOwnerAdjustmentsAsync).
        public DateTime CreatedAt { get; set; }
    }

    #endregion
}
