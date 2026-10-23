using System;
using System.Windows;
using PdvPadaria.Services;

namespace PdvPadaria
{
    public partial class App : Application
    {
        public static DatabaseService Database { get; private set; } = null!;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // TLS 1.2 obrigatório ANTES de qualquer chamada de rede.
            // No Windows 7 SP1 / 8.1 o padrão do sistema ainda é TLS 1.0, e tanto o
            // Supabase quanto o GitHub (auto-update) recusam TLS 1.0 — sem isto o PDV
            // abriria normalmente mas nunca sincronizaria nem atualizaria, sem erro claro.
            // Usa o valor numérico (3072 = Tls12) porque o enum SecurityProtocolType.Tls12
            // não existe em versões antigas do .NET Framework.
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TLS 1.2 setup]: {ex.Message}");
            }

            // Captura qualquer exceção não tratada na UI: grava em arquivo e NÃO fecha o app.
            // Serve tanto pra diagnosticar (temos o stack) quanto pra robustez em produção
            // (um erro numa tela não derruba o caixa inteiro).
            DispatcherUnhandledException += (s, ev) =>
            {
                var log = StartupDiagnostics.Registrar("Erro na interface", ev.Exception);
                try
                {
                    MessageBox.Show(
                        $"Ocorreu um erro nesta tela:\n\n{ev.Exception.Message}\n\n" +
                        (log == null ? "Não foi possível gravar o diagnóstico." : "Diagnóstico salvo em:\n" + log),
                        "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
                ev.Handled = true; // impede o app de fechar
            };

            try
            {
                // 1. Inicializa o serviço de banco SQLite local
                StartupDiagnostics.Registrar("Inicializando banco local");
                Database = new DatabaseService();
                await Database.InitializeAsync();

                // A identidade e o primeiro pull são resolvidos uma única vez, no login.
                // Fazê-los aqui também criava uma corrida: a resposta de um token antigo
                // podia chegar depois do registro novo e sobrescrever a loja correta.

                // 2. Abre a tela de Login
                StartupDiagnostics.Registrar("Abrindo tela de login");
                var loginWindow = new Views.LoginWindow();
                loginWindow.Show();
                StartupDiagnostics.Registrar("Tela de login aberta");
            }
            catch (Exception ex)
            {
                StartupDiagnostics.MostrarErroFatal("Inicialização do banco ou login", ex);
                Shutdown(1);
            }
        }
    }
}

