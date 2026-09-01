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

        /// <summary>Sessão dona deste sincronizador. Sem ela não existe instância.</summary>
        public Sessao Sessao { get; }

        public SyncService(SQLiteConnection dbConnection, Sessao sessao)
        {
            // Exigir a sessão no construtor é o que transforma "lembrar de passar a loja"
            // numa regra do compilador: não há como montar um sincronizador sem contexto.
            Sessao = sessao ?? throw new ArgumentNullException(nameof(sessao));

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
        private static string? ErroDaResposta(string corpo, params string[] camposEsperados)
        {
            if (string.IsNullOrWhiteSpace(corpo))
                return "A nuvem nao confirmou o envio. As operacoes continuam na fila local.";

            Newtonsoft.Json.Linq.JObject? obj;
            try
            {
                obj = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(corpo);
            }
            catch
            {
                return "A nuvem devolveu uma resposta invalida. As operacoes continuam na fila local.";
            }

            if (obj == null)
                return "A nuvem devolveu uma resposta invalida. As operacoes continuam na fila local.";

            string codigo = obj["error"]?.ToString() ?? string.Empty;
            if (codigo == "invalid_token")
            {
                StoreIdentityService.MarcarTokenInvalido();
                return "A credencial de sincronizacao desta maquina nao vale mais. " +
                       "Enquanto isso, NENHUMA venda e NENHUM estoque deste caixa sobe para a nuvem. " +
                       "Para resolver: com a internet funcionando, saia e entre de novo. " +
                       "O caixa renova a credencial sozinho.";
            }

            if (codigo == "venda_de_outra_loja")
                return "Ha venda na fila que foi feita em OUTRA loja. Ela nao pode subir " +
                       "enquanto este caixa estiver operando pela loja atual — subiria " +
                       "carimbada na loja errada. Entre com o usuario da loja onde essa " +
                       "venda foi feita para ela subir, e depois volte.";

            if (!string.IsNullOrEmpty(codigo))
                return $"A nuvem recusou o envio: {codigo}";

            foreach (string campo in camposEsperados)
            {
                var valor = obj[campo];
                if (valor == null || (valor.Type != Newtonsoft.Json.Linq.JTokenType.Integer
                                      && valor.Type != Newtonsoft.Json.Linq.JTokenType.Float))
                    return "A nuvem devolveu uma resposta incompleta. As operacoes continuam na fila local.";
            }

            return null;
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
                LastError = "Faltam SUPABASE_URL ou SUPABASE_ANON_KEY no arquivo .env desta maquina.";
                System.Diagnostics.Debug.WriteLine("[Push Error]: Credenciais do Supabase ausentes no .env");
                return false;
            }

            try
            {
                // 1. Busca vendas locais que não foram sincronizadas
                var pendingSales = _dbConnection.Table<Sale>()
                    .Where(s => !s.IsSynced && s.StoreId == storeId)
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
                    "SELECT * FROM StockMovement WHERE IsSynced = 0 AND StoreId = ? " +
                    "AND (SaleId IS NULL OR SaleId = '')", storeId);
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
                // Sem isto o caixa mostrava "Falha na sincronizacao:" e nada depois: erro
                // sem motivo, que nao da ao operador nem como descrever o que aconteceu.
                if (string.IsNullOrEmpty(LastError)) LastError = ex.Message;
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
                    string? erroRpc = ErroDaResposta(corpo, "sales", "items", "movements");
                    if (erroRpc != null)
                    {
                        LastError = erroRpc;
                        System.Diagnostics.Debug.WriteLine($"[Supabase RPC push_vendas recusado]: {corpo}");
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

        #region Push de estoque (protocolo legado)

        /// <summary>
        /// Publica a foto absoluta do estoque local na RPC legada push_estoque.
        /// A loja e derivada do token no servidor; o storeId serve apenas para impedir
        /// que uma maquina ainda sem identidade publique uma foto sem destino conhecido.
        /// </summary>
        public async Task<bool> PushStockSnapshotAsync(string tenantId, string storeId)
        {
            if (string.IsNullOrEmpty(_supabaseUrl) || string.IsNullOrEmpty(_supabaseAnonKey))
            {
                LastError = "Credenciais do Supabase ausentes.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(storeId))
            {
                LastError = "Esta maquina ainda nao esta ligada a uma loja.";
                return false;
            }

            string storeToken = StoreIdentityService.TokenAtual();
            if (string.IsNullOrWhiteSpace(storeToken))
            {
                LastError = "Esta maquina ainda nao tem credencial de sincronizacao. " +
                            "Entre com o usuario desta loja estando conectado a internet.";
                return false;
            }

            try
            {
                var products = _dbConnection.Table<Product>()
                    .Where(p => p.TenantId == tenantId)
                    .ToList();
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
                var content = new StringContent(
                    JsonConvert.SerializeObject(body, settings), Encoding.UTF8, "application/json");

                var url = $"{_supabaseUrl.TrimEnd('/')}/rest/v1/rpc/push_estoque";
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = content;
                    request.Headers.Add("apikey", _supabaseAnonKey);
                    request.Headers.Add("Authorization", $"Bearer {_supabaseAnonKey}");

                    var response = await _httpClient.SendAsync(request);
                    string corpo = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        LastError = $"Erro HTTP {response.StatusCode} no push_estoque: {corpo}";
                        System.Diagnostics.Debug.WriteLine($"[Supabase RPC push_estoque Error]: {LastError}");
                        return false;
                    }

                    string? erroRpc = ErroDaResposta(corpo, "stock");
                    if (erroRpc != null)
                    {
                        LastError = erroRpc;
                        System.Diagnostics.Debug.WriteLine($"[Supabase RPC push_estoque recusado]: {corpo}");
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
        /// Atualiza os cadastros sem reescrever o saldo local durante o ciclo normal.
        /// A foto da nuvem so vira saldo quando a maquina e vinculada pela primeira vez
        /// ou troca de loja explicitamente.
        /// </summary>
        public async Task<bool> PullUpdatesAsync(
            string tenantId, string storeId, bool semearEstoqueDaNuvem = false)
        {
            if (string.IsNullOrEmpty(_supabaseUrl) || string.IsNullOrEmpty(_supabaseAnonKey))
            {
                LastError = "Credenciais do Supabase ausentes.";
                return false;
            }

            try
            {
                // Uma chamada só, recortada pelo servidor. Antes eram cinco consultas às
                // tabelas, cada uma levando o recorte de rede como parâmetro na URL — ou
                // seja, o recorte era escolha do cliente, e qualquer portador da chave
                // pública lia todas as redes. Aqui a loja vem do TOKEN, igual à escrita.
                var cadastros = await ObterCadastrosAsync(storeId);

                var categories = cadastros?.Categories;
                var products = cadastros?.Products;
                var storeProducts = cadastros?.StoreProducts;
                var breadConfigs = cadastros?.BreadConfigs;

                if (products == null || storeProducts == null || breadConfigs == null)
                {
                    // So preenche se ninguem mais explicou: ObterCadastrosAsync ja pode ter
                    // posto aqui o motivo real (token vencido, por exemplo), que e acionavel.
                    if (string.IsNullOrEmpty(LastError))
                        LastError = "Falha ao carregar cadastro ou estoque da loja.";
                    return false;
                }

                var stockMap = storeProducts
                    .GroupBy(sp => sp.ProductId)
                    .ToDictionary(g => g.Key, g => g.First());

                _dbConnection.RunInTransaction(() =>
                {
                    var deltasPendentes = semearEstoqueDaNuvem
                        ? _dbConnection.Query<StockMovement>(
                                "SELECT * FROM StockMovement WHERE IsSynced = 0 AND StoreId = ?",
                                storeId)
                            .GroupBy(m => m.ProductId)
                            .ToDictionary(g => g.Key, g => g.Sum(DeltaDoMovimento))
                        : new Dictionary<string, double>();

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
                        if (deltasPendentes.TryGetValue(prod.Id, out var deltaPendente))
                            quantidade += deltaPendente;

                        var existing = _dbConnection.Find<Product>(prod.Id);
                        if (existing != null)
                        {
                            _dbConnection.Execute(
                                "UPDATE Product SET Name=?, Barcode=?, PriceSale=?, PriceCost=?, Type=?, " +
                                "UnitMeasure=?, Active=?, ImageUrl=?, CategoryId=?, TenantId=?, UpdatedAt=? WHERE Id=?",
                                prod.Name, prod.Barcode, prod.PriceSale, prod.PriceCost, prod.Type,
                                prod.UnitMeasure, prod.Active, prod.ImageUrl, prod.CategoryId, prod.TenantId,
                                DateTime.Now, prod.Id);

                            if (semearEstoqueDaNuvem)
                            {
                                _dbConnection.Execute(
                                    "UPDATE Product SET LocalStockQuantity=?, MinStock=? WHERE Id=?",
                                    quantidade, minimo, prod.Id);
                            }
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

                if (!ApplyOwnerAdjustments(
                        tenantId, storeId, cadastros!.OwnerAdjustments,
                        snapshotDaSemeadura: semearEstoqueDaNuvem ? stockMap : null))
                    return false;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                System.Diagnostics.Debug.WriteLine($"[Pull Error]: {ex.Message}");
                return false;
            }
        }

        private bool ApplyOwnerAdjustments(
            string tenantId, string storeId,
            List<OwnerStockAdjustmentDto>? ajustes,
            IReadOnlyDictionary<string, StoreProductDto>? snapshotDaSemeadura)
        {
            try
            {
                // Os ajustes vêm no mesmo pacote do catálogo, já recortados pelo servidor.
                if (ajustes == null)
                {
                    LastError = "Falha ao carregar os ajustes de estoque da loja.";
                    return false;
                }
                if (ajustes.Count == 0) return true;

                _dbConnection.RunInTransaction(() =>
                {
                    foreach (var aj in ajustes)
                    {
                        if (string.IsNullOrEmpty(aj.Id)
                            || _dbConnection.Find<AppliedOwnerAdjustment>(aj.Id) != null)
                            continue;

                        bool incorporadoNoSnapshot = snapshotDaSemeadura != null
                            && snapshotDaSemeadura.TryGetValue(aj.ProductId, out var snapshot)
                            && snapshot.UpdatedAt >= aj.CreatedAt;

                        var prod = _dbConnection.Find<Product>(aj.ProductId);
                        // Ajuste posterior a foto baixada ainda precisa ser aplicado; ajuste
                        // anterior ja esta incorporado e e apenas marcado como visto.
                        if (!incorporadoNoSnapshot && prod != null)
                        {
                            double vendidoDepois = _dbConnection.ExecuteScalar<double>(
                                @"SELECT COALESCE(SUM(si.Quantity), 0)
                                    FROM SaleItem si
                                    JOIN Sale s ON s.Id = si.SaleId
                                   WHERE si.ProductId = ?
                                     AND s.StoreId = ?
                                     AND s.PaymentStatus = 'APROVADO'
                                     AND s.SaleDate > ?",
                                aj.ProductId, storeId, aj.CreatedAt);

                            double saldoAnterior = prod.LocalStockQuantity;
                            double saldoFinal = Math.Max(0, aj.Quantity - vendidoDepois);
                            if (aj.MinStock.HasValue)
                                _dbConnection.Execute(
                                    "UPDATE Product SET LocalStockQuantity=?, MinStock=? WHERE Id=?",
                                    saldoFinal, aj.MinStock.Value, aj.ProductId);
                            else
                                _dbConnection.Execute(
                                    "UPDATE Product SET LocalStockQuantity=? WHERE Id=?",
                                    saldoFinal, aj.ProductId);

                            if (Math.Abs(saldoFinal - saldoAnterior) > 0.0001)
                            {
                                _dbConnection.Insert(new StockMovement
                                {
                                    Id = Guid.NewGuid().ToString(),
                                    ProductId = aj.ProductId,
                                    StoreId = storeId,
                                    UserId = aj.CreatedBy ?? string.Empty,
                                    TenantId = tenantId,
                                    Type = saldoFinal >= saldoAnterior ? "ENTRADA" : "SAIDA",
                                    Quantity = Math.Abs(saldoFinal - saldoAnterior),
                                    Reason = "AJUSTE_DONO",
                                    CreatedAt = aj.CreatedAt,
                                    IsSynced = true,
                                    SyncedAt = DateTime.Now,
                                    BalanceBefore = saldoAnterior,
                                    BalanceAfter = saldoFinal
                                });
                            }
                        }

                        _dbConnection.Insert(new AppliedOwnerAdjustment { Id = aj.Id });
                    }
                });
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                System.Diagnostics.Debug.WriteLine($"[ApplyOwnerAdjustments Error]: {ex.Message}");
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

        /// <summary>
        /// Baixa catálogo, estoque, tabela do pão e ajustes do dono numa chamada só,
        /// recortados no servidor a partir do token desta máquina.
        ///
        /// Substitui cinco consultas diretas às tabelas. Aquelas dependiam de o cliente
        /// mandar o filtro certo na URL — e quem esquecesse (ou trocasse) o filtro recebia
        /// as linhas de todas as redes, porque a política de leitura era liberada para
        /// qualquer portador da chave pública. Aqui, esquecer não é possível: a loja é
        /// derivada da credencial, do mesmo jeito que já acontece na escrita.
        /// </summary>
        private async Task<CadastrosDto?> ObterCadastrosAsync(string storeIdEsperado)
        {
            try
            {
                var corpoReq = new StringContent(
                    JsonConvert.SerializeObject(new { p_token = StoreIdentityService.TokenAtual() }),
                    Encoding.UTF8, "application/json");

                using (var request = new HttpRequestMessage(
                    HttpMethod.Post, $"{_supabaseUrl.TrimEnd('/')}/rest/v1/rpc/pull_cadastros"))
                {
                    request.Content = corpoReq;
                    request.Headers.Add("apikey", _supabaseAnonKey);
                    request.Headers.Add("Authorization", $"Bearer {_supabaseAnonKey}");

                    var response = await _httpClient.SendAsync(request);
                    string corpo = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        LastError = $"Erro HTTP {response.StatusCode} no pull_cadastros: {corpo}";
                        return null;
                    }

                    // Mesma armadilha das outras RPCs: recusa vem no corpo, com HTTP 200.
                    string? erro = ErroDaResposta(corpo);
                    if (erro != null)
                    {
                        LastError = erro;
                        return null;
                    }

                    var settings = new JsonSerializerSettings
                    {
                        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                    };
                    var cadastros = JsonConvert.DeserializeObject<CadastrosDto>(corpo, settings);
                    if (cadastros == null) return null;

                    // O servidor devolve a loja que ELE derivou do token. Se nao for a loja
                    // que este caixa acha que e, os dados sao de outra loja: gravar isso aqui
                    // escreveria o catalogo e o estoque de uma loja sob o nome de outra --
                    // exatamente o incidente de 20/08. A resposta ja trazia o dado; faltava
                    // olhar para ele.
                    if (!string.Equals(cadastros.StoreId, storeIdEsperado, StringComparison.OrdinalIgnoreCase))
                    {
                        LastError = $"A nuvem respondeu pela loja {cadastros.StoreId}, mas este caixa " +
                                    $"opera como {storeIdEsperado}. Nada foi gravado. Saia e entre de novo.";
                        return null;
                    }

                    return cadastros;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                System.Diagnostics.Debug.WriteLine($"[pull_cadastros]: {ex.Message}");
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
        public DateTime UpdatedAt { get; set; }
    }

    // Pacote devolvido por pull_cadastros: tudo que o caixa precisa ler, já recortado
    // pela loja do token. Os nomes batem com as colunas do Postgres via camelCase.
    public class CadastrosDto
    {
        // Sem valor inicial DE PROPOSITO. Lista que ja nasce vazia faz chave ausente ou
        // renomeada no JSON virar "esta loja nao tem nada" em vez de erro: o pull segue
        // adiante, o mapa de estoque fica vazio e a semeadura da troca de loja zera o
        // saldo inteiro achando que confirmou. Nulo aqui e o que deixa a guarda funcionar.
        public string StoreId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public List<Category>? Categories { get; set; }
        public List<Product>? Products { get; set; }
        public List<StoreProductDto>? StoreProducts { get; set; }
        public List<BreadConfig>? BreadConfigs { get; set; }
        public List<OwnerStockAdjustmentDto>? OwnerAdjustments { get; set; }
    }

    public class OwnerStockAdjustmentDto
    {
        public string Id { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
        public double Quantity { get; set; }
        public double? MinStock { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
    }

    #endregion
}
