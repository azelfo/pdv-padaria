using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PdvPadaria.Services
{
    /// <summary>
    /// Descobre de QUAL loja é esta máquina — e guarda a credencial que prova isso —
    /// sem ninguém precisar digitar segredo em arquivo nenhum.
    ///
    /// Como era antes, e por que doeu:
    ///
    ///   A identidade do caixa vinha de duas linhas independentes do .env. O
    ///   STORE_SYNC_TOKEN decidia onde a venda CAÍA (o servidor carimba a loja a partir
    ///   dele) e o STORE_ID decidia o que a máquina LIA — produtos, tabela do pão e os
    ///   ajustes de estoque do dono. Nada conferia se as duas combinavam, e trocar uma
    ///   sem a outra deixava o caixa vendendo por uma loja e mostrando o estoque de
    ///   outra, calado. Pior: quando um token era revogado, alguém tinha que ir de PC em
    ///   PC colar o novo — e enquanto isso não acontecia, a loja simplesmente parava de
    ///   sincronizar. Duas lojas ficaram dias assim.
    ///
    /// Como é agora:
    ///
    ///   Quem sabe a loja é o LOGIN. O usuário do caixa (centro@, japao@, producao@) já
    ///   carrega o storeId no cadastro, então no primeiro login online a máquina chama
    ///   registrar_caixa e recebe um token só dela, que fica guardado em %AppData%. Daí
    ///   em diante esse token responde pelos dois lados — o que grava e o que lê.
    ///
    ///   O STORE_SYNC_TOKEN do .env continua aceito, para as máquinas que já estavam
    ///   configuradas. O STORE_ID virou só socorro de primeira abertura offline.
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

        /// <summary>A máquina ainda não tem token nenhum (nem registrado, nem no .env).</summary>
        public static bool TokenAusente { get; private set; }

        // PDV_DADOS_DIR existe para os testes nao mexerem na identidade real da maquina:
        // ResolverAsync grava loja-identidade.txt e estoque-loja.txt, e rodar a checagem num
        // caixa de loja reescrevia esses arquivos. Em producao a variavel nao existe.
        private static string PastaDados =>
            Environment.GetEnvironmentVariable("PDV_DADOS_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pdv-padaria");

        // Loja descoberta pelo token, guardada para o caixa continuar sabendo quem é
        // quando abrir sem internet.
        private static string CaminhoLoja => Path.Combine(PastaDados, "loja-identidade.txt");

        // Loja cujo ESTOQUE está hoje no SQLite. É separada da loja do token porque uma
        // troca só termina depois que o saldo da loja nova foi baixado com sucesso.
        private static string CaminhoLojaDoEstoque => Path.Combine(PastaDados, "estoque-loja.txt");

        // Token PRÓPRIO desta máquina, emitido no login. Fica fora do .env de propósito:
        // o .env vai junto com a pasta do programa e já vazou uma vez dentro do instalador.
        private static string CaminhoToken => Path.Combine(PastaDados, "caixa-token.dat");

        /// <summary>
        /// Credencial de sincronização em uso: o token desta máquina, se já registrado;
        /// senão o STORE_SYNC_TOKEN do .env (máquinas antigas). Vazio = não dá para gravar
        /// nada na nuvem.
        /// </summary>
        public static string TokenAtual()
        {
            string doArquivo = LerArquivo(CaminhoToken);
            if (!string.IsNullOrWhiteSpace(doArquivo)) return doArquivo;

            return EnvService.Get("STORE_SYNC_TOKEN");
        }

        /// <summary>
        /// Loja utilizável AGORA, sem esperar rede. Enquanto a resolução não terminou, cai
        /// no que já se sabia (arquivo local, depois .env, depois o fallback do chamador).
        /// </summary>
        public static string Atual(string fallback = "")
        {
            if (!string.IsNullOrWhiteSpace(_storeId)) return _storeId;

            string doCache = LerArquivo(CaminhoLoja);
            if (!string.IsNullOrWhiteSpace(doCache)) return doCache;

            return EnvService.Get("STORE_ID", fallback);
        }

        /// <summary>Loja cujo saldo está carregado no SQLite desta máquina.</summary>
        public static string LojaDoEstoque()
        {
            GarantirCacheDoEstoque();
            return LerArquivo(CaminhoLojaDoEstoque);
        }

        /// <summary>Última loja confirmada pelo token, sem confiar no STORE_ID legado.</summary>
        public static string LojaDoTokenConhecida()
        {
            // _storeId também pode conter o fallback do STORE_ID quando o token está
            // ausente/inválido. DONO não pode usar esse palpite para religar a máquina.
            // Este arquivo só é gravado por token validado ou registro confirmado.
            return LerArquivo(CaminhoLoja);
        }

        /// <summary>
        /// Confirma a troca só depois que o estoque da loja nova foi baixado. Assim, se a
        /// internet cair no meio, o próximo login tenta novamente em vez de abrir misturado.
        /// </summary>
        public static bool ConfirmarEstoqueDaLoja(string storeId)
        {
            if (string.IsNullOrWhiteSpace(storeId)) return false;
            return GravarArquivo(CaminhoLojaDoEstoque, storeId);
        }

        /// <summary>
        /// Este registro (venda, movimento) é da loja desta máquina?
        ///
        /// Serve para barrar operação que só a loja dona pode fazer — cancelar uma venda,
        /// por exemplo, que devolve estoque no banco local. O servidor já recusa gravação
        /// fora da loja do token, mas a recusa derruba o LOTE inteiro do envio: uma
        /// operação errada numa tela travaria a subida de TODAS as vendas pendentes. Por
        /// isso a pergunta é feita antes, aqui, e não depois, lá.
        ///
        /// Loja desconhecida devolve falso: sem saber quem se é, não se age.
        /// </summary>
        public static bool PertenceAEstaMaquina(string? storeIdDoRegistro, string fallback = "")
        {
            if (string.IsNullOrWhiteSpace(storeIdDoRegistro)) return false;

            string lojaDesteCaixa = Atual(fallback);
            if (string.IsNullOrWhiteSpace(lojaDesteCaixa)) return false;

            return string.Equals(storeIdDoRegistro, lojaDesteCaixa, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Descarta o que esta execução já descobriu, para o próximo login recomeçar do zero.
        ///
        /// Existe porque estes campos são estáticos: eles vivem enquanto o processo viver, e
        /// o logout não reinicia o processo — troca a janela. Sem esta chamada, entrar com
        /// outro usuário reaproveitava a identidade do anterior, e o caixa passava a se
        /// comportar de um jeito na primeira entrada e de outro na segunda. Era essa a
        /// "sessão fantasma".
        ///
        /// NÃO apaga os arquivos de identidade: token e loja pertencem à MÁQUINA, não a quem
        /// está logado. Apagá-los faria o caixa perder o cadastro dele e precisar registrar
        /// de novo a cada troca de turno.
        /// </summary>
        public static void Encerrar()
        {
            _storeId = string.Empty;
            _resolvido = false;
            TokenAusente = false;
            // TokenInvalido sobrevive de propósito: é um fato sobre a CREDENCIAL da máquina,
            // não sobre quem estava logado. Sair do sistema não conserta um token recusado.
        }

        /// <summary>
        /// Pergunta à nuvem de quem é o token desta máquina e fixa a resposta. Roda uma vez
        /// por execução, a não ser que um registro novo peça para refazer. Nunca lança:
        /// máquina offline segue com a última identidade conhecida.
        /// </summary>
        public static async Task ResolverAsync(string fallback = "")
        {
            if (_resolvido) return;
            _resolvido = true;

            GarantirCacheDoEstoque();
            TokenAusente = false;
            // TokenInvalido NÃO é zerado aqui. Ele só muda quando a nuvem responde: se o
            // caixa abrir sem conexão, o veredito anterior continua valendo. Zerá-lo antes
            // de perguntar fazia o aviso sumir num logout+login offline, e o caixa voltava
            // a operar calado com uma credencial que a nuvem já tinha recusado.

            string token = TokenAtual();
            if (string.IsNullOrWhiteSpace(token))
            {
                TokenAusente = true;
                _storeId = Atual(fallback);
                return;
            }

            var (alcancouNuvem, lojaDoToken) = await PerguntarLojaDoTokenAsync(token);

            if (alcancouNuvem && !string.IsNullOrWhiteSpace(lojaDoToken))
            {
                // A nuvem reconheceu o token: só agora o veredito anterior deixa de valer.
                TokenInvalido = false;
                _storeId = lojaDoToken;
                GravarArquivo(CaminhoLoja, lojaDoToken);
                return;
            }

            if (alcancouNuvem)
            {
                // A nuvem respondeu e não reconheceu o token: nada que este caixa gravar
                // vai subir. O estoque local continua certo e as vendas ficam na fila.
                TokenInvalido = true;
            }

            _storeId = Atual(fallback);
        }

        /// <summary>
        /// Esta máquina precisa se registrar (ou re-registrar) para a loja do usuário que
        /// acabou de logar? Verdadeiro quando não há token, quando o token não vale mais,
        /// ou quando ele responde por OUTRA loja — o caso em que a máquina estaria
        /// lançando as vendas na loja errada.
        /// </summary>
        public static bool PrecisaRegistrar(string storeIdDoUsuario)
        {
            if (string.IsNullOrWhiteSpace(storeIdDoUsuario)) return false; // DONO não tem loja
            if (TokenAusente || TokenInvalido) return true;

            return !string.Equals(_storeId, storeIdDoUsuario, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>O token mudou de loja, mas o estoque novo ainda não foi carregado?</summary>
        public static bool PrecisaSemearEstoque(string storeId)
        {
            if (string.IsNullOrWhiteSpace(storeId)) return false;
            return !string.Equals(LojaDoEstoque(), storeId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Marca a recusa recebida durante o sync. Permite sair e entrar novamente no mesmo
        /// processo para renovar a credencial, sem depender de fechar o aplicativo inteiro.
        /// </summary>
        public static void MarcarTokenInvalido()
        {
            TokenInvalido = true;
        }

        /// <summary>
        /// Pede à nuvem um token próprio desta máquina, usando as credenciais que o
        /// operador acabou de digitar. A senha não é guardada em lugar nenhum: ela só
        /// atravessa esta chamada. O token volta UMA vez e fica salvo em %AppData%.
        /// </summary>
        public static async Task<bool> RegistrarPeloLoginAsync(string email, string senha, string storeId)
        {
            string url = EnvService.Get("SUPABASE_URL");
            string anonKey = EnvService.Get("SUPABASE_ANON_KEY");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(anonKey)) return false;

            try
            {
                var corpo = JsonConvert.SerializeObject(new
                {
                    p_email = email,
                    p_senha = senha,
                    p_terminal = EnvService.Get("TERMINAL_NAME", Environment.MachineName),
                    p_store_id = storeId,
                    p_token_atual = TokenAtual()
                });

                using (var request = new HttpRequestMessage(HttpMethod.Post, $"{url.TrimEnd('/')}/rest/v1/rpc/registrar_caixa"))
                {
                    request.Content = new StringContent(corpo, Encoding.UTF8, "application/json");
                    request.Headers.Add("apikey", anonKey);
                    request.Headers.Add("Authorization", $"Bearer {anonKey}");

                    var response = await _http.SendAsync(request);
                    if (!response.IsSuccessStatusCode) return false;

                    var json = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(
                        await response.Content.ReadAsStringAsync());
                    if (json == null) return false;

                    // registrar_caixa também recusa com HTTP 200, no corpo — mesma armadilha
                    // das outras RPCs de escrita.
                    string? erro = json["error"]?.ToString();
                    if (!string.IsNullOrEmpty(erro))
                    {
                        System.Diagnostics.Debug.WriteLine($"[registrar_caixa recusado]: {erro}");
                        return false;
                    }

                    string token = json["token"]?.ToString() ?? string.Empty;
                    string storeIdRegistrado = json["storeId"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(storeIdRegistrado)) return false;

                    if (!GravarArquivo(CaminhoToken, token)) return false;
                    if (!GravarArquivo(CaminhoLoja, storeIdRegistrado))
                    {
                        _resolvido = false;
                        return false;
                    }

                    _storeId = storeIdRegistrado;
                    TokenAusente = false;
                    TokenInvalido = false;
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[registrar_caixa]: {ex.Message}");
                return false;
            }
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

        private static string LerArquivo(string caminho)
        {
            try
            {
                return File.Exists(caminho) ? File.ReadAllText(caminho).Trim() : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static bool GarantirCacheDoEstoque()
        {
            if (File.Exists(CaminhoLojaDoEstoque)) return true;

            // Migração da versão antiga: se token e STORE_ID concordavam, preserva o
            // estoque local e evita uma carga desnecessária. Quando divergem (o caso das
            // fotos do incidente), deixa a origem desconhecida e exige um login online
            // antes de confirmar qualquer loja. Nunca escolhe silenciosamente um lado.
            string lojaDoToken = LerArquivo(CaminhoLoja);
            string lojaDoEnv = EnvService.Get("STORE_ID");
            if (!string.IsNullOrWhiteSpace(lojaDoToken)
                && !string.IsNullOrWhiteSpace(lojaDoEnv)
                && !string.Equals(lojaDoToken, lojaDoEnv, StringComparison.OrdinalIgnoreCase))
            {
                return GravarArquivo(CaminhoLojaDoEstoque, string.Empty);
            }

            string conhecida = !string.IsNullOrWhiteSpace(lojaDoToken) ? lojaDoToken : lojaDoEnv;
            return GravarArquivo(CaminhoLojaDoEstoque, conhecida);
        }

        private static bool GravarArquivo(string caminho, string conteudo)
        {
            try
            {
                Directory.CreateDirectory(PastaDados);
                File.WriteAllText(caminho, conteudo);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StoreIdentity: gravar {Path.GetFileName(caminho)}]: {ex.Message}");
                return false;
            }
        }
    }
}
