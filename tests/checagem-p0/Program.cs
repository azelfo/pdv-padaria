using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdvPadaria.Services;

class Program
{
    static int falhas = 0;
    static bool Lanca<T>(Action acao) where T : Exception
    {
        try { acao(); return false; } catch (T) { return true; } catch { return false; }
    }

    static void Checa(string nome, bool ok, string detalhe = "")
    {
        Console.WriteLine($"  {(ok ? "PASSA" : "FALHA")}  {nome}{(ok ? "" : "  <- " + detalhe)}");
        if (!ok) falhas++;
    }

    // O instalador agora e gravado com nome sorteado; a checagem passou a perguntar se
    // ALGUM arquivo de atualizacao apareceu, em vez de vigiar um caminho fixo que o
    // codigo nao usa mais (e que faria os dois testes de disco passarem por engano).
    static bool GravouAlgumInstalador() =>
        Directory.GetFiles(Path.GetTempPath(), "PdvUpdate_*.exe").Length > 0;

    // Sobe procurando docs/version.json: o sha estava fixo aqui e envelhecia a cada
    // lancamento, fazendo o teste da URL passar pelo motivo errado (o hash e que barrava).
    static string ShaPublicado()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "version.json")))
            dir = dir.Parent;
        if (dir == null) throw new FileNotFoundException("docs/version.json nao encontrado");
        var json = File.ReadAllText(Path.Combine(dir.FullName, "docs", "version.json"));
        var m = System.Text.RegularExpressions.Regex.Match(json, @"""sha256""\s*:\s*""([0-9a-fA-F]{64})""");
        return m.Success ? m.Groups[1].Value : throw new InvalidOperationException("sha256 ausente");
    }

    static async Task<int> Main()
    {
        // Isola os arquivos de identidade: ResolverAsync grava loja-identidade.txt e
        // estoque-loja.txt, e rodar esta checagem num caixa de loja reescrevia os de verdade.
        var pastaTeste = Path.Combine(Path.GetTempPath(), "pdv-checagem-" + Path.GetRandomFileName());
        Directory.CreateDirectory(pastaTeste);
        Environment.SetEnvironmentVariable("PDV_DADOS_DIR", pastaTeste);

        string urlOficial = "https://raw.githubusercontent.com/azelfo/pdv-padaria/main/PdvPadaria/Output/Setup_PadariaVenancio.exe";
        string shaReal = ShaPublicado();

        Console.WriteLine("\n== P0-1: instalador verificado ==");

        foreach (var f in Directory.GetFiles(Path.GetTempPath(), "PdvUpdate_*.exe"))
            try { File.Delete(f); } catch { }
        bool r1 = await UpdateService.DownloadAndInstallAsync(new UpdateInfo {
            Version = "9.9.9", Url = "https://exemplo-malicioso.invalido/setup.exe", Sha256 = shaReal });
        Checa("endereco fora do repositorio oficial e recusado", !r1, "aceitou endereco estranho");
        Checa("nada foi gravado no disco", !GravouAlgumInstalador(), "gravou arquivo mesmo recusando");

        bool r2 = await UpdateService.DownloadAndInstallAsync(new UpdateInfo {
            Version = "9.9.9", Url = urlOficial,
            Sha256 = "0000000000000000000000000000000000000000000000000000000000000000" });
        Checa("impressao digital que nao confere e recusada", !r2, "executou binario nao conferido");
        Checa("arquivo adulterado nao fica no disco", !GravouAlgumInstalador(), "deixou o arquivo adulterado no disco");

        Console.WriteLine("\n== P0-3: estado da sessao morre no logout ==");
        await StoreIdentityService.ResolverAsync(string.Empty);
        string antes = StoreIdentityService.StoreId;
        Checa("identidade resolvida na 1a sessao", !string.IsNullOrEmpty(antes), "nao resolveu (offline?)");

        StoreIdentityService.Encerrar();
        Checa("StoreId zerado apos Encerrar", string.IsNullOrEmpty(StoreIdentityService.StoreId), StoreIdentityService.StoreId);
        Checa("TokenInvalido zerado", !StoreIdentityService.TokenInvalido);
        Checa("TokenAusente zerado", !StoreIdentityService.TokenAusente);

        await StoreIdentityService.ResolverAsync(string.Empty);
        Checa("2a sessao reconsulta e resolve de novo", StoreIdentityService.StoreId == antes,
              $"antes={antes} depois={StoreIdentityService.StoreId}");

        Console.WriteLine("\n== cancelamento: so a loja dona pode estornar ==");
        Checa("venda desta loja pode ser cancelada aqui",
              StoreIdentityService.PertenceAEstaMaquina(antes));
        Checa("venda de OUTRA loja e barrada",
              !StoreIdentityService.PertenceAEstaMaquina("e33ec1a1-a041-4fae-aefa-625bc518772f"),
              "deixaria cancelar venda de outra loja -> travaria a fila de envio");
        Checa("registro sem loja e barrado",
              !StoreIdentityService.PertenceAEstaMaquina(null));
        Checa("loja em branco e barrada",
              !StoreIdentityService.PertenceAEstaMaquina("   "));
        // A identidade da MAQUINA sobrevive ao logout de proposito: e ela que permite o
        // caixa abrir e operar sem internet no dia seguinte. O que o logout descarta e a
        // SESSAO. Se esta asserção passar a falhar, o caixa perdeu a capacidade de saber
        // de que loja ele e quando abrir offline.
        StoreIdentityService.Encerrar();
        Checa("apos o logout a maquina ainda sabe de que loja e",
              StoreIdentityService.PertenceAEstaMaquina(antes),
              "perdeu a identidade da maquina; abriria offline sem saber a loja");

        Console.WriteLine("\n== pull: dados de outra loja nao entram neste caixa ==");
        SQLitePCL.Batteries_V2.Init();
        var db = Path.Combine(pastaTeste, "vazio.db");
        using (var c = new SQLite.SQLiteConnection(db))
        {
            c.CreateTable<PdvPadaria.Models.Product>();
            c.CreateTable<PdvPadaria.Models.Category>();
            c.CreateTable<PdvPadaria.Models.BreadConfig>();
            c.CreateTable<PdvPadaria.Models.Sale>();
            c.CreateTable<PdvPadaria.Models.SaleItem>();
            c.CreateTable<PdvPadaria.Models.StockMovement>();
            c.CreateTable<PdvPadaria.Models.AppliedOwnerAdjustment>();
        }
        var sessaoDoTeste = new Sessao("u", "ATENDENTE", "loja-que-nao-e-esta", "tenant-qualquer");
        using (var conn = new SQLite.SQLiteConnection(db))
        using (var sync = new SyncService(conn, sessaoDoTeste))
        {
            // O token desta maquina resolve para a loja real. Pedir o pull dizendo ser OUTRA
            // loja tem que ser recusado: sem esta guarda, o catalogo e o estoque da loja do
            // token seriam gravados sob o nome da loja errada -- o incidente de 20/08.
            bool ok = await sync.PullUpdatesAsync("tenant-qualquer", "loja-que-nao-e-esta");
            Checa("pull mentindo a loja e recusado", !ok, "gravou dados de outra loja");
            Checa("a recusa explica o motivo", sync.LastError.Contains("opera como"), sync.LastError);
        }
        using (var c = new SQLite.SQLiteConnection(db))
        {
            int prod = c.ExecuteScalar<int>("SELECT COUNT(*) FROM Product");
            Checa("nada foi gravado no banco", prod == 0, $"{prod} produto(s) gravados");
        }

        Console.WriteLine("\n== venda da fila nao troca de loja no caminho ==");
        // Venda feita OFFLINE numa loja, que sobe depois que a maquina passou a operar por
        // OUTRA, virava venda da segunda loja em silencio: o servidor carimbava o storeId a
        // partir do token. Medido em 26/08 -- venda da Centro chegou como Japao.
        // Agora e recusada e fica na fila ate a maquina voltar a ser a loja dela.
        // Este teste nunca grava na nuvem: o envio e rejeitado antes disso.
        const string OUTRA_LOJA = "e33ec1a1-a041-4fae-aefa-625bc518772f";
        using (var c = new SQLite.SQLiteConnection(db))
        {
            c.Execute("DELETE FROM Sale");
            c.Insert(new PdvPadaria.Models.Sale {
                Id = "checagem-outra-loja", StoreId = OUTRA_LOJA,
                TenantId = "927cfa8e-655a-4307-a50a-806c72d99e4f", UserId = "u",
                SaleDate = DateTime.Now, Subtotal = 1, Total = 1,
                PaymentMethod = "DINHEIRO", PaymentStatus = "APROVADO", IsSynced = false });
        }
        using (var conn = new SQLite.SQLiteConnection(db))
        using (var sync = new SyncService(conn, new Sessao("u", "ATENDENTE", OUTRA_LOJA, "t")))
        {
            bool subiu = await sync.PushSalesAsync("t", OUTRA_LOJA);
            Checa("venda de outra loja e recusada", !subiu,
                  "subiu carimbada na loja errada");
            Checa("a recusa explica o que fazer", sync.LastError.Contains("OUTRA loja"),
                  sync.LastError);
        }
        using (var c = new SQLite.SQLiteConnection(db))
            Checa("a venda continua na fila",
                  c.ExecuteScalar<int>("SELECT COUNT(*) FROM Sale WHERE IsSynced=0") == 1,
                  "venda sumiu da fila sem ter subido");

        Console.WriteLine("\n== token recusado continua recusado depois do logout ==");
        StoreIdentityService.Encerrar();
        File.WriteAllText(Path.Combine(pastaTeste, "caixa-token.dat"), "token-que-nao-existe");
        await StoreIdentityService.ResolverAsync(string.Empty);
        Checa("nuvem recusou o token", StoreIdentityService.TokenInvalido, "nao marcou como invalido");
        StoreIdentityService.Encerrar();
        // Sair do sistema não conserta credencial recusada. Zerar o veredito aqui fazia o
        // aviso sumir num logout+login sem internet, e o caixa voltava a operar calado.
        Checa("o veredito sobrevive ao logout", StoreIdentityService.TokenInvalido,
              "esqueceu que o token foi recusado; abriria offline sem aviso");

        Console.WriteLine("\n== etapa 1: a sessao e imutavel e unica ==");
        var s1 = new Sessao("u-1", "ATENDENTE", "loja-a", "rede-1");
        var s2 = new Sessao("u-1", "ATENDENTE", "loja-a", "rede-1");
        Checa("cada sessao tem geracao propria", s1.Geracao != s2.Geracao,
              "duas sessoes com a mesma geracao: resposta atrasada de uma passaria pela outra");
        Checa("geracao nao vem vazia", !string.IsNullOrWhiteSpace(s1.Geracao));
        // Trocar de loja tem que ser encerrar uma sessao e abrir outra, nunca mutar esta.
        foreach (var prop in new[] { "LojaId", "RedeId", "UsuarioId", "Papel", "Geracao" })
            Checa($"{prop} nao tem setter", typeof(Sessao).GetProperty(prop)?.SetMethod == null,
                  "sessao mutavel: da para trocar a loja sem passar por login");
        Checa("guarda o momento de abertura", s1.AbertaEm > DateTime.MinValue);

        Console.WriteLine("\n== etapa 3: o escopo desfaz a sessao ==");
        await StoreIdentityService.ResolverAsync(string.Empty);   // identidade resolvida
        var escopo = new EscopoDeSessao(new Sessao("u-1", "ATENDENTE", "loja-a", "rede-1"));
        Checa("escopo nasce ativo", !escopo.Encerrado);

        escopo.Dispose();
        Checa("Dispose encerra o escopo", escopo.Encerrado);
        // A limpeza deixa de depender de alguem lembrar de chamar Encerrar() no botao de
        // logout: fechar pelo X passa a descartar o escopo pelo mesmo caminho.
        Checa("Dispose limpa a identidade da sessao",
              string.IsNullOrEmpty(StoreIdentityService.StoreId),
              "estado da sessao anterior sobreviveu ao descarte do escopo");
        Checa("operar num escopo encerrado e recusado",
              Lanca<ObjectDisposedException>(() => escopo.GarantirAtivo()),
              "escopo encerrado ainda aceita operacao");
        escopo.Dispose();
        Checa("Dispose duas vezes nao quebra", escopo.Encerrado);

        Console.WriteLine("\n== etapa 5: encerrar tenta subir o que ficou para tras ==");
        var limite = TimeSpan.FromMilliseconds(300);

        var esc1 = new EscopoDeSessao(new Sessao("u", "ATENDENTE", "loja-a", "rede-1"));
        var res1 = await esc1.EncerrarAsync(() => Task.FromResult(true), limite);
        Checa("envio ok: encerra sem pendencia", res1 == ResultadoDoEncerramento.Enviado && esc1.Encerrado);

        var esc2 = new EscopoDeSessao(new Sessao("u", "ATENDENTE", "loja-a", "rede-1"));
        var res2 = await esc2.EncerrarAsync(() => Task.FromResult(false), limite);
        // Falhar em subir NAO pode impedir de fechar -- travaria o caixa sem internet.
        // Mas o chamador precisa saber, para avisar quem esta fechando.
        Checa("envio falhou: encerra assim mesmo, mas avisa",
              res2 == ResultadoDoEncerramento.FicouPendente && esc2.Encerrado);

        var esc3 = new EscopoDeSessao(new Sessao("u", "ATENDENTE", "loja-a", "rede-1"));
        var relogio = System.Diagnostics.Stopwatch.StartNew();
        var res3 = await esc3.EncerrarAsync(async () => { await Task.Delay(10000); return true; }, limite);
        relogio.Stop();
        Checa("envio pendurado nao trava o fechamento",
              res3 == ResultadoDoEncerramento.FicouPendente && relogio.ElapsedMilliseconds < 3000,
              $"levou {relogio.ElapsedMilliseconds}ms");
        Checa("mesmo estourando o tempo, o escopo e encerrado", esc3.Encerrado);

        var esc4 = new EscopoDeSessao(new Sessao("u", "ATENDENTE", "loja-a", "rede-1"));
        var res4 = await esc4.EncerrarAsync(() => throw new InvalidOperationException("rede caiu"), limite);
        Checa("erro no envio nao escapa para quem esta fechando",
              res4 == ResultadoDoEncerramento.FicouPendente && esc4.Encerrado);

        Console.WriteLine("\n== etapa 4: nao existe sincronizador sem sessao ==");
        // A garantia e de COMPILACAO: nao ha construtor sem Sessao, entao consulta sem
        // contexto deixa de ser esquecimento e passa a nao compilar. O que da para afirmar
        // em execucao e que passar nulo e recusado na hora, em vez de virar um objeto que
        // grava sem saber por qual loja.
        Checa("nenhum construtor de SyncService dispensa a sessao",
              typeof(SyncService).GetConstructors()
                  .All(ctor => ctor.GetParameters().Any(par => par.ParameterType == typeof(Sessao))),
              "da para montar um sincronizador sem contexto de sessao");
        using (var c2 = new SQLite.SQLiteConnection(Path.Combine(pastaTeste, "vazio.db")))
        {
            Checa("sessao nula e recusada na hora",
                  Lanca<ArgumentNullException>(() => { using var _ = new SyncService(c2, null!); }),
                  "aceitou sessao nula");
        }

        Console.WriteLine("\n== etapa 6: configuracao nao atravessa a sessao ==");
        string urlAntes = EnvService.Get("SUPABASE_URL");
        Checa("config carregada", !string.IsNullOrEmpty(urlAntes));

        int cargasAntes = EnvService.Carregamentos;
        var esc5 = new EscopoDeSessao(new Sessao("u", "ATENDENTE", "loja-a", "rede-1"));
        esc5.Dispose();
        string urlDepois = EnvService.Get("SUPABASE_URL");

        // Contar as cargas e o que distingue "releu" de "continuou em cache". Sem isto o
        // teste passaria mesmo sem implementacao nenhuma -- o cache antigo devolveria o
        // mesmo valor e o verde seria mentira.
        Checa("descartar o escopo derruba o cache de configuracao",
              EnvService.Carregamentos > cargasAntes,
              "config da sessao anterior continuou em memoria");
        Checa("a releitura nao perde nada", urlDepois == urlAntes,
              "caixa abriria a sessao seguinte sem saber falar com a nuvem");

        try { Directory.Delete(pastaTeste, true); } catch { }
        Console.WriteLine($"\n  {(falhas == 0 ? "tudo passou" : falhas + " falharam")}");
        return falhas;
    }
}
