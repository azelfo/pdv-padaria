using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using PdvPadaria.Models;
using PdvPadaria.Services;

namespace PdvPadaria.Views
{
    public partial class LoginWindow : Window
    {
        public LoginWindow()
        {
            InitializeComponent();
            
            // Garante foco inicial no campo de e-mail
            EmailInput.Focus();
        }

        // Permite arrastar a janela sem borda clicando em qualquer lugar do fundo
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        // Fecha a aplicação
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        // Trata o botão de login com fluxo híbrido online-first e fallback offline
        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            var email = EmailInput.Text.Trim();
            var password = PasswordInput.Password;

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                ShowError("Preencha todos os campos obrigatórios.");
                return;
            }

            LoginButton.IsEnabled = false;
            LoginButton.Content = "Entrando...";
            ErrorText.Visibility = Visibility.Collapsed;

            try
            {
                User? user = null;
                bool isOnlineSuccess = false;

                // 1. Tenta fazer validação Online contra o Supabase (se as chaves estiverem no .env)
                string supabaseUrl = EnvService.Get("SUPABASE_URL");
                string supabaseAnonKey = EnvService.Get("SUPABASE_ANON_KEY");

                if (!string.IsNullOrEmpty(supabaseUrl) && !string.IsNullOrEmpty(supabaseAnonKey))
                {
                    try
                    {
                        using (var httpClient = new HttpClient())
                        {
                            httpClient.Timeout = TimeSpan.FromSeconds(8); // Timeout rápido para não prender o operador
                            httpClient.DefaultRequestHeaders.Add("apikey", supabaseAnonKey);
                            httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + supabaseAnonKey);

                            // Login server-side: a função RPC verifica a senha no banco
                            // (pgcrypto) e devolve o usuário SEM o hash. A senha nunca é
                            // exposta para a anon key, e o hash não trafega pela rede.
                            var url = $"{supabaseUrl.TrimEnd('/')}/rest/v1/rpc/login_caixa";
                            var rpcBody = new StringContent(
                                JsonConvert.SerializeObject(new { p_email = email, p_senha = password }),
                                System.Text.Encoding.UTF8,
                                "application/json");
                            var response = await httpClient.PostAsync(url, rpcBody);

                            if (response.IsSuccessStatusCode)
                            {
                                var responseText = await response.Content.ReadAsStringAsync();
                                var users = JsonConvert.DeserializeObject<List<User>>(responseText);

                                if (users != null && users.Count > 0)
                                {
                                    var onlineUser = users[0];

                                    // RPC já validou a senha no servidor. Grava localmente
                                    // como hash BCrypt (derivado da senha digitada) para
                                    // permitir login offline futuro deste operador.
                                    onlineUser.Password = PasswordHasher.Hash(password);

                                    var connection = App.Database.GetConnection();
                                    await connection.InsertOrReplaceAsync(onlineUser);

                                    user = onlineUser;
                                    isOnlineSuccess = true;
                                }
                                else
                                {
                                    // RPC retorna vazio tanto para senha errada quanto para
                                    // e-mail inexistente/inativo (não revela qual dos dois).
                                    ShowError("Credenciais inválidas. Verifique sua senha.");
                                    return;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Online Auth Network Failure]: {ex.Message}");
                        // Falha de rede/DNS/Timeout: continuará para a autenticação offline local do SQLite
                    }
                }

                // 2. Se a validação online não ocorreu ou falhou por rede, faz fallback para offline (local SQLite)
                if (!isOnlineSuccess)
                {
                    var connection = App.Database.GetConnection();
                    user = await connection.Table<User>()
                                            .Where(u => u.Email == email && u.Active)
                                            .FirstOrDefaultAsync();

                    if (user == null)
                    {
                        ShowError("Usuário não encontrado localmente e sem conexão online.");
                        return;
                    }

                    if (!PasswordHasher.Verify(password, user.Password))
                    {
                        ShowError("Credenciais inválidas. Verifique sua senha.");
                        return;
                    }

                    // Migracao transparente: se a senha local ainda estava em texto
                    // puro (base antiga), re-grava como hash BCrypt agora.
                    if (PasswordHasher.NeedsUpgrade(user.Password))
                    {
                        user.Password = PasswordHasher.Hash(password);
                        await connection.UpdateAsync(user);
                    }
                }

                // 3. Sucesso no login: Se logou online, puxa produtos e estoques em background
                if (isOnlineSuccess && user != null)
                {
                    string tenantId = EnvService.Get("TENANT_ID", user.TenantId);
                    string storeId = EnvService.Get("STORE_ID", user.StoreId ?? "store-test");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var syncService = new SyncService(App.Database.GetSyncConnection());
                            await syncService.PullUpdatesAsync(tenantId, storeId);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Sync Initial Pull Error]: {ex.Message}");
                        }
                    });
                }

                // Sucesso completo: Abre a janela principal do PDV
                // Passa as credenciais digitadas (memória) para o painel da rede do DONO.
                var pdvWindow = new MainWindow(user!, email, password);
                pdvWindow.Show();
                
                // Fecha a tela de login
                Close();
            }
            catch (Exception ex)
            {
                ShowError($"Erro inesperado: {ex.Message}");
            }
            finally
            {
                LoginButton.IsEnabled = true;
                LoginButton.Content = "Entrar no Caixa";
            }
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                LoginButton_Click(this, new RoutedEventArgs());
            }
        }

    }
}
