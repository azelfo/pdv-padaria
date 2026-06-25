using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QRCoder;

namespace PdvPadaria.Services
{
    public static class InfinitePayService
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        public static Task<string> ObterAccessTokenAsync()
        {
            string clientSecret = EnvService.Get("INFINITE_CLIENT_SECRET", "");

            if (string.IsNullOrEmpty(clientSecret))
            {
                throw new Exception("A credencial de autenticação da InfinitePay (configurada em INFINITE_CLIENT_SECRET no .env) não foi informada.");
            }

            return Task.FromResult(clientSecret);
        }

        private static string GetValueLength(string value)
        {
            return value.Length.ToString("D2");
        }

        private static string CalcularCrc16(string payload)
        {
            int polinomio = 0x1021;
            int resultado = 0xFFFF;

            byte[] dados = Encoding.UTF8.GetBytes(payload);

            foreach (byte b in dados)
            {
                resultado ^= (b << 8);
                for (int i = 0; i < 8; i++)
                {
                    if ((resultado & 0x8000) != 0)
                        resultado = (resultado << 1) ^ polinomio;
                    else
                        resultado <<= 1;
                    resultado &= 0xFFFF;
                }
            }

            return resultado.ToString("X4");
        }

        /// <summary>
        /// Gera a string do Pix Estático offline e retorna o Base64 da imagem para renderizar na tela.
        /// </summary>
        public static string GerarQrCodePixLocal(string saleId, int totalCentavos)
        {
            string chavePix = EnvService.Get("PIX_CHAVE", "sua_chave_pix@padaria.com");
            string merchantName = EnvService.Get("PIX_NOME", "Padaria Ouro");
            string merchantCity = EnvService.Get("PIX_CIDADE", "Fortaleza");
            string amountStr = (totalCentavos / 100.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            
            // Tratamento do TxId - remove traços (se for GUID) para o padrão PIX
            string txId = saleId.Replace("-", "").ToUpper();
            if (txId.Length > 25) txId = txId.Substring(0, 25);

            string gui = "br.gov.bcb.pix";
            string contaInfo = $"00{GetValueLength(gui)}{gui}01{GetValueLength(chavePix)}{chavePix}";
            string infoAdicional = $"05{GetValueLength(txId)}{txId}";

            string payloadFormatIndicator = "000201";
            string merchantAccountInfo = $"26{GetValueLength(contaInfo)}{contaInfo}";
            string merchantCategoryCode = "52040000";
            string transactionCurrency = "5303986";
            string transactionAmount = $"54{GetValueLength(amountStr)}{amountStr}";
            string countryCode = "5802BR";
            string nameInfo = $"59{GetValueLength(merchantName)}{merchantName}";
            string cityInfo = $"60{GetValueLength(merchantCity)}{merchantCity}";
            string additionalData = $"62{GetValueLength(infoAdicional)}{infoAdicional}";

            string payloadWithoutCrc = $"{payloadFormatIndicator}{merchantAccountInfo}{merchantCategoryCode}{transactionCurrency}{transactionAmount}{countryCode}{nameInfo}{cityInfo}{additionalData}6304";
            
            string crc = CalcularCrc16(payloadWithoutCrc);
            string pixString = payloadWithoutCrc + crc;

            // Gera o Base64 do QR Code da imagem
            using (var qrGenerator = new QRCodeGenerator())
            {
                var qrCodeData = qrGenerator.CreateQrCode(pixString, QRCodeGenerator.ECCLevel.Q);
                var qrCode = new PngByteQRCode(qrCodeData);
                byte[] qrCodeBytes = qrCode.GetGraphic(20);
                string base64Image = Convert.ToBase64String(qrCodeBytes);
                return "data:image/png;base64," + base64Image;
            }
        }

        /// <summary>
        /// Verifica o status do pagamento diretamente na InfinitePay.
        /// Retorna true se estiver pago, false caso contrário.
        /// </summary>
        public static async Task<bool> VerificarPagamentoInfinitePayAsync(string saleId)
        {
            string handle = EnvService.Get("INFINITE_HANDLE", "padaria-ouro-test");

            var payload = new
            {
                handle = handle,
                order_nsu = saleId
            };

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.checkout.infinitepay.io/payment_check"))
                {
                    request.Content = new StringContent(
                        JsonConvert.SerializeObject(payload),
                        Encoding.UTF8,
                        "application/json"
                    );

                    var response = await _httpClient.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        string jsonResponse = await response.Content.ReadAsStringAsync();
                        var data = JsonConvert.DeserializeObject<dynamic>(jsonResponse);

                        if (data != null && data.paid != null)
                        {
                            bool isPaid = (bool)data.paid;
                            return isPaid;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InfinitePay Polling Error for Sale {saleId}]: {ex.Message}");
            }

            return false;
        }
    }
}
