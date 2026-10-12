using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using PdvPadaria.Models;
using PdvPadaria.Services;
using SQLite;

// Apenas SQLite em memoria, credenciais ficticias e HTTP em loopback; nunca le o .env real.
class Program
{
    private static readonly DateTime Contagem = new DateTime(2026, 10, 12, 10, 0, 0);
    private static string pasta = string.Empty;
    private static int verificacoes;

    static async Task Main()
    {
        SQLitePCL.Batteries_V2.Init();
        pasta = Path.Combine(Path.GetTempPath(), "pdv-regressao-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pasta);
        Environment.SetEnvironmentVariable("PDV_DADOS_DIR", pasta);
        try
        {
            using var servidor = new ServidorLocal();
            await Ajustes(servidor);
            await TrocaDurantePull(servidor);
            await EncerramentoDuranteAck(servidor);
            await TokenCapturado(servidor);
            await BarreiraDeEfeitos();
            Console.WriteLine($"PASSA: {verificacoes} verificacoes locais de sincronizacao.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PDV_DADOS_DIR", null);
            var destino = Path.GetFullPath(pasta);
            if (destino.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(destino, true);
        }
    }

    static void Checa(bool ok, string nome)
    {
        if (!ok) throw new InvalidOperationException(nome);
        verificacoes++;
    }

    static void Configurar(ServidorLocal servidor, string token = "token-a-ficticio")
    {
        var config = (Dictionary<string, string>)typeof(EnvService)
            .GetField("_envVars", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        config.Clear();
        config["SUPABASE_URL"] = servidor.Url;
        config["SUPABASE_ANON_KEY"] = "anon-ficticia";
        typeof(EnvService).GetField("_loaded", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, true);
        File.WriteAllText(Path.Combine(pasta, "caixa-token.dat"), token);
    }

    static SQLiteConnection Banco()
    {
        var db = new SQLiteConnection(":memory:");
        db.CreateTable<Product>(); db.CreateTable<Sale>(); db.CreateTable<SaleItem>();
        db.CreateTable<StockMovement>(); db.CreateTable<AppliedOwnerAdjustment>();
        db.CreateTable<Category>(); db.CreateTable<BreadConfig>();
        db.Insert(new Product { Id = "p", Name = "Pao", TenantId = "t", LocalStockQuantity = 50 });
        return db;
    }

    static void Movimento(SQLiteConnection db, string tipo, double quantidade, DateTime em,
        string? venda = null, string motivo = "AJUSTE_MANUAL", string loja = "a") =>
        db.Insert(new StockMovement { ProductId = "p", StoreId = loja, TenantId = "t", Type = tipo,
            Quantity = quantidade, CreatedAt = em, SaleId = venda, Reason = motivo });

    static void Venda(SQLiteConnection db, DateTime em, bool cancelada = false)
    {
        db.Insert(new Sale { Id = "v", StoreId = "a", TenantId = "t", SaleDate = em,
            PaymentStatus = cancelada ? "CANCELADO" : "APROVADO" });
        db.Insert(new SaleItem { SaleId = "v", ProductId = "p", Quantity = 20 });
    }

    static CadastrosDto Cadastros() => new CadastrosDto {
        StoreId = "a", TenantId = "t", Categories = new List<Category>(),
        Products = new List<Product> { new Product { Id = "p", Name = "Pao", TenantId = "t" } },
        StoreProducts = new List<StoreProductDto> { new StoreProductDto {
            ProductId = "p", StoreId = "a", Quantity = 123, UpdatedAt = Contagem.AddMinutes(30) } },
        BreadConfigs = new List<BreadConfig>(),
        OwnerAdjustments = new List<OwnerStockAdjustmentDto> { new OwnerStockAdjustmentDto {
            Id = "aj", ProductId = "p", Quantity = 100, CreatedAt = Contagem, MinStock = 5 } }
    };

    static async Task Puxar(ServidorLocal servidor, SyncService sync, CadastrosDto dados, bool semear = false)
    {
        var pull = sync.PullUpdatesAsync("t", "a", semear);
        var req = await servidor.Receber();
        Checa(req.Request.Url!.AbsolutePath.EndsWith("/pull_cadastros"), "RPC de leitura esperada");
        await ServidorLocal.Responder(req, JsonConvert.SerializeObject(dados,
            new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() }));
        Checa(await pull.WaitAsync(TimeSpan.FromSeconds(5)), "pull deve funcionar: " + sync.LastError);
    }

    static async Task Ajustes(ServidorLocal servidor)
    {
        foreach (var caso in new[] { "reposicao", "perda", "estorno-anterior", "venda", "legado", "cancelada", "escopo", "semeadura" })
        {
            Configurar(servidor);
            var db = Banco();
            using var sync = new SyncService(db, new Sessao("u", "DONO", "a", "t"));
            double esperado = 100;
            switch (caso)
            {
                case "reposicao": Movimento(db, "ENTRADA", 20, Contagem.AddMinutes(1)); esperado = 120; break;
                case "perda": Movimento(db, "SAIDA", 10, Contagem.AddMinutes(1)); esperado = 90; break;
                case "estorno-anterior":
                    Venda(db, Contagem.AddMinutes(-1), true);
                    Movimento(db, "SAIDA", 20, Contagem.AddMinutes(-1), "v", "VENDA");
                    Movimento(db, "ENTRADA", 20, Contagem.AddMinutes(1), "v", "CANCELAMENTO_VENDA");
                    esperado = 120; break;
                case "venda":
                case "cancelada":
                    Venda(db, Contagem.AddMinutes(1), caso == "cancelada");
                    Movimento(db, "SAIDA", 20, Contagem.AddMinutes(1), "v", "VENDA");
                    if (caso == "cancelada") Movimento(db, "ENTRADA", 20, Contagem.AddMinutes(2), "v", "CANCELAMENTO_VENDA");
                    esperado = caso == "venda" ? 80 : 100; break;
                case "legado": Venda(db, Contagem.AddMinutes(1)); esperado = 80; break;
                case "escopo":
                    Movimento(db, "ENTRADA", 90, Contagem.AddMinutes(1), loja: "b");
                    Movimento(db, "ENTRADA", 70, Contagem.AddMinutes(-1));
                    Movimento(db, "ENTRADA", 200, Contagem.AddMinutes(1), motivo: "AJUSTE_DONO"); break;
                case "semeadura": Movimento(db, "ENTRADA", 20, Contagem.AddMinutes(1)); esperado = 143; break;
            }
            var dados = Cadastros();
            await Puxar(servidor, sync, dados, caso == "semeadura");
            Checa(db.Find<Product>("p").LocalStockQuantity == esperado, "saldo: " + caso);
            Checa(db.Find<AppliedOwnerAdjustment>("aj") != null, "ajuste confirmado: " + caso);
            if (caso != "semeadura") Checa(db.Find<Product>("p").MinStock == 5, "minimo aplicado");
            db.Execute("UPDATE Product SET LocalStockQuantity = LocalStockQuantity + 3");
            await Puxar(servidor, sync, dados);
            Checa(db.Find<Product>("p").LocalStockQuantity == esperado + 3, "retry nao reaplica: " + caso);
        }
    }

    static async Task TrocaDurantePull(ServidorLocal servidor)
    {
        Configurar(servidor);
        var db = Banco();
        using var escopo = new EscopoDeSessao(new Sessao("u", "DONO", "a", "t"));
        using var sync = new SyncService(db, escopo.Sessao);
        var pull = sync.PullUpdatesAsync("t", "a");
        var req = await servidor.Receber();
        escopo.Dispose();
        Configurar(servidor, "token-b-ficticio");
        db.Execute("UPDATE Product SET LocalStockQuantity=50");
        await ServidorLocal.Responder(req, JsonConvert.SerializeObject(Cadastros()));
        Checa(!await pull.WaitAsync(TimeSpan.FromSeconds(5)), "pull antigo deve ser cancelado");
        Checa(db.Find<Product>("p").LocalStockQuantity == 50, "resposta antiga nao altera loja nova");
        Checa(!await sync.PushStockSnapshotAsync("t", "a"), "sessao encerrada nao publica estoque");
        Checa(!await sync.PushSalesAsync("t", "a"), "sessao encerrada nao confirma nem fila vazia");

        // Mesmo uma resposta ja lida antes do cancelamento nao pode atravessar a barreira de gravacao.
        var aplicar = typeof(SyncService).GetMethod("ApplyOwnerAdjustments", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var aplicado = (bool)aplicar.Invoke(sync, new object?[] { "t", "a", Cadastros().OwnerAdjustments, null })!;
        Checa(!aplicado && db.Find<Product>("p").LocalStockQuantity == 50, "ajuste tardio recusado na transacao");
    }

    static async Task EncerramentoDuranteAck(ServidorLocal servidor)
    {
        Configurar(servidor);
        var db = Banco();
        Venda(db, Contagem);
        Movimento(db, "SAIDA", 20, Contagem, "v", "VENDA");
        using var escopo = new EscopoDeSessao(new Sessao("u", "DONO", "a", "t"));
        using var sync = new SyncService(db, escopo.Sessao);
        var envio = sync.PushSalesAsync("t", "a");
        var req = await servidor.Receber();
        var resultado = await escopo.EncerrarAsync(() => envio, TimeSpan.FromMilliseconds(20));
        await ServidorLocal.Responder(req, "{\"sales\":1,\"items\":1,\"movements\":1}");
        Checa(resultado == ResultadoDoEncerramento.FicouPendente, "timeout conserva aviso de pendencia");
        Checa(!await envio.WaitAsync(TimeSpan.FromSeconds(5)), "envio em voo cancelado");
        Checa(!db.Find<Sale>("v").IsSynced, "ACK tardio nao apaga venda da fila");
        Checa(db.Table<StockMovement>().Where(m => !m.IsSynced).Count() == 1, "ACK tardio nao apaga movimento");
    }

    static async Task TokenCapturado(ServidorLocal servidor)
    {
        Configurar(servidor);
        using var sync = new SyncService(Banco(), new Sessao("u", "DONO", "a", "t"));
        File.WriteAllText(Path.Combine(pasta, "caixa-token.dat"), "token-b-ficticio");
        var envio = sync.PushStockSnapshotAsync("t", "a");
        var req = await servidor.Receber();
        var json = JObject.Parse(await new StreamReader(req.Request.InputStream).ReadToEndAsync());
        Checa(json["p_token"]!.ToString() == "token-a-ficticio", "sync nao troca de credencial entre etapas");
        await ServidorLocal.Responder(req, "{\"stock\":1}");
        Checa(await envio, "snapshot legado continua funcionando");
    }

    static async Task BarreiraDeEfeitos()
    {
        var sessao = new Sessao("u", "DONO", "a", "t");
        using var entrou = new ManualResetEventSlim();
        using var liberar = new ManualResetEventSlim();
        using var encerrando = new ManualResetEventSlim();
        var gravacao = Task.Run(() => sessao.ExecutarSeAtiva(() => { entrou.Set(); liberar.Wait(); }));
        Checa(entrou.Wait(TimeSpan.FromSeconds(5)), "efeito iniciou");
        var fim = Task.Run(() => { encerrando.Set(); sessao.Encerrar(); });
        Checa(encerrando.Wait(TimeSpan.FromSeconds(5)), "logout iniciou");
        Checa(!fim.Wait(TimeSpan.FromMilliseconds(30)), "logout espera transacao que ja iniciou");
        liberar.Set();
        await Task.WhenAll(gravacao, fim).WaitAsync(TimeSpan.FromSeconds(5));
        bool recusou = false;
        try { sessao.ExecutarSeAtiva(() => throw new Exception("efeito nao deveria executar")); }
        catch (ObjectDisposedException) { recusou = true; }
        Checa(recusou, "logout impede novos efeitos");
    }

    sealed class ServidorLocal : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        public string Url { get; }
        public ServidorLocal()
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            Url = $"http://127.0.0.1:{port}";
            listener.Prefixes.Add(Url + "/");
            listener.Start();
        }
        public Task<HttpListenerContext> Receber() => listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
        public static async Task Responder(HttpListenerContext ctx, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            try
            {
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            catch (HttpListenerException) { } // O cliente cancelado ja pode ter fechado o socket.
            finally { ctx.Response.Close(); }
        }
        public void Dispose() => listener.Close();
    }
}
