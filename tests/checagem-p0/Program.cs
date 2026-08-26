using System;
using System.IO;
using System.Threading.Tasks;
using PdvPadaria.Services;

class Program
{
    static int falhas = 0;
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
        using (var conn = new SQLite.SQLiteConnection(db))
        using (var sync = new SyncService(conn))
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

        try { Directory.Delete(pastaTeste, true); } catch { }
        Console.WriteLine($"\n  {(falhas == 0 ? "tudo passou" : falhas + " falharam")}");
        return falhas;
    }
}
