using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Net.Http;
using System.Collections.Generic;
using Newtonsoft.Json;
using PdvPadaria.Models;
using PdvPadaria.Services;

namespace PdvPadaria.Views
{
    public partial class LoginWindow : Window
    {
        // HttpClient ÚNICO para todos os logins. Reusar evita esgotar sockets (TIME_WAIT 240s no Windows).
        private static readonly System.Net.Http.HttpClient _httpClient = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

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
                        // Login server-side: a função RPC verifica a senha no banco
                        // (pgcrypto) e devolve o usuário SEM o hash. A senha nunca é
                        // exposta para a anon key, e o hash não trafega pela rede.
                        var url = $"{supabaseUrl.TrimEnd('/')}/rest/v1/rpc/login_caixa";
                        var rpcBody = new StringContent(
                            JsonConvert.SerializeObject(new { p_email = email, p_senha = password }),
                            System.Text.Encoding.UTF8,
                            "application/json");

                        using var request = new HttpRequestMessage(HttpMethod.Post, url);
                        request.Content = rpcBody;
                        request.Headers.Add("apikey", supabaseAnonKey);
                        request.Headers.Add("Authorization", "Bearer " + supabaseAnonKey);

                        var response = await _httpClient.SendAsync(request);

                        if (response.IsSuccessStatusCode)
                        {
                            var responseText = await response.Content.ReadAsStringAsync();
                            var users = JsonConvert.DeserializeObject<List<User>>(responseText);

                            if (users != null && users.Count > 0)
                            {
                                var onlineUser = users[0];

                                if (string.IsNullOrWhiteSpace(onlineUser.TenantId))
                                {
                                    ShowError("Este usuário não está vinculado a uma rede. Corrija o cadastro antes de usar este caixa.");
                                    return;
                                }

                                // RPC já validou a senha no servidor. Grava localmente
                                // como hash BCrypt (derivado da senha digitada) para
                                // permitir login offline futuro deste operador.
                                onlineUser.Password = PasswordHasher.Hash(password);

                                var connection = App.Database.GetConnection();
                                int produtosDeOutraRede = await connection.ExecuteScalarAsync<int>(
                                    "SELECT COUNT(*) FROM Product WHERE TenantId IS NOT NULL " +
                                    "AND TenantId <> '' AND LOWER(TenantId) <> 'tenant-test' " +
                                    "AND Id NOT IN ('prod-pao-carioca', 'prod-pao-massa-fina') " +
                                    "AND LOWER(TenantId) <> LOWER(?)", onlineUser.TenantId);
                                if (produtosDeOutraRede > 0)
                                {
                                    ShowError("Este computador já pertence a outra rede. Por segurança, use uma instalação separada; nenhum dado local foi alterado.");
                                    return;
                                }
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

                    if (string.IsNullOrWhiteSpace(user.TenantId))
                    {
                        ShowError("Este usuário local não está vinculado a uma rede. Conecte a internet e entre novamente.");
                        return;
                    }

                    // Migracao transparente: se a senha local ainda estava em texto
                    // puro (base antiga), re-grava como hash BCrypt agora.
                    if (PasswordHasher.NeedsUpgrade(user.Password))
                    {
                        user.Password = PasswordHasher.Hash(password);
                        await connection.UpdateAsync(user);
                    }

                    // DONO enxerga todas as lojas da SUA rede, não de outro tenant. O
                    // catálogo local identifica a rede desta instalação mesmo offline.
                    int produtosDeOutraRede = await connection.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM Product WHERE TenantId IS NOT NULL " +
                        "AND TenantId <> '' AND LOWER(TenantId) <> 'tenant-test' " +
                        "AND Id NOT IN ('prod-pao-carioca', 'prod-pao-massa-fina') " +
                        "AND LOWER(TenantId) <> LOWER(?)", user.TenantId);
                    if (produtosDeOutraRede > 0)
                    {
                        ShowError("Este usuário pertence a outra rede e não pode operar esta instalação offline.");
                        return;
                    }

                    // Um usuário já usado neste PC fica disponível offline, mas isso não
                    // autoriza uma loja a operar o caixa de outra. DONO é global; operador
                    // de loja precisa combinar com a identidade já confirmada da máquina.
                    string lojaDoToken = StoreIdentityService.LojaDoTokenConhecida();
                    string lojaDoEstoque = StoreIdentityService.LojaDoEstoque();
                    if (string.IsNullOrWhiteSpace(lojaDoEstoque))
                    {
                        ShowError("Após esta atualização, a origem do estoque precisa ser confirmada uma vez. Conecte a internet e entre novamente; nenhum dado local será apagado.");
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(lojaDoToken)
                        && !string.IsNullOrWhiteSpace(lojaDoEstoque)
                        && !string.Equals(lojaDoToken, lojaDoEstoque, StringComparison.OrdinalIgnoreCase))
                    {
                        ShowError("Esta máquina está no meio de uma troca de loja. Conecte a internet e entre novamente para concluir sem misturar os estoques.");
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(user.StoreId))
                    {
                        string lojaConhecida = !string.IsNullOrWhiteSpace(lojaDoToken) ? lojaDoToken : lojaDoEstoque;
                        if (!string.IsNullOrWhiteSpace(lojaConhecida)
                            && !string.Equals(lojaConhecida, user.StoreId, StringComparison.OrdinalIgnoreCase))
                        {
                            ShowError("Este usuário pertence a outra loja. Para trocar esta máquina de loja, faça o primeiro acesso com internet.");
                            return;
                        }
                    }
                }

