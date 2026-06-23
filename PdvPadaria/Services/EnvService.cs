using System;
using System.IO;
using System.Collections.Generic;

namespace PdvPadaria.Services
{
    public static class EnvService
    {
        private static readonly Dictionary<string, string> _envVars = new Dictionary<string, string>();
        private static bool _loaded = false;

        private static void Load()
        {
            if (_loaded) return;

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

        public static string Get(string key, string defaultValue = "")
        {
            Load();
            return _envVars.TryGetValue(key, out string? value) ? value : defaultValue;
        }
    }
}
