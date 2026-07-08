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
        public static async Task<bool> DownloadAndInstallAsync(string url)
        {
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), "Setup_PadariaVenancio_update.exe");

                using (var response = await _http.GetAsync(url))
                {
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(tempPath, bytes);
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
