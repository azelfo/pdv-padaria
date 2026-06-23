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

                            var url = $"{supabaseUrl.TrimEnd('/')}/rest/v1/User?email=eq.{Uri.EscapeDataString(email)}&active=eq.true";
                            var response = await httpClient.GetAsync(url);

                            if (response.IsSuccessStatusCode)
                            {
                                var responseText = await response.Content.ReadAsStringAsync();
                                var users = JsonConvert.DeserializeObject<List<User>>(responseText);

                                if (users != null && users.Count > 0)
                                {
                                    var onlineUser = users[0];

                                    if (!PasswordHasher.Verify(password, onlineUser.Password))
                                    {
                                        ShowError("Credenciais inválidas. Verifique sua senha.");
                                        return;
                                    }

                                    // Senha correta: grava localmente SEMPRE como hash BCrypt
                                    // (mesmo se o servidor ainda devolver texto puro), para o
                                    // SQLite local nunca conter senha em claro.
                                    onlineUser.Password = PasswordHasher.Hash(password);

                                    var connection = App.Database.GetConnection();
                                    await connection.InsertOrReplaceAsync(onlineUser);

                                    user = onlineUser;
                                    isOnlineSuccess = true;
                                }
                                else
                                {
                                    ShowError("Usuário não cadastrado na nuvem ou inativo.");
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
                            var syncService = new SyncService(App.Database.GetSyncConnection());
                            await syncService.PullUpdatesAsync(tenantId, storeId);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Sync Initial Pull Error]: {ex.Message}");
                        }
                    });
                }

                // Sucesso completo: Abre a janela principal do PDV
                var pdvWindow = new MainWindow(user!);
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

        // Atalhos de Login Rápido para Debug/Homologação
        private void TestLoginShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string email)
            {
                EmailInput.Text = email;
                PasswordInput.Password = "123";
                LoginButton_Click(LoginButton, new RoutedEventArgs());
            }
        }
    }
}
