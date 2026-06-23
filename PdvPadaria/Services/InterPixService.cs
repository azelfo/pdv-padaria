using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PdvPadaria.Services
{
    public static class InterPixService
    {
        private static string _accessToken = "";
        private static DateTime _tokenExpiryTime = DateTime.MinValue;

        private static string BaseUrl => EnvService.Get("INTER_ENV", "producao").ToLower() == "sandbox"
            ? "https://cdpj-sandbox.partners.uatinter.co"
            : "https://cdpj.partners.bancointer.com.br";

        private static HttpClient CreatemTLSClient()
        {
            string certPath = EnvService.Get("INTER_CERT_PATH", "");
            string certPassword = EnvService.Get("INTER_CERT_PASSWORD", "");

            if (string.IsNullOrEmpty(certPath))
            {
                throw new FileNotFoundException("O caminho do certificado digital (.pfx) do Banco Inter (INTER_CERT_PATH) não foi configurado no arquivo .env.");
            }

            if (!File.Exists(certPath))
            {
                throw new FileNotFoundException($"O arquivo de certificado digital do Banco Inter não foi encontrado no caminho especificado: {certPath}");
            }

            var handler = new HttpClientHandler();
            try
            {
                // Carrega o certificado digital .pfx ou .p12 para autenticação mTLS
                var certificate = new X509Certificate2(certPath, certPassword);
                handler.ClientCertificates.Add(certificate);
            }
            catch (Exception ex)
            {
                throw new Exception($"Falha ao carregar o certificado digital do Banco Inter (.pfx). Verifique se a senha informada no .env está correta. Detalhes: {ex.Message}");
            }

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
        }

        public static async Task<string> ObterAccessTokenAsync()
        {
            // Reutiliza o token em cache se estiver na validade
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiryTime)
            {
                return _accessToken;
            }

            string clientId = EnvService.Get("INTER_CLIENT_ID", "");
            string clientSecret = EnvService.Get("INTER_CLIENT_SECRET", "");

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                throw new Exception("As credenciais do Banco Inter (INTER_CLIENT_ID e INTER_CLIENT_SECRET) não foram configuradas no arquivo .env.");
            }

            using (var client = CreatemTLSClient())
            {
                var body = $"client_id={Uri.EscapeDataString(clientId)}" +
                           $"&client_secret={Uri.EscapeDataString(clientSecret)}" +
                           $"&grant_type=client_credentials" +
                           $"&scope=pix.write%20pix.read";

                var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

                var response = await client.PostAsync($"{BaseUrl}/oauth/v2/token", content);
                if (!response.IsSuccessStatusCode)
                {
                    string errorResponse = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Erro na autenticação com o Banco Inter ({response.StatusCode}): {errorResponse}");
                }

                string jsonResponse = await response.Content.ReadAsStringAsync();
                var tokenData = JsonConvert.DeserializeObject<dynamic>(jsonResponse);

                _accessToken = tokenData?.access_token ?? "";
                int expiresInSeconds = tokenData?.expires_in ?? 3600;

                // Expiração com margem de segurança de 30 segundos
                _tokenExpiryTime = DateTime.UtcNow.AddSeconds(expiresInSeconds - 30);

                if (string.IsNullOrEmpty(_accessToken))
                {
                    throw new Exception("Resposta de autenticação do Banco Inter não retornou um access_token válido.");
                }

                return _accessToken;
            }
        }

        public static async Task<dynamic> CriarCobrançaPixAsync(int totalCentavos)
        {
            string token = await ObterAccessTokenAsync();
            string chavePix = EnvService.Get("INTER_CHAVE_PIX", "");

            if (string.IsNullOrEmpty(chavePix))
            {
                throw new Exception("A chave PIX do Banco Inter (INTER_CHAVE_PIX) não foi configurada no arquivo .env.");
            }

            double valorOriginal = totalCentavos / 100.0;
            string valorFormatado = valorOriginal.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

            using (var client = CreatemTLSClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

                var payload = new
                {
                    calendario = new
                    {
                        expiracao = 3600
                    },
                    valor = new
                    {
                        original = valorFormatado
                    },
                    chave = chavePix,
                    solicitacaoPagador = "Pagamento PDV Padaria"
                };

                var content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await client.PostAsync($"{BaseUrl}/pix/v2/cob", content);
                if (!response.IsSuccessStatusCode)
                {
                    string errorResponse = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Erro ao criar cobrança PIX no Banco Inter ({response.StatusCode}): {errorResponse}");
                }

                string jsonResponse = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<dynamic>(jsonResponse) ?? throw new Exception("Falha ao ler dados da cobrança PIX.");
            }
        }

        public static async Task<string> ConsultarPixAsync(string txid)
        {
            if (string.IsNullOrEmpty(txid)) return "REMOVIDA";

            string token = await ObterAccessTokenAsync();

            using (var client = CreatemTLSClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

                var response = await client.GetAsync($"{BaseUrl}/pix/v2/cob/{txid}");
                if (!response.IsSuccessStatusCode)
                {
                    string errorResponse = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Erro ao consultar cobrança PIX ({response.StatusCode}): {errorResponse}");
                }

                string jsonResponse = await response.Content.ReadAsStringAsync();
                var data = JsonConvert.DeserializeObject<dynamic>(jsonResponse);
                return data?.status ?? "ATIVA";
            }
        }
    }
}
