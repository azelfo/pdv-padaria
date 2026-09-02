using System;
using System.IO;
using System.Collections.Generic;

namespace PdvPadaria.Services
{
    public static class EnvService
    {
        private static readonly Dictionary<string, string> _envVars = new Dictionary<string, string>();
        private static bool _loaded = false;

        // O que a maquina ja sabe sem precisar de arquivo nenhum.
        //
        // O instalador so grava o .env se ainda NAO houver um (onlyifdoesntexist, e tem de
        // continuar assim: senao toda atualizacao apagaria a configuracao da loja). O efeito
        // colateral era mortal -- um caixa instalado com o arquivo em branco ficava em branco
        // para sempre, dando "Configuracao da nuvem ausente no .env" no login e na
        // sincronizacao, e nenhuma atualizacao consertava porque o arquivo ja existia.
        //
        // Estes dois valores nao sao segredo: a chave "anon" e publicavel por design e o
        // Painel da Rede ja a expoe na web. Quem protege os dados e a politica de acesso do
        // servidor, nao o sigilo dela. O que E segredo -- STORE_SYNC_TOKEN, a credencial de
        // ESCRITA -- fica de fora de proposito: ja vazou uma vez num repositorio publico e
        // foi preciso trocar o token das tres lojas. STORE_ID tambem fica de fora, senao
        // toda maquina nasceria carimbada como a mesma loja.
        private static readonly Dictionary<string, string> _padrao = new Dictionary<string, string>
        {
            { "SUPABASE_URL", "https://aezwtbzyremthqdkldzl.supabase.co" },
            { "SUPABASE_ANON_KEY", "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImFlend0Ynp5cmVtdGhxZGtsZHpsIiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODE0NDI4NjEsImV4cCI6MjA5NzAxODg2MX0.kShlLQFGG8PeMpBPbB0Lfbu4bbB-pMdqNO-yOWej1us" }
        };

        /// <summary>
        /// Valor de fabrica da chave, ou vazio se ela nao tiver um.
        /// </summary>
        public static string PadraoEmbutido(string key) =>
            _padrao.TryGetValue(key, out string? v) ? v : "";

        /// <summary>
        /// Quantas vezes o arquivo foi lido de verdade. Existe para o teste conseguir
        /// distinguir "releu" de "continuou em cache" -- sem isso a checagem passaria
        /// mesmo sem implementacao nenhuma, porque o cache antigo devolve o mesmo valor.
        /// </summary>
        public static int Carregamentos { get; private set; }

        /// <summary>
        /// Derruba o cache para a proxima leitura vir do arquivo.
        ///
        /// O cache e estatico e atravessava o logout inteiro: a configuracao lida na sessao
        /// de uma empresa continuava valendo na seguinte. Enquanto ha uma rede so isso e
        /// inofensivo; com duas, e dado de uma empresa sobrevivendo na sessao de outra.
        /// Chamado por EscopoDeSessao.Dispose().
        /// </summary>
        public static void Recarregar()
        {
            _envVars.Clear();
            _loaded = false;
        }

        private static void Load()
        {
            if (_loaded) return;
            Carregamentos++;

            try
            {
                // Busca o arquivo .env a partir do diretório de execução ou da raiz do projeto
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string envPath = Path.Combine(baseDir, ".env");

                // Sobe até achar o arquivo .env se estiver em subdiretórios de compilação
                int limit = 5;
                while (!File.Exists(envPath) && limit > 0)
                {
                    var parent = Directory.GetParent(baseDir);
                    if (parent == null) break;
                    baseDir = parent.FullName;
                    envPath = Path.Combine(baseDir, ".env");
                    limit--;
                }

                if (File.Exists(envPath))
                {
                    var lines = File.ReadAllLines(envPath);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

                        int equalIndex = trimmed.IndexOf('=');
                        if (equalIndex > 0)
                        {
                            string key = trimmed.Substring(0, equalIndex).Trim();
                            string val = trimmed.Substring(equalIndex + 1).Trim();

                            // Remove aspas se houver
                            if (val.StartsWith("\"") && val.EndsWith("\"") && val.Length >= 2)
                            {
                                val = val.Substring(1, val.Length - 2);
                            }
                            else if (val.StartsWith("'") && val.EndsWith("'") && val.Length >= 2)
                            {
                                val = val.Substring(1, val.Length - 2);
                            }

                            _envVars[key] = val;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EnvService Load Error]: {ex.Message}");
            }
            finally
            {
                _loaded = true;
            }
        }

        // Valor EM BRANCO conta como AUSENTE, e nao como "configurado com vazio".
        //
        // O .env.exemplo que vai no instalador traz as chaves com o valor vazio
        // (STORE_ID=, STORE_SYNC_TOKEN=). Numa maquina onde alguem esqueceu de
        // preencher uma linha, a chave EXISTE no dicionario, entao o TryGetValue
        // devolvia "" e o defaultValue (o valor vindo do usuario logado) nunca era
        // usado. Resultado: o PDV consultava a nuvem com storeId vazio, recebia
        // lista vazia de ajustes do dono e o estoque local nunca mais mudava --
        // sem nenhuma mensagem de erro, porque uma resposta vazia e um 200 valido.
        public static string Get(string key, string defaultValue = "")
        {
            Load();
            if (_envVars.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                return value;
            // O .env manda: so caimos no valor de fabrica quando nao ha nada escrito.
            if (!string.IsNullOrEmpty(defaultValue))
                return defaultValue;
            return PadraoEmbutido(key);
        }
    }
}
