using System;
using System.Windows;
using System.Threading.Tasks;
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
                try
                {
                    string dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pdv-padaria");
                    System.IO.Directory.CreateDirectory(dir);
                    string log = System.IO.Path.Combine(dir, "erro.log");
                    System.IO.File.AppendAllText(log,
                        $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n{ev.Exception}\n\n");
                    MessageBox.Show(
                        $"Ocorreu um erro nesta tela:\n\n{ev.Exception.Message}\n\nO erro foi registrado. O sistema continua funcionando.",
                        "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
                ev.Handled = true; // impede o app de fechar
            };

            try
            {
                // 1. Inicializa o serviço de banco SQLite local
                Database = new DatabaseService();
                await Database.InitializeAsync();

                // Tenta puxar atualizações cadastrais da nuvem de forma assíncrona em background
                string tenantId = EnvService.Get("TENANT_ID");
                string storeId = EnvService.Get("STORE_ID");
                if (!string.IsNullOrEmpty(tenantId) && !string.IsNullOrEmpty(storeId))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var syncService = new SyncService(Database.GetSyncConnection());
                            await syncService.PullUpdatesAsync(tenantId, storeId);
                        }
                        catch
                        {
                            // Ignora falhas na inicialização (ex: offline) pois o caixa funciona de forma autônoma offline
                        }
                    });
                }

                // 2. Abre a tela de Login
                var loginWindow = new Views.LoginWindow();
                loginWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Erro fatal durante a inicialização do aplicativo:\n\n{ex.Message}\n\nO aplicativo será encerrado.",
                    "Erro de Inicialização",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                Shutdown();
            }
        }
    }
}

