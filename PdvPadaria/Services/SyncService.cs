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

        #region Leitura das respostas das RPCs de escrita

        /// <summary>
        /// Traduz o CORPO de uma resposta das RPCs de escrita (push_vendas / push_estoque)
        /// em uma mensagem de erro, ou null se a gravação foi aceita.
        ///
        /// Existe porque essas funções devolvem HTTP 200 mesmo quando recusam o envio: o
        /// motivo vem dentro do JSON, em {"error": "..."}. Enquanto o PDV olhava só o status
        /// HTTP, uma recusa passava por sucesso — as vendas eram marcadas como enviadas e
        /// apagadas da fila, e o estoque nunca chegava ao painel.
        /// </summary>
        private static string? ErroDaResposta(string corpo)
        {
            if (string.IsNullOrWhiteSpace(corpo)) return null;

            string codigo;
            try
            {
                var obj = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JToken>(corpo);
                // A RPC pode voltar como objeto ou dentro de um array de uma posição.
                if (obj is Newtonsoft.Json.Linq.JArray arr) obj = arr.First;
                codigo = (obj as Newtonsoft.Json.Linq.JObject)?["error"]?.ToString() ?? string.Empty;
            }
            catch
            {
                return null; // corpo não-JSON: deixa passar, o status HTTP já cuidou disso
            }

            if (string.IsNullOrEmpty(codigo)) return null;

            if (codigo == "invalid_token")
            {
                StoreIdentityService.MarcarTokenInvalido();
                return "A credencial de sincronizacao desta maquina nao vale mais. " +
                       "Enquanto isso, NENHUMA venda e NENHUM estoque deste caixa sobe para a nuvem. " +
                       "Para resolver: com a internet funcionando, saia e entre de novo. " +
                       "O caixa renova a credencial sozinho.";
            }

            return $"A nuvem recusou o envio: {codigo}";
        }

        #endregion

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

                // 4. ACK exato: marca apenas os IDs que realmente viajaram. Uma venda pode
                // ser cancelada enquanto o HTTP esta em voo; nesse caso ela continua pendente
                // para enviar o novo PaymentStatus, e o movimento de estorno nao e engolido.
                _dbConnection.RunInTransaction(() =>
                {
                    DateTime ackEm = DateTime.Now;
                    foreach (var sale in salesToSend)
                    {
                        _dbConnection.Execute(
                            "UPDATE Sale SET IsSynced = 1, SyncedAt = ? " +
                            "WHERE Id = ? AND IsSynced = 0 AND PaymentStatus = ?",
                            ackEm, sale.Id, sale.PaymentStatus);
                    }

                    foreach (var movimento in movementsToSend)
                    {
                        _dbConnection.Execute(
                            "UPDATE StockMovement SET IsSynced = 1, SyncedAt = ? WHERE Id = ?",
                            ackEm, movimento.Id);
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
                string storeToken = StoreIdentityService.TokenAtual();
                if (string.IsNullOrEmpty(storeToken))
                {
                    LastError = "Esta maquina ainda nao tem credencial de sincronizacao. " +
                                "Entre com o usuario desta loja estando conectado a internet.";
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
                    string corpo = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        LastError = $"Erro HTTP {response.StatusCode} no push_vendas: {corpo}";
                        System.Diagnostics.Debug.WriteLine($"[Supabase RPC Error]: {LastError}");
                        return false;
                    }

                    // A funcao push_vendas devolve HTTP 200 mesmo quando RECUSA o envio
                    // (ex.: {"error":"invalid_token"}). Confiar so no status HTTP fazia o
                    // PDV marcar as vendas como sincronizadas e nunca mais tentar de novo:
                    // a venda sumia de vez. O erro tem que vir do CORPO da resposta.
                    string? erroRpc = ErroDaResposta(corpo);
                    if (erroRpc != null)
                    {
                        LastError = erroRpc;
                        System.Diagnostics.Debug.WriteLine($"[Supabase RPC push_vendas recusado]: {corpo}");
                        return false;
                    }

                    try
                    {
                        var tokenResposta = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JToken>(corpo);
                        if (tokenResposta is Newtonsoft.Json.Linq.JArray array)
                            tokenResposta = array.First;
                        var objetoResposta = tokenResposta as Newtonsoft.Json.Linq.JObject;
                        if (objetoResposta?["mode"]?.ToString() != "ledger")
                        {
                            LastError = "A atualizacao segura do servidor ainda nao esta ativa. " +
                                        "As operacoes continuam na fila local para nova tentativa.";
                            return false;
                        }
                    }
                    catch
                    {
                        LastError = "A nuvem nao confirmou o envio. As operacoes continuam na fila local.";
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

        #region Troca de loja desta máquina

        /// <summary>
        /// Confere se esta máquina pode virar caixa de outra loja.
        ///
        /// A credencial troca no login, mas o SQLite também guarda o ESTOQUE. Antes de baixar
        /// o saldo da loja nova, precisamos garantir que não há operação antiga esperando.
        ///
        /// Pendencias da loja que esta entrando sao preservadas e reaplicadas sobre o snapshot
        /// correto. A troca so e recusada se houver operacao de OUTRA loja: ela nao pode subir
        /// usando a credencial nova.
        ///
        /// Não apaga nada. O histórico sincronizado pode continuar no SQLite, separado pelo
        /// StoreId, e o saldo da loja nova é semeado somente depois do registro funcionar.
        /// </summary>
        public (bool Ok, string Motivo) PodeTrocarDeLoja(string storeId)
        {
            try
            {
                // Pendencias da propria loja podem ser preservadas: o pull de semeadura
                // baixa o snapshot correto e reaplica somente os deltas desses movimentos.
                // O que nao pode e levar operacoes de OUTRA loja junto com a troca.
                int vendasPendentes = _dbConnection.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM Sale WHERE IsSynced = 0 " +
                    "AND (StoreId IS NULL OR StoreId = '' OR StoreId <> ?)", storeId);
                int movimentosPendentes = _dbConnection.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM StockMovement WHERE IsSynced = 0 " +
                    "AND (StoreId IS NULL OR StoreId = '' OR StoreId <> ?)", storeId);

                if (vendasPendentes > 0 || movimentosPendentes > 0)
                {
                    return (false,
                        $"Esta maquina ainda tem {vendasPendentes} venda(s) e {movimentosPendentes} " +
                        "movimento(s) de estoque de outra loja esperando para subir. " +
                        "Conecte a internet e espere a sincronizacao terminar antes de usar " +
                        "esta maquina em outra loja.");
                }

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PodeTrocarDeLoja]: {ex.Message}");
                return (false, "Nao foi possivel conferir a troca de loja: " + ex.Message);
            }
        }

        #endregion

        #region Métodos de Pull (Recebimento de cadastros e estoques da nuvem)

        /// <summary>
        /// Atualiza cadastros e reconstroi o saldo local a partir da projecao autoritativa
        /// da nuvem mais os movimentos desta maquina que ainda nao receberam ACK.
        /// O servidor aplica cada StockMovement.id uma unica vez; nao existe mais foto
        /// absoluta enviada por um PC capaz de sobrescrever o saldo dos outros.
        /// </summary>
        public async Task<bool> PullUpdatesAsync(string tenantId, string storeId)
        {
            if (string.IsNullOrEmpty(_supabaseUrl) || string.IsNullOrEmpty(_supabaseAnonKey))
            {
                LastError = "Credenciais do Supabase ausentes.";
                return false;
            }

            try
            {
                var categoriesTask = GetFromSupabaseAsync<Category>(
                    $"Category?tenantId=eq.{Uri.EscapeDataString(tenantId)}");
                var productsTask = GetFromSupabaseAsync<Product>(
                    $"Product?tenantId=eq.{Uri.EscapeDataString(tenantId)}");
                var storeProductsTask = GetFromSupabaseAsync<StoreProductDto>(
                    $"StoreProduct?storeId=eq.{Uri.EscapeDataString(storeId)}");
                var breadConfigsTask = GetFromSupabaseAsync<BreadConfig>(
                    $"BreadConfig?storeId=eq.{Uri.EscapeDataString(storeId)}&active=eq.true");

                await Task.WhenAll(categoriesTask, productsTask, storeProductsTask, breadConfigsTask);

                var categories = await categoriesTask;
                var products = await productsTask;
                var storeProducts = await storeProductsTask;
                var breadConfigs = await breadConfigsTask;

                if (products == null || storeProducts == null || breadConfigs == null)
                {
                    LastError = "Falha ao carregar cadastro ou estoque da loja.";
                    return false;
                }

                var stockMap = storeProducts
                    .GroupBy(sp => sp.ProductId)
                    .ToDictionary(g => g.Key, g => g.First());

                // A mesma transacao que le a fila tambem troca a base do estoque. Uma venda
                // concorrente fica inteira antes ou depois dela, nunca perdida no meio.
                _dbConnection.RunInTransaction(() =>
                {
                    var deltasPendentes = _dbConnection.Query<StockMovement>(
                            "SELECT * FROM StockMovement WHERE IsSynced = 0 AND StoreId = ?",
                            storeId)
                        .GroupBy(m => m.ProductId)
                        .ToDictionary(g => g.Key, g => g.Sum(DeltaDoMovimento));

                    if (categories != null)
                    {
                        foreach (var cat in categories)
                            _dbConnection.InsertOrReplace(cat);
                    }

                    foreach (var prod in products)
                    {
                        double quantidade = stockMap.TryGetValue(prod.Id, out var saldo)
                            ? saldo.Quantity
                            : 0;
                        double minimo = saldo?.MinStock ?? 0;
                        if (deltasPendentes.TryGetValue(prod.Id, out var pendente))
                            quantidade += pendente;

                        var existing = _dbConnection.Find<Product>(prod.Id);
                        if (existing != null)
                        {
                            _dbConnection.Execute(
                                "UPDATE Product SET Name=?, Barcode=?, PriceSale=?, PriceCost=?, Type=?, " +
                                "UnitMeasure=?, Active=?, ImageUrl=?, CategoryId=?, TenantId=?, UpdatedAt=?, " +
                                "LocalStockQuantity=?, MinStock=? WHERE Id=?",
                                prod.Name, prod.Barcode, prod.PriceSale, prod.PriceCost, prod.Type,
                                prod.UnitMeasure, prod.Active, prod.ImageUrl, prod.CategoryId, prod.TenantId,
                                DateTime.Now, quantidade, minimo, prod.Id);
                        }
                        else if (prod.Active)
                        {
                            prod.LocalStockQuantity = quantidade;
                            prod.MinStock = minimo;
                            _dbConnection.Insert(prod);
                        }
                    }

                    if (breadConfigs.Count > 0)
                    {
                        _dbConnection.Execute(
                            "DELETE FROM BreadConfig WHERE StoreId = ? AND Id <> ?",
                            storeId, breadConfigs[0].Id);
                        _dbConnection.InsertOrReplace(breadConfigs[0]);
                    }
                });

                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                System.Diagnostics.Debug.WriteLine($"[Pull Error]: {ex.Message}");
                return false;
            }
        }

        private static double DeltaDoMovimento(StockMovement movimento)
        {
            double quantidade = Math.Abs(movimento.Quantity);
            if (string.Equals(movimento.Type, "ENTRADA", StringComparison.OrdinalIgnoreCase))
                return quantidade;
            if (string.Equals(movimento.Type, "SAIDA", StringComparison.OrdinalIgnoreCase))
                return -quantidade;
            return 0;
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