                // 3. Sucesso online: renova a credencial e conclui uma eventual troca de loja.
                if (isOnlineSuccess && user != null)
                {
                    // O login é o momento em que a máquina descobre de que loja ela é: o
                    // usuário do caixa já traz o storeId do cadastro. Se ela ainda não tem
                    // credencial de sincronização — ou tem uma que venceu, ou que responde
                    // por outra loja — ela pede a sua agora e guarda. É isto que dispensa
                    // alguém ir de PC em PC colar token em arquivo, que foi como duas lojas
                    // acabaram dias sem sincronizar.
                    string lojaDoUsuario = user.StoreId ?? string.Empty;
                    await StoreIdentityService.ResolverAsync(lojaDoUsuario);

                    // Usuário de loja vincula a máquina à sua loja. DONO mantém a loja da
                    // própria máquina e pode renovar o token dela sem ganhar uma loja fixa.
                    string lojaAlvo = lojaDoUsuario;
                    if (string.IsNullOrWhiteSpace(lojaAlvo))
                    {
                        lojaAlvo = !StoreIdentityService.TokenAusente && !StoreIdentityService.TokenInvalido
                            ? StoreIdentityService.StoreId
                            : StoreIdentityService.LojaDoTokenConhecida();
                    }

                    if (string.IsNullOrWhiteSpace(lojaAlvo))
                    {
                        ShowError("Esta máquina ainda não está ligada a uma loja. Entre uma vez, com internet, usando o usuário da loja que funcionará neste caixa.");
                        return;
                    }

                    bool semearEstoque = StoreIdentityService.PrecisaSemearEstoque(lojaAlvo);
                    if (semearEstoque)
                    {
                        using var prep = new SyncService(App.Database.GetSyncConnection());
                        var (ok, motivo) = prep.PodeTrocarDeLoja(lojaAlvo);
                        if (!ok)
                        {
                            MessageBox.Show(motivo, "Troca de loja adiada",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }

                    if (StoreIdentityService.PrecisaRegistrar(lojaAlvo))
                    {
                        bool registrou = await StoreIdentityService.RegistrarPeloLoginAsync(email, password, lojaAlvo);
                        System.Diagnostics.Debug.WriteLine($"[Registro do caixa]: {(registrou ? "ok" : "falhou")}");
                        if (!registrou)
                        {
                            ShowError("Não foi possível renovar o acesso desta máquina agora. Confira a internet e tente novamente; nenhum dado local foi apagado.");
                            return;
                        }
                    }

                    string tenantId = user.TenantId;
                    string storeId = StoreIdentityService.StoreId;
                    semearEstoque = StoreIdentityService.PrecisaSemearEstoque(storeId);

                    if (semearEstoque)
                    {
                        // Awaited de propósito: o caixa não pode abrir mostrando o estoque da
                        // loja anterior enquanto o da nova ainda está a caminho.
                        try
                        {
                            using var troca = new SyncService(App.Database.GetSyncConnection());
                            // Reconstroi a loja da nuvem + deltas locais ainda pendentes,
                            // envia a fila e publica a foto resultante.
                            bool carregou = await troca.PullUpdatesAsync(
                                tenantId, storeId, semearEstoqueDaNuvem: true);
                            // A partir daqui o SQLite ja pertence com seguranca a loja alvo.
                            // Persistir isso antes do ACK evita perder o delta se a venda subir
                            // e a foto falhar: o proximo login preserva o saldo local e repete.
                            bool confirmou = carregou
                                && StoreIdentityService.ConfirmarEstoqueDaLoja(storeId);
                            bool enviou = confirmou
                                && await troca.PushSalesAsync(tenantId, storeId);
                            bool publicou = enviou
                                && await troca.PushStockSnapshotAsync(tenantId, storeId);
                            if (!publicou)
                            {
                                ShowError("A loja foi identificada, mas o estoque não terminou de carregar. Tente entrar novamente com a internet funcionando; nenhum histórico foi apagado.");
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Pull da troca de loja]: {ex.Message}");
                            ShowError("Não foi possível carregar o estoque da loja. Tente entrar novamente; nenhum histórico foi apagado.");
                            return;
                        }
                    }
                    // O primeiro ciclo da MainWindow já faz o pull. Disparar outro aqui
                    // concorria com login/logout e podia aplicar dados da loja anterior.
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
