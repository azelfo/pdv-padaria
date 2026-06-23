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

