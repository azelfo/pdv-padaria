using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PdvPadaria.Services
{
    /// <summary>
    /// Descobre de QUAL loja é esta máquina, perguntando para a nuvem em vez de acreditar
    /// numa linha digitada à mão.
    ///
    /// Antes a identidade do caixa vinha de DUAS linhas independentes do .env:
    ///
    ///   STORE_SYNC_TOKEN  decide onde a venda CAI  (o servidor carimba a loja a partir
    ///                     dele e ignora qualquer storeId enviado no payload)
    ///   STORE_ID          decidia o que o caixa LÊ (produtos da loja, tabela de preço do
    ///                     pão e os ajustes de estoque lançados pelo dono)
    ///
    /// Nada amarrava as duas. Trocar o token de loja e esquecer o STORE_ID — ou o contrário —
    /// deixava o caixa vendendo por uma loja e mostrando o estoque de outra, sem erro nenhum
    /// na tela. Foi o que aconteceu em 20/08/2026: venda caindo na Padaria Japão numa máquina
    /// que lia o estoque da Padaria Centro. Para quem estava no balcão, isso apareceu como
    /// "o estoque do PDV não atualiza".
    ///
    /// Agora só o TOKEN define a loja, dos dois lados. A RPC loja_do_token devolve o storeId
    /// dono do token; esse valor é gravado aqui do lado para o caixa continuar sabendo quem é
    /// quando abrir sem internet. O STORE_ID do .env vira apenas socorro para a primeira
    /// abertura offline de uma máquina recém-instalada.
    /// </summary>
    public static class StoreIdentityService
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        private static string _storeId = string.Empty;
        private static bool _resolvido = false;

        /// <summary>Loja desta máquina, já resolvida. Vazio só se nunca deu para descobrir.</summary>
        public static string StoreId => _storeId;

        /// <summary>A nuvem respondeu que o token desta máquina não vale mais.</summary>
        public static bool TokenInvalido { get; private set; }

        /// <summary>Falta STORE_SYNC_TOKEN no .env desta máquina.</summary>
        public static bool TokenAusente { get; private set; }

        /// <summary>O STORE_ID escrito no .env aponta para outra loja (e foi ignorado).</summary>
        public static bool EnvDivergente { get; private set; }

        /// <summary>Valor da linha STORE_ID do .env, para poder mostrar a divergência na tela.</summary>
        public static string StoreIdDoEnv { get; private set; } = string.Empty;

        private static string CaminhoCache
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pdv-padaria");
                return Path.Combine(dir, "loja-identidade.txt");
            }
        }

        /// <summary>
        /// Valor utilizável AGORA, sem esperar rede. Enquanto a resolução não terminou, cai
        /// no que já se sabia (arquivo local, depois .env, depois o fallback do chamador).
        /// Os pontos que dependem da loja certa — sincronização, venda, ajuste de estoque —
        /// rodam depois de ResolverAsync, então na prática recebem o valor da nuvem.
        /// </summary>
        public static string Atual(string fallback = "")
        {
            if (!string.IsNullOrWhiteSpace(_storeId)) return _storeId;

            string doCache = LerCache();
            if (!string.IsNullOrWhiteSpace(doCache)) return doCache;

            return EnvService.Get("STORE_ID", fallback);
        }

        /// <summary>
        /// Pergunta à nuvem de quem é o token desta máquina e fixa a resposta. Roda uma vez
        /// por execução: chamar de novo devolve na hora. Nunca lança — máquina offline
        /// simplesmente segue com a última identidade conhecida.
        /// </summary>
        public static async Task ResolverAsync(string fallback = "")
        {
            if (_resolvido) return;
            _resolvido = true;

            StoreIdDoEnv = EnvService.Get("STORE_ID");

            string token = EnvService.Get("STORE_SYNC_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                TokenAusente = true;
                _storeId = Atual(fallback);
                return;
            }

            var (alcancouNuvem, lojaDoToken) = await PerguntarLojaDoTokenAsync(token);

            if (alcancouNuvem && !string.IsNullOrWhiteSpace(lojaDoToken))
            {
                _storeId = lojaDoToken;
                GravarCache(lojaDoToken);

                // Divergência é só informativa: o token manda, e o caixa já está funcionando
                // certo. Mas vale avisar, porque a linha errada no .env volta a confundir
                // quem for mexer na configuração depois.
                EnvDivergente = !string.IsNullOrWhiteSpace(StoreIdDoEnv)
                                && !string.Equals(StoreIdDoEnv, lojaDoToken, StringComparison.OrdinalIgnoreCase);
                return;
            }

            if (alcancouNuvem)
            {
                // A nuvem respondeu e não reconheceu o token: nada que este caixa gravar vai
                // subir. O estoque local continua correto e as vendas ficam na fila.
                TokenInvalido = true;
            }

            _storeId = Atual(fallback);
        }

        // Chama a RPC loja_do_token. Devolve (alcancouNuvem, storeId).
        // "alcancouNuvem = false" é offline/erro de rede — não é veredito sobre o token.
        private static async Task<(bool AlcancouNuvem, string StoreId)> PerguntarLojaDoTokenAsync(string token)
        {
            string url = EnvService.Get("SUPABASE_URL");
            string anonKey = EnvService.Get("SUPABASE_ANON_KEY");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(anonKey))
                return (false, string.Empty);

            try
            {
                var content = new StringContent(
                    JsonConvert.SerializeObject(new { p_token = token }), Encoding.UTF8, "application/json");

                using (var request = new HttpRequestMessage(HttpMethod.Post, $"{url.TrimEnd('/')}/rest/v1/rpc/loja_do_token"))
                {
                    request.Content = content;
                    request.Headers.Add("apikey", anonKey);
                    request.Headers.Add("Authorization", $"Bearer {anonKey}");

                    var response = await _http.SendAsync(request);
                    if (!response.IsSuccessStatusCode) return (false, string.Empty);

                    // A RPC devolve texto puro entre aspas, ou "null" quando não reconhece o token.
                    string corpo = (await response.Content.ReadAsStringAsync()).Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(corpo) || corpo == "null") return (true, string.Empty);

                    return (true, corpo);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StoreIdentity: rede]: {ex.Message}");
                return (false, string.Empty);
            }
        }

        private static string LerCache()
        {
            try
            {
                return File.Exists(CaminhoCache) ? File.ReadAllText(CaminhoCache).Trim() : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static void GravarCache(string storeId)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CaminhoCache)!);
                File.WriteAllText(CaminhoCache, storeId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StoreIdentity: cache]: {ex.Message}");
            }
        }
    }
}
