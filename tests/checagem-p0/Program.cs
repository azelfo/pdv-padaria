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

    static string Temp => Path.Combine(Path.GetTempPath(), "Setup_PadariaVenancio_update.exe");

    static async Task<int> Main()
    {
        string urlOficial = "https://raw.githubusercontent.com/azelfo/pdv-padaria/main/PdvPadaria/Output/Setup_PadariaVenancio.exe";
        string shaReal = "481438b196adc6cd7887f97cc4bab1300a2471e357bdbcb9450500c310cb2640";

        Console.WriteLine("\n== P0-1: instalador verificado ==");

        if (File.Exists(Temp)) File.Delete(Temp);
        bool r1 = await UpdateService.DownloadAndInstallAsync(new UpdateInfo {
            Version = "9.9.9", Url = "https://exemplo-malicioso.invalido/setup.exe", Sha256 = shaReal });
        Checa("endereco fora do repositorio oficial e recusado", !r1, "aceitou endereco estranho");
        Checa("nada foi gravado no disco", !File.Exists(Temp), "gravou arquivo mesmo recusando");

        bool r2 = await UpdateService.DownloadAndInstallAsync(new UpdateInfo {
            Version = "9.9.9", Url = urlOficial,
            Sha256 = "0000000000000000000000000000000000000000000000000000000000000000" });
        Checa("impressao digital que nao confere e recusada", !r2, "executou binario nao conferido");
        Checa("arquivo adulterado nao chega ao disco", !File.Exists(Temp), "gravou o arquivo antes de conferir");

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

        Console.WriteLine($"\n  {(falhas == 0 ? "tudo passou" : falhas + " falharam")}");
        return falhas;
    }
}
