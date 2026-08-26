using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;

namespace PdvPadaria.Services
{
    public class UpdateInfo
    {
        [JsonProperty("version")]
        public string Version { get; set; } = string.Empty;

        [JsonProperty("url")]
        public string Url { get; set; } = string.Empty;

        [JsonProperty("notes")]
        public string Notes { get; set; } = string.Empty;

        // Impressão digital SHA-256 do instalador. Sem ela o caixa não tem como
        // saber se o que baixou é o que foi publicado — e ele executa o arquivo
        // com privilégio de administrador.
        [JsonProperty("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }

    // Verifica e aplica atualizações do PDV a partir de um arquivo version.json hospedado
    // no GitHub Pages do projeto. Fluxo: baixa o installer novo -> fecha o app -> instalador
    // roda silencioso -> reabre o app sozinho (ver [Run]/[Code] em PdvPadaria/setup.iss).
    //
    // IMPORTANTE (bootstrap): só funciona a partir da versão que introduziu este serviço
    // (1.0.8). Máquinas com versão anterior precisam de UMA última instalação manual;
    // depois disso, atualizam sozinhas.
    public static class UpdateService
    {
        private const string VersionUrl = "https://raw.githubusercontent.com/azelfo/pdv-padaria/main/docs/version.json";

        // O endereço do instalador vinha DENTRO do arquivo baixado, então quem
        // controlasse esse arquivo apontava o download para onde quisesse. Fixar o
        // prefixo aqui tira essa escolha de quem publica o JSON.
        private const string PrefixoPermitido = "https://raw.githubusercontent.com/azelfo/pdv-padaria/";

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        // Consulta o version.json remoto. Retorna null se não houver atualização, se a
        // checagem falhar (offline) ou se o JSON vier inválido — nunca lança para o chamador,
        // uma checagem de update jamais deve derrubar o caixa.
        public static async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            try
            {
                var json = await _http.GetStringAsync(VersionUrl);
                var info = JsonConvert.DeserializeObject<UpdateInfo>(json);
                if (info == null || string.IsNullOrWhiteSpace(info.Version) || string.IsNullOrWhiteSpace(info.Url))
                    return null;

                // Sem impressão digital declarada, não oferece a atualização. Recusar é
                // melhor que executar um binário que não dá para conferir: um caixa que
                // não atualiza continua vendendo; um caixa comprometido, não.
                if (info.Sha256 == null || info.Sha256.Trim().Length != 64)
                {
                    Debug.WriteLine("[UpdateService]: version.json sem sha256; atualização ignorada.");
                    return null;
                }

                var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (current == null) return null;

                if (!Version.TryParse(NormalizeVersion(info.Version), out var remote))
                    return null;

                return remote > current ? info : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService.CheckForUpdateAsync Error]: {ex.Message}");
                return null;
            }
        }

        private static string ImpressaoDigital(byte[] dados)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(dados)).Replace("-", string.Empty);
            }
        }

        // "1.0.8" -> "1.0.8.0" (System.Version exige 4 partes para comparar com AssemblyVersion)
        private static string NormalizeVersion(string v)
        {
            var parts = v.Trim().Split('.');
            return parts.Length switch
            {
                1 => $"{v}.0.0.0",
                2 => $"{v}.0.0",
                3 => $"{v}.0",
                _ => v
            };
        }

        // Baixa o instalador e o executa de forma silenciosa; em seguida encerra ESTE processo
        // (libera os arquivos para o instalador sobrescrever) e o instalador reabre o PDV sozinho
        // ao final (ver [Run] com Check: ShouldRelaunchSilently em setup.iss).
        public static async Task<bool> DownloadAndInstallAsync(UpdateInfo info)
        {
            try
            {
                // Duas conferências antes de qualquer coisa tocar o disco.
                if (!info.Url.StartsWith(PrefixoPermitido, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[UpdateService]: endereço fora do repositório oficial: {info.Url}");
                    return false;
                }

                // Nome sorteado: o caminho fixo era previsivel, entao outro processo do mesmo
                // usuario podia deixar o arquivo pronto (ou trocar depois) e o PDV executaria
                // ele com o privilegio do instalador.
                var tempPath = Path.Combine(Path.GetTempPath(),
                    "PdvUpdate_" + Path.GetRandomFileName() + ".exe");

                using (var response = await _http.GetAsync(info.Url))
                {
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync();

                    // Confere ANTES de gravar: um arquivo que não bate nunca chega a existir
                    // no disco, então não há como ser executado por engano depois.
                    string digital = ImpressaoDigital(bytes);
                    if (!string.Equals(digital, info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine($"[UpdateService]: impressão digital não confere. esperado={info.Sha256} obtido={digital}");
                        return false;
                    }

                    // File.WriteAllBytesAsync não existe no .NET Framework 4.8.
                    await Task.Run(() => File.WriteAllBytes(tempPath, bytes));
                }

                // Confere DE NOVO, agora lendo do disco, logo antes de executar. A conferencia
                // anterior foi sobre os bytes em memoria: entre gravar e rodar ainda cabia uma
                // troca do arquivo, e era justamente isso que a verificacao existia para impedir.
                if (!string.Equals(ImpressaoDigital(File.ReadAllBytes(tempPath)),
                                   info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine("[UpdateService]: arquivo em disco nao confere; nao sera executado.");
                    try { File.Delete(tempPath); } catch { }
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES",
                    UseShellExecute = true
                };
                Process.Start(psi);

                // Fecha ESTE processo agora (não espera o instalador) para não travar os
                // arquivos durante a cópia. O [Run] do instalador reabre o app ao terminar.
                Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService.DownloadAndInstallAsync Error]: {ex.Message}");
                return false;
            }
        }
    }
}
