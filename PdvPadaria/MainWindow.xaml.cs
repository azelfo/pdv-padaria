using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PdvPadaria.Models;
using PdvPadaria.Services;
using PdvPadaria.Views;
using System.Runtime.InteropServices;

namespace PdvPadaria
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private void SafeShowHideTaskbar(bool show)
        {
            try
            {
                IntPtr taskbarHandle = FindWindow("Shell_TrayWnd", null);
                if (taskbarHandle != IntPtr.Zero)
                {
                    ShowWindow(taskbarHandle, show ? SW_SHOW : SW_HIDE);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erro ao gerenciar barra de tarefas: {ex.Message}");
            }
        }

        public User CurrentUser { get; private set; }
        private readonly ObservableCollection<CartItemView> _cartItems = new ObservableCollection<CartItemView>();
        
        // Variáveis para salvar o estado e dimensões da janela antes do Kiosk Mode
        private double _prevLeft;
        private double _prevTop;
        private double _prevWidth;
        private double _prevHeight;
        private WindowState _prevWindowState = WindowState.Maximized;
        
        // Controle de seletores e estados de rede para Dashboard
        private bool _dashSelectorReady = false;
        private string _selectedPaymentMethod = string.Empty;
        private int _subtotalCentavos = 0;
        private int _discountCentavos = 0;
        private int _totalCentavos = 0;
        private DispatcherTimer _syncTimer = null!;
        private readonly System.Threading.SemaphoreSlim _syncGate = new System.Threading.SemaphoreSlim(1, 1);
        private readonly System.Threading.SemaphoreSlim _saleGate = new System.Threading.SemaphoreSlim(1, 1);
        private bool _encerrando;
        private readonly EscopoDeSessao _escopo;
        // Segunda passada pelo Closing: o encerramento é assíncrono e o WPF não espera,
        // então o primeiro Closing cancela o fechamento, encerra a sessão e fecha de novo.
        private bool _sessaoEncerrada;
        private string _donoEmail = string.Empty;
        private string _donoPassword = string.Empty;
        private int _redePeriodoDias = 0;
        private static readonly System.Net.Http.HttpClient _redeHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Abas do DONO com filtro por loja: "" = minha loja (local); senão = id da loja remota.
        private string _stockRemoteStoreId = string.Empty;
        private string _alertRemoteStoreId = string.Empty;
        private string _historyRemoteStoreId = string.Empty;
        private bool _stockSelectorReady = false;
        private bool _alertSelectorReady = false;
        private bool _historySelectorReady = false;
        private bool _suppressStockStoreEvent = false;
        private List<RedeLojaView> _lojasCache = new List<RedeLojaView>();
        private int _valorRecebidoCentavos;
        private string _stockCategoryFilter = "TODOS";
        private List<StockProductView> _remoteStockCache = new List<StockProductView>();

        public enum PdvState
        {
            Consultation,
            ActiveSale
        }

        private PdvState _currentState = PdvState.Consultation;
        private DateTime _lastBarcodeEvent = DateTime.MinValue;
        private readonly System.Text.StringBuilder _barcodeAccumulator = new System.Text.StringBuilder();

        public MainWindow(User user, EscopoDeSessao escopo, string loginEmail = "", string loginPassword = "")
        {
            InitializeComponent();
            CurrentUser = user;
            // Credenciais guardadas em memória só para o painel da rede (RPC exige login do dono).
            _donoEmail = loginEmail;
            _donoPassword = loginPassword;

            // A sessão nasce no login, com a loja já decidida, e termina quando esta janela
            // terminar — por logout OU pelo X. Recebê-la pronta (em vez de montar aqui) é o
            // que garante que a tela e o sincronizador falam da MESMA sessão.
            _escopo = escopo ?? throw new ArgumentNullException(nameof(escopo));

            OperatorNameText.Text = CurrentUser.Name;
            CartItemsList.ItemsSource = _cartItems;

            // Restrição de Acesso: Atendente possui acesso exclusivo à Frente de Caixa
            if (CurrentUser.Role.ToUpper() == "ATENDENTE")
            {
                BtnEstoque.Visibility = Visibility.Collapsed;
                BtnVendas.Visibility = Visibility.Collapsed;
                BtnDashboard.Visibility = Visibility.Collapsed;
                BtnAlertas.Visibility = Visibility.Collapsed;
            }

            // Painel da Rede (consolidado das lojas) é exclusivo do DONO.
            if (CurrentUser.Role.ToUpper() != "DONO")
            {
                BtnRede.Visibility = Visibility.Collapsed;
            }

            this.Deactivated += Window_Deactivated;
            Loaded += (s, e) => SetPdvState(PdvState.Consultation);
        }

        private void Window_Deactivated(object? sender, EventArgs e)
        {
            try
            {
                if (KioskToggle.IsChecked == true)
                {
                    this.Topmost = true;
                    this.Activate();
                    this.Focus();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erro ao forçar foco no Kiosk: {ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            SafeShowHideTaskbar(true);
        }



        // ViewModel auxiliar para renderizar a linha do carrinho de compras
        public class CartItemView
        {
            public string CartItemId { get; set; } = string.Empty;
            public string ProductId { get; set; } = string.Empty;
            public string ProductName { get; set; } = string.Empty;
            public string ProductType { get; set; } = "NORMAL";
            public double Quantity { get; set; }
            public int PriceUnit { get; set; }
            public int Subtotal { get; set; }
            public string Variation { get; set; } = "NORMAL";
            public string Details { get; set; } = string.Empty;
            // Item veio de etiqueta de peso: preço já veio pronto na etiqueta, então
            // não faz sentido mexer na quantidade com os botões +/- (cada etiqueta é
            // um pacote físico com aquele peso exato).
            public bool IsPesado { get; set; } = false;

            public string QuantityString => ProductType == "PAO_FRANCES" || Quantity % 1 == 0
                ? $"{Quantity:F0} UN"
                : $"{Quantity:F3} KG";

            public string PriceUnitString => $"R$ {PriceUnit / 100.0:F2}";
            public string SubtotalString => $"R$ {Subtotal / 100.0:F2}";

            public bool IsBolo => ProductType == "BOLO";
            public bool IsNotPao => ProductType != "PAO_FRANCES";
            public Visibility BoloVisibility => IsBolo ? Visibility.Visible : Visibility.Collapsed;
            public Visibility NormalQuantityVisibility => (IsNotPao && !IsPesado) ? Visibility.Visible : Visibility.Collapsed;
            public Visibility DetailsVisibility => string.IsNullOrEmpty(Details) ? Visibility.Collapsed : Visibility.Visible;
        }

        // ViewModels adicionais para as novas abas
        public class StockProductView
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string CategoryName { get; set; } = string.Empty;
            public string BarcodeString => string.IsNullOrEmpty(Barcode) ? "-" : Barcode;
            public string Barcode { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public string UnitMeasure { get; set; } = string.Empty;
            public int PriceSale { get; set; }
            public int PriceCost { get; set; }
            public double LocalStockQuantity { get; set; }
            public double MinStock { get; set; }

            public string PriceSaleString => $"R$ {PriceSale / 100.0:F2}";
            public string PriceCostString => $"R$ {PriceCost / 100.0:F2}";
            public string StockString => UnitMeasure == "KG" ? $"{LocalStockQuantity:F3} KG" : $"{LocalStockQuantity:F0} UN";
            public bool IsLowStock => LocalStockQuantity <= MinStock;
        }

        public class SalesHistoryView
        {
            public string Id { get; set; } = string.Empty;
            public string StoreId { get; set; } = string.Empty; // loja da venda (usado no modo "Todas as sedes")
            public DateTime SaleDate { get; set; }
            public int Total { get; set; }
            public string PaymentMethod { get; set; } = string.Empty;
            public string PaymentStatus { get; set; } = string.Empty;
            public double ItemCount { get; set; }

            public string FormattedDate => SaleDate.ToString("dd/MM/yyyy HH:mm");
            public string ShortId => Id.Length > 8 ? Id.Substring(0, 8) : Id;
            public string TotalString => $"R$ {Total / 100.0:F2}";
            public bool IsCanceled => PaymentStatus == "CANCELADO";
            public string ItemCountString => ItemCount % 1 == 0 ? $"{ItemCount:F0}" : $"{ItemCount:F3}";
        }

        public class DashboardTopProduct
        {
            public string ProductName { get; set; } = string.Empty;
            public double Quantity { get; set; }
            public string UnitMeasure { get; set; } = "UN";
            public int TotalCents { get; set; }

            public string QuantityString => UnitMeasure == "KG" ? $"{Quantity:F3} KG" : $"{Quantity:F0} UN";
            public string TotalString => $"R$ {TotalCents / 100.0:F2}";
        }

        // Linha de "SELECT DISTINCT ProductId FROM StockMovement": marca quais produtos já
        // passaram por algum lançamento de estoque nesta máquina.
        private class ProdutoIdRow
        {
            public string ProductId { get; set; } = string.Empty;
        }

        public class StockAlertView
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string CategoryName { get; set; } = string.Empty;
            public string BarcodeString => string.IsNullOrEmpty(Barcode) ? "-" : Barcode;
            public string Barcode { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public string UnitMeasure { get; set; } = string.Empty;
            public double LocalStockQuantity { get; set; }
            public double MinStock { get; set; }

            public string MinStockString => UnitMeasure == "KG" ? $"{MinStock:F3} KG" : $"{MinStock:F0} UN";
            public string StockString => UnitMeasure == "KG" ? $"{LocalStockQuantity:F3} KG" : $"{LocalStockQuantity:F0} UN";

            // A lista de alertas só recebe produto que ACABOU (zerado, mas já lançado aqui
            // alguma vez) ou que está NO MÍNIMO. Quem nunca teve contagem fica fora — ver
            // LoadLowStockAlerts.
            public string StatusAlerta => LocalStockQuantity <= 0 ? "ACABOU" : "NO MÍNIMO";

            public System.Windows.Media.Brush BadgeBackground => LocalStockQuantity <= 0 
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(38, 239, 68, 68))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(38, 245, 158, 11));

            public System.Windows.Media.Brush BadgeForeground => LocalStockQuantity <= 0
                ? AppColors.Danger
                : AppColors.Accent;
        }



        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Inicializa DatePickers com a data de hoje
            HistoryStartDatePicker.SelectedDate = DateTime.Today;
            HistoryEndDatePicker.SelectedDate = DateTime.Today;
            DashStartDatePicker.SelectedDate = DateTime.Today;
            DashEndDatePicker.SelectedDate = DateTime.Today;

            // Inicializa e agenda a sincronização periódica a cada 60 segundos (1 minuto)
            _syncTimer = new DispatcherTimer();
            _syncTimer.Interval = TimeSpan.FromSeconds(60);
            _syncTimer.Tick += async (s, ev) => await RunSincronizacaoSilenciosa();
            _syncTimer.Start();

            // Descobre de que loja é esta máquina ANTES do primeiro sync: quem manda é o
            // token, e é dele que sai também o estoque que o caixa vai mostrar. Sem isto,
            // o primeiro ciclo ainda leria pela linha STORE_ID do .env.
            await StoreIdentityService.ResolverAsync(CurrentUser.StoreId);
            AvisarSeIdentidadeEstiverErrada();

            // Roda a primeira sincronização assim que abre
            await RunSincronizacaoSilenciosa();

            // Checagem de atualização em background — não atrasa a abertura do caixa.
            _ = CheckForUpdateAsync();
        }

        // Avisa o operador, em português claro, quando a configuração desta máquina está
        // impedindo venda e estoque de circularem. Só fala quando há problema real de
        // configuração: estar sem internet não dispara aviso nenhum.
        private void AvisarSeIdentidadeEstiverErrada()
        {
            try
            {
                if (StoreIdentityService.TokenAusente || StoreIdentityService.TokenInvalido)
                {
                    SyncStatusText.Text = "Token da loja invalido";
                    SyncStatusIndicator.Fill = System.Windows.Media.Brushes.Red;

                    string motivo = StoreIdentityService.TokenAusente
                        ? "Esta maquina ainda nao esta ligada a nenhuma loja."
                        : "A credencial de sincronizacao desta maquina nao vale mais.";

                    MessageBox.Show(
                        motivo + "\n\n" +
                        "Enquanto isso, NENHUMA venda e NENHUM estoque deste caixa sobe para a nuvem." +
                        "\n\nO caixa continua vendendo normalmente e nada se perde: as vendas ficam " +
                        "guardadas aqui e sobem sozinhas assim que a ligacao for refeita." +
                        "\n\nCOMO RESOLVER: confira a internet, saia e entre novamente. O caixa " +
                        "renova a credencial sozinho - nao precisa mexer em nenhum arquivo.",
                        "Configuracao desta maquina", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AvisarSeIdentidadeEstiverErrada]: {ex.Message}");
            }
        }

        // Verifica se há versão nova (docs/version.json no GitHub) e oferece atualizar.
        // Nunca interrompe o uso do caixa: se offline ou sem update, não faz nada.
        private async Task CheckForUpdateAsync()
        {
            var info = await Services.UpdateService.CheckForUpdateAsync();
            if (info == null) return;

            var msg = $"Nova versão {info.Version} disponível.\n\n{info.Notes}\n\nAtualizar agora? O programa fecha e reabre sozinho em instantes.";
            if (MessageBox.Show(msg, "Atualização disponível", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                return;

            bool started = await Services.UpdateService.DownloadAndInstallAsync(info);
            if (!started)
            {
                MessageBox.Show("Não foi possível baixar a atualização agora. Tente novamente mais tarde.",
                    "Atualização", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Navegação Lateral
        private async void SidebarBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string indexStr && int.TryParse(indexStr, out int index))
            {
                // Reseta estilos de todos os botões laterais
                BtnPdv.Background = System.Windows.Media.Brushes.Transparent;
                BtnPdv.Foreground = AppColors.TextMuted;
                BtnEstoque.Background = System.Windows.Media.Brushes.Transparent;
                BtnEstoque.Foreground = AppColors.TextMuted;
                BtnVendas.Background = System.Windows.Media.Brushes.Transparent;
                BtnVendas.Foreground = AppColors.TextMuted;
                BtnDashboard.Background = System.Windows.Media.Brushes.Transparent;
                BtnDashboard.Foreground = AppColors.TextMuted;
                BtnAlertas.Background = System.Windows.Media.Brushes.Transparent;
                BtnAlertas.Foreground = AppColors.TextMuted;
                BtnRede.Background = System.Windows.Media.Brushes.Transparent;
                BtnRede.Foreground = AppColors.TextMuted;
                BtnEtiquetas.Background = System.Windows.Media.Brushes.Transparent;
                BtnEtiquetas.Foreground = AppColors.TextMuted;

                // Destaca o botão selecionado
                btn.Background = AppColors.Surface;
                btn.Foreground = AppColors.Accent;

                // Troca a aba do TabControl
                MainTabControl.SelectedIndex = index;

                // Carrega dados específicos da aba
                if (index == 1)
                {
                    await SetupStockStoreSelector();
                    UpdateStockFilterButtons();
                    if (string.IsNullOrEmpty(_stockRemoteStoreId)) LoadStock();
                    else _ = LoadRemoteStock(_stockRemoteStoreId);
                }
                else if (index == 2)
                {
                    // Aguarda o seletor popular ANTES de decidir local vs remoto. Sem o await, o
                    // setup async ainda não definiu _historyRemoteStoreId e o histórico carregava
                    // as vendas locais enquanto o dropdown já mostrava a 1ª loja (filtro fantasma).
                    await SetupHistoryStoreSelector();
                    if (_historyRemoteStoreId == "TODAS") _ = LoadAllStoresHistory();
                    else if (string.IsNullOrEmpty(_historyRemoteStoreId)) LoadSalesHistory();
                    else _ = LoadRemoteHistory(_historyRemoteStoreId);
                }
                else if (index == 3)
                {
                    // await para o seletor (com "Todas as sedes") existir antes do load; senão a
                    // 1a abertura caía no dashboard LOCAL enquanto o seletor ainda populava.
                    await SetupDashStoreSelector();
                    LoadDashboard();
                }
                else if (index == 4)
                {
                    await SetupAlertStoreSelector();
                    if (string.IsNullOrEmpty(_alertRemoteStoreId)) LoadLowStockAlerts();
                    else _ = LoadRemoteAlerts(_alertRemoteStoreId);
                }
                else if (index == 5)
                {
                    _ = LoadRede();
                }
                else if (index == 6)
                {
                    LoadEtiquetasProdutos();
                }
            }
        }

        // ================= ABA 6: ETIQUETAS (impressão de código de barras) =================

        // Item da lista de produtos da aba Etiquetas (não altera nada, só exibição).
        public class EtiquetaProdView
        {
            public Product Produto { get; set; } = null!;
            public string Name => Produto.Name;
            public string BarcodeDisplay => string.IsNullOrEmpty(Produto.Barcode) ? "— sem código —" : Produto.Barcode!;
            public string PriceDisplay => $"R$ {Produto.PriceSale / 100.0:F2}";
        }

        private List<EtiquetaProdView> _etqTodos = new List<EtiquetaProdView>();
        private Product? _etqSelecionado;

        private async void LoadEtiquetasProdutos()
        {
            try
            {
                var connection = App.Database.GetConnection();
                var produtos = await connection.Table<Product>().Where(p => p.Active).ToListAsync();
                _etqTodos = produtos
                    .OrderBy(p => p.Name)
                    .Select(p => new EtiquetaProdView { Produto = p })
                    .ToList();
                AplicarFiltroEtiquetas(EtqSearchBox.Text.Trim());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao carregar produtos: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AplicarFiltroEtiquetas(string filtro)
        {
            IEnumerable<EtiquetaProdView> lista = _etqTodos;
            if (!string.IsNullOrWhiteSpace(filtro))
            {
                lista = _etqTodos.Where(v =>
                    ContemIgnorandoCaixa(v.Name, filtro) ||
                    ContemIgnorandoCaixa(v.Produto.Barcode, filtro));
            }
            EtqProductsList.ItemsSource = lista.ToList();
        }

        private void EtqSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            SincronizarPlaceholder(EtqSearchBox, EtqSearchPlaceholder);
            if (_etqTodos.Count > 0) AplicarFiltroEtiquetas(EtqSearchBox.Text.Trim());
        }

        private void EtqProductsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _etqSelecionado = (EtqProductsList.SelectedItem as EtiquetaProdView)?.Produto;
            if (EtqPesoBox != null) EtqPesoBox.Text = string.Empty; // peso é por pesagem, não reaproveita
            AtualizarPreviewEtiqueta();
        }

        private void EtqPreco_Changed(object sender, RoutedEventArgs e)
        {
            AtualizarPreviewEtiqueta();
        }

        private void EtqPesoBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            AtualizarPreviewEtiqueta();
        }

        private bool EtqEhPesado => _etqSelecionado != null && _etqSelecionado.UnitMeasure == "KG";

        // Código e preço que serão realmente impressos (para peso, mudam a cada pesagem).
        private string? _etqCodigoImprimir;
        private int _etqPrecoImprimir;

        private void AtualizarPreviewEtiqueta()
        {
            // O checkbox "Mostrar preço" tem IsChecked=True, então o evento Checked dispara
            // durante o InitializeComponent — antes de EtqImprimirBtn/EtqStatus existirem.
            // Sem este guard, dava NullReference já na abertura da janela (crash no login).
            if (EtqImprimirBtn == null || EtqStatus == null || EtqPesoPanel == null) return;

            _etqCodigoImprimir = null;
            _etqPrecoImprimir = 0;

            if (_etqSelecionado == null)
            {
                EtqPreviewName.Text = "Selecione um produto";
                EtqPreviewBarcode.Source = null;
                EtqPreviewCode.Text = "";
                EtqPreviewPrice.Text = "";
                EtqPesoPanel.Visibility = Visibility.Collapsed;
                EtqImprimirBtn.IsEnabled = false;
                EtqStatus.Text = "";
                return;
            }

            EtqPreviewName.Text = _etqSelecionado.Name;
            EtqPesoPanel.Visibility = EtqEhPesado ? Visibility.Visible : Visibility.Collapsed;

            if (string.IsNullOrEmpty(_etqSelecionado.Barcode))
            {
                // Produto sem código não dá para etiquetar (nada para o leitor bipar).
                EtqPreviewBarcode.Source = null;
                EtqPreviewCode.Text = "sem código de barras";
                EtqPreviewPrice.Text = "";
                EtqImprimirBtn.IsEnabled = false;
                EtqStatus.Text = "Este produto não tem código de barras. Cadastre/gere um código antes de etiquetar.";
                return;
            }

            if (EtqEhPesado) AtualizarPreviewPeso();
            else AtualizarPreviewUnidade();
        }

        // Etiqueta comum: o código do produto vai direto, preço fixo do cadastro.
        private void AtualizarPreviewUnidade()
        {
            _etqCodigoImprimir = _etqSelecionado!.Barcode;
            _etqPrecoImprimir = _etqSelecionado.PriceSale;

            EtqPreviewBarcode.Source = Services.BarcodeService.Gerar(_etqCodigoImprimir);
            EtqPreviewCode.Text = _etqCodigoImprimir;
            EtqPreviewPrice.Text = (EtqMostrarPreco.IsChecked == true) ? $"R$ {_etqPrecoImprimir / 100.0:F2}" : "";
            EtqImprimirBtn.IsEnabled = EtqPreviewBarcode.Source != null;
            EtqStatus.Text = EtqPreviewBarcode.Source == null ? "Não foi possível gerar o código de barras." : "";
        }

        // Etiqueta de peso: preço = peso digitado x preço do quilo, embutido no código de barras.
        private void AtualizarPreviewPeso()
        {
            var prod = _etqSelecionado!;
            EtqPrecoKgInfo.Text = $"Preço do quilo: R$ {prod.PriceSale / 100.0:F2}";

            void Bloqueia(string msg, string aviso = "")
            {
                EtqPreviewBarcode.Source = null;
                EtqPreviewCode.Text = "";
                EtqPreviewPrice.Text = "";
                EtqPesoTotal.Text = msg;
                EtqImprimirBtn.IsEnabled = false;
                EtqStatus.Text = aviso;
            }

            if (!Services.WeightBarcodeService.SuportaEtiquetaPeso(prod.Barcode))
            {
                Bloqueia("", "Para etiqueta de peso o produto precisa de um código interno (gerado pelo botão \"Gerar\" no cadastro). O código atual é de fábrica ou digitado à mão.");
                return;
            }

            if (prod.PriceSale <= 0)
            {
                Bloqueia("", "Cadastre o preço do quilo neste produto antes de etiquetar.");
                return;
            }

            string pesoTexto = (EtqPesoBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(pesoTexto))
            {
                Bloqueia("", "Digite o peso lido na balança.");
                return;
            }

            if (!TryParseDouble(pesoTexto, out double pesoKg) || pesoKg <= 0)
            {
                Bloqueia("", "Peso inválido. Use por exemplo 0,300 para 300 gramas.");
                return;
            }

            int precoCentavos = (int)Math.Round(prod.PriceSale * pesoKg);
            if (precoCentavos <= 0)
            {
                Bloqueia("", "Peso muito pequeno para gerar preço.");
                return;
            }
            if (precoCentavos > Services.WeightBarcodeService.PrecoMaximoCentavos)
            {
                Bloqueia("", $"Valor acima do limite da etiqueta (R$ {Services.WeightBarcodeService.PrecoMaximoCentavos / 100.0:F2}). Divida em pacotes menores.");
                return;
            }

            string? codigo = Services.WeightBarcodeService.Gerar(prod.Barcode, precoCentavos);
            if (codigo == null)
            {
                Bloqueia("", "Não foi possível gerar o código de peso deste produto.");
                return;
            }

            _etqCodigoImprimir = codigo;
            _etqPrecoImprimir = precoCentavos;

            EtqPesoTotal.Text = $"{pesoKg:F3} kg  =  R$ {precoCentavos / 100.0:F2}";
            EtqPreviewBarcode.Source = Services.BarcodeService.Gerar(codigo);
            EtqPreviewCode.Text = codigo;
            EtqPreviewPrice.Text = (EtqMostrarPreco.IsChecked == true) ? $"R$ {precoCentavos / 100.0:F2}" : "";
            EtqImprimirBtn.IsEnabled = EtqPreviewBarcode.Source != null;
            EtqStatus.Text = "";
        }

        private void EtqImprimir_Click(object sender, RoutedEventArgs e)
        {
            if (_etqSelecionado == null || string.IsNullOrEmpty(_etqCodigoImprimir)) return;

            int qtd;
            if (EtqEhPesado)
            {
                // Cada etiqueta de peso vale por UM pacote pesado. Imprimir várias iguais
                // permitiria colar o mesmo preço em pacotes de pesos diferentes.
                qtd = 1;
            }
            else if (!int.TryParse(EtqQuantidade.Text.Trim(), out qtd) || qtd < 1 || qtd > 200)
            {
                MessageBox.Show("Informe uma quantidade entre 1 e 200.", "Etiquetas", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var barcodeImg = Services.BarcodeService.Gerar(_etqCodigoImprimir, 600, 180);
            if (barcodeImg == null)
            {
                MessageBox.Show("Não foi possível gerar o código de barras para impressão.", "Etiquetas", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var dlg = new System.Windows.Controls.PrintDialog();
            if (dlg.ShowDialog() != true) return;

            bool mostrarPreco = EtqMostrarPreco.IsChecked == true;
            string nome = _etqSelecionado.Name;
            string codigo = _etqCodigoImprimir!;
            string preco = $"R$ {_etqPrecoImprimir / 100.0:F2}";

            var doc = new System.Windows.Documents.FixedDocument();
            for (int i = 0; i < qtd; i++)
            {
                var page = new System.Windows.Documents.FixedPage
                {
                    Width = MmParaDiu(50),
                    Height = MmParaDiu(30)
                };
                page.Children.Add(ConstruirEtiqueta(nome, barcodeImg, codigo, mostrarPreco ? preco : null));
                var pageContent = new System.Windows.Documents.PageContent();
                ((System.Windows.Markup.IAddChild)pageContent).AddChild(page);
                doc.Pages.Add(pageContent);
            }

            try
            {
                dlg.PrintDocument(doc.DocumentPaginator, $"Etiquetas - {nome}");
                EtqStatus.Text = $"✓ {qtd} etiqueta(s) enviada(s) para impressão.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao imprimir: {ex.Message}", "Etiquetas", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Monta o visual de UMA etiqueta (50x30mm): nome em cima, código de barras no meio,
        // número + preço embaixo. Cada página de impressão precisa da própria instância.
        private FrameworkElement ConstruirEtiqueta(string nome, System.Windows.Media.Imaging.BitmapSource barcode, string codigo, string? preco)
        {
            var painel = new StackPanel
            {
                Width = MmParaDiu(50),
                Height = MmParaDiu(30),
                Background = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            painel.Children.Add(new TextBlock
            {
                Text = nome,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.Black,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 26,
                Margin = new Thickness(2, 2, 2, 0)
            });
            painel.Children.Add(new System.Windows.Controls.Image
            {
                Source = barcode,
                Height = MmParaDiu(11),
                Stretch = System.Windows.Media.Stretch.Uniform,
                Margin = new Thickness(2, 1, 2, 0)
            });
            painel.Children.Add(new TextBlock
            {
                Text = codigo,
                FontSize = 8,
                Foreground = System.Windows.Media.Brushes.Black,
                TextAlignment = TextAlignment.Center
            });
            if (!string.IsNullOrEmpty(preco))
            {
                painel.Children.Add(new TextBlock
                {
                    Text = preco,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = System.Windows.Media.Brushes.Black,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 0)
                });
            }
            return painel;
        }

        // Milímetros -> unidades WPF (1 polegada = 96 DIU = 25.4 mm).
        private static double MmParaDiu(double mm) => mm * 96.0 / 25.4;

        // ================= ABA 0: FRENTE DE CAIXA (PDV) LOGIC =================

        // Placeholder das caixas de busca: a visibilidade segue o TEXTO digitado, não o foco.
        // Antes um único par GotFocus/LostFocus era compartilhado por caixas diferentes, então
        // digitar numa escondia o rótulo da outra e o texto ficava sobreposto ao placeholder.
        private static void SincronizarPlaceholder(TextBox? caixa, TextBlock? placeholder)
        {
            if (caixa == null || placeholder == null) return;
            placeholder.Visibility = string.IsNullOrEmpty(caixa.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            SincronizarPlaceholder(SearchBox, SearchPlaceholder);
            if (_catalogoBusca.Count == 0) await ObterCatalogoAsync();
            AtualizarSugestoes(SearchBox.Text, VendaSugestoesList, VendaSugestoesPopup);
        }

        private async void ConsultationSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            SincronizarPlaceholder(ConsultationSearchBox, ConsultationPlaceholder);
            if (_catalogoBusca.Count == 0) await ObterCatalogoAsync();
            AtualizarSugestoes(ConsultationSearchBox.Text, ConsultationSugestoesList, ConsultationSugestoesPopup);
        }

        // Clique numa sugestão: lança no carrinho (venda) ou mostra os dados (consulta).
        private void VendaSugestoes_Click(object sender, MouseButtonEventArgs e)
        {
            UsarSugestaoVenda();
        }

        private void ConsultationSugestoes_Click(object sender, MouseButtonEventArgs e)
        {
            UsarSugestaoConsulta();
        }

        private void UsarSugestaoVenda()
        {
            int i = VendaSugestoesList.SelectedIndex;
            if (i < 0 || i >= _sugestoesAtuais.Count) return;
            var produto = _sugestoesAtuais[i];

            VendaSugestoesPopup.IsOpen = false;
            SearchBox.Text = string.Empty;
            SearchBox.Focus();

            if (produto.Type == "PAO_FRANCES") PromptPaoFrances(produto);
            else AddProductToCart(produto, 1.0);
        }

        private async void UsarSugestaoConsulta()
        {
            int i = ConsultationSugestoesList.SelectedIndex;
            if (i < 0 || i >= _sugestoesAtuais.Count) return;
            var produto = _sugestoesAtuais[i];

            ConsultationSugestoesPopup.IsOpen = false;
            ConsultationSearchBox.Text = string.Empty;
            await MostrarConsulta(produto);
        }

        // Preenche o cartão de detalhes do produto na aba de consulta.
        private async Task MostrarConsulta(Product produto)
        {
            ConsultationProductName.Text = produto.Name;
            ConsultationProductPrice.Text = $"R$ {produto.PriceSale / 100.0:F2}";
            ConsultationProductStock.Text = $"Estoque: {(produto.UnitMeasure == "KG" ? $"{produto.LocalStockQuantity:F3} KG" : $"{produto.LocalStockQuantity:F0} UN")}";
            ConsultationBarcode.Text = produto.Barcode ?? "-";
            try
            {
                var connection = App.Database.GetConnection();
                var categoria = await connection.FindAsync<Category>(produto.CategoryId);
                ConsultationProductCategory.Text = $"Categoria: {(categoria != null ? categoria.Name : "Geral")}";
            }
            catch
            {
                ConsultationProductCategory.Text = "Categoria: Geral";
            }
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            // Com a lista de sugestões aberta, as setas navegam e o Enter escolhe.
            // O leitor de código de barras não é afetado: ele digita rápido e o texto casa
            // exatamente com um código, então nem chega a abrir sugestão.
            if (VendaSugestoesPopup.IsOpen && NavegarSugestoes(e, VendaSugestoesList))
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter)
            {
                if (VendaSugestoesPopup.IsOpen && VendaSugestoesList.SelectedIndex >= 0)
                {
                    e.Handled = true;
                    UsarSugestaoVenda();
                    return;
                }
                ExecuteSearch();
            }
            else if (e.Key == Key.Escape && VendaSugestoesPopup.IsOpen)
            {
                e.Handled = true;
                VendaSugestoesPopup.IsOpen = false;
            }
        }

        // Move a seleção da lista de sugestões com as setas. Retorna true se tratou a tecla.
        private static bool NavegarSugestoes(KeyEventArgs e, ListBox lista)
        {
            if (lista.Items.Count == 0) return false;
            if (e.Key == Key.Down)
            {
                lista.SelectedIndex = (lista.SelectedIndex + 1) % lista.Items.Count;
                lista.ScrollIntoView(lista.SelectedItem);
                return true;
            }
            if (e.Key == Key.Up)
            {
                lista.SelectedIndex = lista.SelectedIndex <= 0 ? lista.Items.Count - 1 : lista.SelectedIndex - 1;
                lista.ScrollIntoView(lista.SelectedItem);
                return true;
            }
            return false;
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            ExecuteSearch();
        }

        private async void ExecuteSearch()
        {
            var query = SearchBox.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;

            SearchBox.Text = string.Empty;
            SearchPlaceholder.Visibility = Visibility.Visible;
            SearchBox.Focus();

            try
            {
                var connection = App.Database.GetConnection();

                // Código de barras exato primeiro: é o caminho do leitor, e precisa lançar direto.
                var product = await connection.Table<Product>()
                    .Where(p => p.Barcode == query && p.Active)
                    .FirstOrDefaultAsync();

                if (product == null)
                {
                    // Busca por nome ignorando acento (antes "pao" não achava "Pão").
                    await ObterCatalogoAsync();
                    var achados = BuscarProdutos(query, 30);

                    if (achados.Count == 0)
                    {
                        MessageBox.Show($"Nenhum produto encontrado para '{query}'.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (achados.Count > 1)
                    {
                        // Antes o sistema lançava silenciosamente o PRIMEIRO da lista, o que
                        // podia colocar no carrinho um produto diferente do pretendido.
                        SearchBox.Text = query;
                        AtualizarSugestoes(query, VendaSugestoesList, VendaSugestoesPopup);
                        SearchBox.Focus();
                        return;
                    }
                    product = achados[0];
                }

                if (product.Type == "PAO_FRANCES") PromptPaoFrances(product);
                else AddProductToCart(product, 1.0);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro na consulta: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AddProductToCart(Product product, double quantity)
        {
            double jaNoCarrinho = _cartItems.Where(i => i.ProductId == product.Id).Sum(i => i.Quantity);
            double disponivel = product.LocalStockQuantity - jaNoCarrinho;
            if (disponivel < quantity)
            {
                string estoqueStr = product.UnitMeasure == "KG"
                    ? $"{product.LocalStockQuantity:F3} KG"
                    : $"{(int)product.LocalStockQuantity} UN";
                MessageBox.Show(
                    $"Estoque insuficiente para '{product.Name}'.\nDisponível: {estoqueStr}.",
                    "Sem Estoque", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int itemSubtotal = (int)Math.Round(product.PriceSale * quantity);

            var existing = _cartItems.FirstOrDefault(i => i.CartItemId == product.Id);
            if (existing != null)
            {
                existing.Quantity += quantity;
                existing.Subtotal += itemSubtotal;
                
                int index = _cartItems.IndexOf(existing);
                _cartItems[index] = existing;
            }
            else
            {
                _cartItems.Add(new CartItemView
                {
                    CartItemId = product.Id,
                    ProductId = product.Id,
                    ProductName = product.Name,
                    ProductType = product.Type,
                    Quantity = quantity,
                    PriceUnit = product.PriceSale,
                    Subtotal = itemSubtotal
                });
            }

            UpdateTotals();
        }

        private void RemoveItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string cartItemId)
            {
                var item = _cartItems.FirstOrDefault(i => i.CartItemId == cartItemId);
                if (item != null)
                {
                    _cartItems.Remove(item);
                    UpdateTotals();
                }
            }
        }

        private void LaunchBreadButton_Click(object sender, RoutedEventArgs e)
        {
            LaunchBreadFlow();
        }

        private async void LaunchBreadFlow()
        {
            try
            {
                var connection = App.Database.GetConnection();
                var paes = await connection.Table<Product>()
                    .Where(p => p.Type == "PAO_FRANCES" && p.Active)
                    .ToListAsync();
                paes = paes.OrderBy(p => p.Name).ToList();

                if (paes.Count == 0)
                {
                    MessageBox.Show(
                        "Nenhum pão cadastrado.\n\nCadastre o produto em Estoque e escolha o tipo \"Pão\" para ele aparecer aqui.",
                        "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // A própria janela de lançamento já lista os pães em botões (e aceita as teclas
                // 1..N), então não existe passo intermediário de escolha: abre direto nela.
                PromptPaoFrances(paes[0]);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao buscar pão: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ================= BUSCA DE PRODUTO (sugestões enquanto digita) =================

        // Normaliza para comparar: tira acento e caixa. Sem isso, "pao" não achava "Pão"
        // e "ACUCAR" não achava "Açúcar" — o LIKE do SQLite só ignora caixa em ASCII.
        private static string Normalizar(string? texto)
        {
            if (string.IsNullOrEmpty(texto)) return string.Empty;
            var decomposto = texto!.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposto.Length);
            foreach (var ch in decomposto)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().ToUpperInvariant();
        }

        // Catálogo em memória para a busca. São ~100 produtos, então filtrar em memória é
        // instantâneo e permite comparar ignorando acento (o que o SQL não faria).
        private List<Product> _catalogoBusca = new List<Product>();

        private async Task<List<Product>> ObterCatalogoAsync()
        {
            try
            {
                var connection = App.Database.GetConnection();
                _catalogoBusca = await connection.Table<Product>().Where(p => p.Active).ToListAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ObterCatalogo Error]: {ex.Message}");
            }
            return _catalogoBusca;
        }

        // Produtos que casam com o texto digitado, melhores primeiro:
        // começa com o termo > contém o termo > casa pelo código de barras.
        private List<Product> BuscarProdutos(string termo, int limite = 8)
        {
            var alvo = Normalizar(termo);
            if (alvo.Length == 0) return new List<Product>();

            return _catalogoBusca
                .Select(p => new { P = p, N = Normalizar(p.Name), B = p.Barcode ?? string.Empty })
                .Where(x => x.N.Contains(alvo) || x.B.Contains(termo.Trim()))
                .OrderBy(x => x.N.StartsWith(alvo) ? 0 : 1)
                .ThenBy(x => x.N)
                .Take(limite)
                .Select(x => x.P)
                .ToList();
        }

        private static string DescreverProduto(Product p)
        {
            string estoque = p.UnitMeasure == "KG" ? $"{p.LocalStockQuantity:F3} KG" : $"{p.LocalStockQuantity:F0} UN";
            return $"{p.Name}   —   R$ {p.PriceSale / 100.0:F2}   ·   {estoque}";
        }

        // Preenche e mostra/esconde a lista de sugestões de uma das barras de busca.
        private void AtualizarSugestoes(string termo, ListBox lista, System.Windows.Controls.Primitives.Popup popup)
        {
            // Uma letra só traria quase o catálogo inteiro; a partir de 2 já vale mostrar.
            if (termo.Trim().Length < 2)
            {
                popup.IsOpen = false;
                return;
            }

            var achados = BuscarProdutos(termo);
            lista.Items.Clear();
            foreach (var p in achados) lista.Items.Add(DescreverProduto(p));
            _sugestoesAtuais = achados;

            popup.IsOpen = achados.Count > 0;
            if (achados.Count > 0) lista.SelectedIndex = 0;
        }

        private List<Product> _sugestoesAtuais = new List<Product>();

        // Busca "contém" sem diferenciar maiúscula/minúscula. O .NET Framework 4.8 não tem
        // a sobrecarga string.Contains(string, StringComparison) — só o .NET moderno tem —
        // então usamos IndexOf, que existe nos dois e faz exatamente a mesma coisa.
        private static bool ContemIgnorandoCaixa(string? texto, string? busca)
        {
            if (string.IsNullOrEmpty(busca)) return true;
            if (string.IsNullOrEmpty(texto)) return false;
            return texto!.IndexOf(busca, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryParseDouble(string text, out double value)
        {
            if (string.IsNullOrEmpty(text))
            {
                value = 0;
                return false;
            }
            return double.TryParse(text.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        public class Bracket
        {
            [Newtonsoft.Json.JsonProperty("ate")]
            public int Ate { get; set; }
            [Newtonsoft.Json.JsonProperty("qtd")]
            public int Qtd { get; set; }
        }

        private static int CalcularQuantidadePaes(int valorCentavos, List<Bracket> brackets, int priceUnit)
        {
            if (brackets == null || brackets.Count == 0)
            {
                return priceUnit > 0 ? valorCentavos / priceUnit : 0;
            }

            // Descarta faixas inválidas (Ate <= 0) que causariam divisão por zero ou laço infinito
            var sorted = brackets.Where(b => b.Ate > 0).OrderBy(b => b.Ate).ToList();
            if (sorted.Count == 0)
            {
                return priceUnit > 0 ? valorCentavos / priceUnit : 0;
            }

            int total = 0;
            int valorRestante = valorCentavos;

            while (valorRestante >= sorted[0].Ate)
            {
                Bracket? melhorFaixa = null;
                for (int i = sorted.Count - 1; i >= 0; i--)
                {
                    if (valorRestante >= sorted[i].Ate)
                    {
                        melhorFaixa = sorted[i];
                        break;
                    }
                }

                if (melhorFaixa != null)
                {
                    int mult = valorRestante / melhorFaixa.Ate;
                    total += mult * melhorFaixa.Qtd;
                    valorRestante %= melhorFaixa.Ate;
                }
                else
                {
                    break;
                }
            }

            if (priceUnit > 0 && valorRestante >= priceUnit)
            {
                total += valorRestante / priceUnit;
            }

            return total;
        }

        private async void PromptPaoFrances(Product bread)
        {
            int priceUnit = 50;
            string bracketsJson = string.Empty;
            try
            {
                var connection = App.Database.GetConnection();
                // Filtra pela loja e por ativo: sem isso o FirstOrDefault pegava qualquer
                // linha da tabela — inclusive a semente local antiga — e o pão podia sair
                // com a faixa de preço errada.
                string storeIdPao = StoreIdentityService.Atual(CurrentUser.StoreId);
                var breadConfig = await connection.Table<BreadConfig>()
                    .Where(b => b.StoreId == storeIdPao && b.Active == true)
                    .FirstOrDefaultAsync();
                if (breadConfig != null)
                {
                    priceUnit = breadConfig.PriceUnit;
                    bracketsJson = breadConfig.Brackets;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BreadConfig Read Error]: {ex.Message}");
            }

            var brackets = string.IsNullOrEmpty(bracketsJson) 
                ? new List<Bracket>() 
                : Newtonsoft.Json.JsonConvert.DeserializeObject<List<Bracket>>(bracketsJson) ?? new List<Bracket>();

            var inputWindow = new Window
            {
                Title = "Lançar Pão por Valor",
                Width = 460,
                Height = 390,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.ToolWindow,
                Background = AppColors.Surface,
                Foreground = AppColors.TextPrimary
            };

            var stack = new StackPanel { Margin = new Thickness(25) };

            // Os botões de variação são montados a partir dos pães REALMENTE cadastrados.
            // Antes eram dois botões fixos no código ("Pão Carioca" / "Pão Massa Fina") com
            // ids chumbados, então um terceiro pão cadastrado nunca apareceria aqui.
            List<Product> paes;
            try
            {
                var conn = App.Database.GetConnection();
                var lista = await conn.Table<Product>().Where(p => p.Type == "PAO_FRANCES" && p.Active).ToListAsync();
                paes = lista.OrderBy(p => p.Name).ToList();
            }
            catch
            {
                paes = new List<Product>();
            }
            if (paes.Count == 0) paes = new List<Product> { bread };

            var variationLabel = new TextBlock
            {
                Text = paes.Count > 1 ? "Selecione o pão (ou tecle F1, F2...):" : "Pão:",
                Margin = new Thickness(0, 0, 0, 10),
                Foreground = AppColors.TextMuted,
                FontSize = 15,
                FontWeight = FontWeights.Bold
            };

            var paoSelecionado = paes.FirstOrDefault(p => p.Id == bread.Id) ?? paes[0];

            var buttonsGrid = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            var botoes = new List<Button>();

            void PintarSelecionado()
            {
                for (int i = 0; i < botoes.Count; i++)
                {
                    bool sel = paes[i].Id == paoSelecionado.Id;
                    botoes[i].Background = sel ? AppColors.Accent : AppColors.BorderSoft;
                    botoes[i].Foreground = sel ? AppColors.BgBase : System.Windows.Media.Brushes.White;
                }
            }

            for (int i = 0; i < paes.Count; i++)
            {
                if (i > 0) buttonsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                buttonsGrid.ColumnDefinitions.Add(new ColumnDefinition());

                var pao = paes[i];
                var btn = new Button
                {
                    // O rótulo combina com a tecla de atalho (F1, F2, F3...).
                    Content = paes.Count > 1 ? $"F{i + 1}. {pao.Name}" : pao.Name,
                    Padding = new Thickness(15, 12, 15, 12),
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    Cursor = Cursors.Hand
                };
                btn.Resources.Add(typeof(Border), new Style(typeof(Border)) { Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(6)) } });
                btn.Click += (s, ev) => { paoSelecionado = pao; PintarSelecionado(); };

                Grid.SetColumn(btn, i * 2);
                buttonsGrid.Children.Add(btn);
                botoes.Add(btn);
            }
            PintarSelecionado();

            var label = new TextBlock { Text = "Digite o valor em dinheiro do pão (R$):", Margin = new Thickness(0, 0, 0, 8), Foreground = AppColors.TextMuted, FontSize = 14, FontWeight = FontWeights.SemiBold };
            var textBox = new TextBox { Padding = new Thickness(10), FontSize = 22, FontWeight = FontWeights.Bold, Background = AppColors.BgBase, Foreground = System.Windows.Media.Brushes.White, BorderBrush = AppColors.BorderSoft };
            
            var previewLabel = new TextBlock 
            { 
                Text = "Quantidade: 0 pães", 
                Foreground = AppColors.Accent,
                FontWeight = FontWeights.Bold,
                FontSize = 18,
                Margin = new Thickness(0, 15, 0, 10),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var button = new Button 
            { 
                Content = "Confirmar Lançamento", 
                Margin = new Thickness(0, 15, 0, 0), 
                Padding = new Thickness(12),
                FontSize = 16,
                Background = AppColors.Accent,
                Foreground = AppColors.BgBase,
                FontWeight = FontWeights.Bold
            };

            textBox.TextChanged += (s, ev) => 
            {
                if (TryParseDouble(textBox.Text, out double val) && val > 0)
                {
                    int valueCents = (int)Math.Round(val * 100);
                    int totalDePaes = CalcularQuantidadePaes(valueCents, brackets, priceUnit);
                    previewLabel.Text = $"Quantidade: {totalDePaes} pães";
                }
                else
                {
                    previewLabel.Text = "Quantidade: 0 pães";
                }
            };

            // Sem "async": desde que o lançamento passou a usar direto o pão selecionado,
            // não há mais consulta ao banco aqui.
            button.Click += (s, ev) => {
                if (TryParseDouble(textBox.Text, out double val) && val > 0)
                {
                    int valueCents = (int)Math.Round(val * 100);
                    int totalDePaes = CalcularQuantidadePaes(valueCents, brackets, priceUnit);

                    if (totalDePaes <= 0)
                    {
                        MessageBox.Show("Valor insuficiente.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // Usa direto o pão escolhido nos botões. Antes havia uma busca por id fixo
                    // ("prod-pao-carioca"/"prod-pao-massa-fina"), que ignorava qualquer pão novo.
                    AddBreadProductToCart(paoSelecionado, val, totalDePaes, paoSelecionado.Name);
                    inputWindow.Close();
                }
                else
                {
                    MessageBox.Show("Digite um valor válido.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            textBox.KeyDown += (s, ev) => {
                if (ev.Key == Key.Enter)
                {
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            };

            // F1..F9 escolhem o pão sem tirar a mão do teclado.
            //
            // Antes o atalho eram as teclas 1..9, interceptadas só com o campo de valor vazio.
            // Mas campo vazio é exatamente o momento do PRIMEIRO dígito: quem digitava "1,00"
            // via o "1" ser engolido pela seleção e sobrava ",00". Todo valor começando em 1 ou
            // 2 saía errado. Tecla de função não disputa com número, então o atalho funciona a
            // qualquer momento e o campo recebe o que foi digitado.
            if (paes.Count > 1)
            {
                inputWindow.PreviewKeyDown += (s, ev) =>
                {
                    if (ev.Key < Key.F1 || ev.Key > Key.F9) return;

                    int idx = ev.Key - Key.F1;
                    if (idx >= 0 && idx < paes.Count)
                    {
                        ev.Handled = true;
                        paoSelecionado = paes[idx];
                        PintarSelecionado();
                        textBox.Focus();
                    }
                };
            }

            stack.Children.Add(variationLabel);
            stack.Children.Add(buttonsGrid);
            stack.Children.Add(label);
            stack.Children.Add(textBox);
            stack.Children.Add(previewLabel);
            stack.Children.Add(button);
            inputWindow.Content = stack;

            textBox.Focus();
            inputWindow.ShowDialog();
        }

        private void AddBreadProductToCart(Product product, double totalValorDigitado, int quantidadePaes, string variationCode)
        {
            int valueCents = (int)Math.Round(totalValorDigitado * 100);
            int priceUnit = quantidadePaes > 0 ? (int)Math.Round((double)valueCents / quantidadePaes) : product.PriceSale;

            string cartItemId = $"{product.Id}-{valueCents}";
            string textDetail = $"R$ {totalValorDigitado:F2}";

            var existing = _cartItems.FirstOrDefault(i => i.CartItemId == cartItemId);
            if (existing != null)
            {
                existing.Quantity += quantidadePaes;
                existing.Subtotal += valueCents;
                existing.Details = $"{existing.Quantity} pães ({textDetail})";
                
                int index = _cartItems.IndexOf(existing);
                _cartItems[index] = existing;
            }
            else
            {
                _cartItems.Add(new CartItemView
                {
                    CartItemId = cartItemId,
                    ProductId = product.Id,
                    ProductName = $"{product.Name} - R$ {totalValorDigitado:F2}",
                    ProductType = product.Type,
                    Quantity = quantidadePaes,
                    PriceUnit = priceUnit,
                    Subtotal = valueCents,
                    Variation = variationCode,
                    Details = $"{quantidadePaes} pães calculados ({textDetail})"
                });
            }

            UpdateTotals();
        }

        private async void ChangeVariation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string cartItemId)
            {
                var item = _cartItems.FirstOrDefault(i => i.CartItemId == cartItemId);
                if (item == null) return;

                string targetVar = btn.CommandParameter?.ToString() ?? "NORMAL";
                if (item.Variation == targetVar) return;

                try
                {
                    var connection = App.Database.GetConnection();
                    var originalProduct = await connection.Table<Product>().Where(p => p.Id == item.ProductId).FirstOrDefaultAsync();
                    if (originalProduct == null) return;

                    if (targetVar == "INTEIRO")
                    {
                        item.Variation = "INTEIRO";
                        item.PriceUnit = 5500; // R$ 55,00 inteiro
                        item.Quantity = 1;
                        item.Subtotal = 5500;
                        item.ProductName = $"{originalProduct.Name} (Inteiro)";
                        item.Details = "Bolo Inteiro (R$ 55,00)";
                    }
                    else
                    {
                        item.Variation = "NORMAL";
                        item.PriceUnit = originalProduct.PriceSale;
                        item.Quantity = 1;
                        item.Subtotal = originalProduct.PriceSale;
                        item.ProductName = originalProduct.Name;
                        item.Details = "Fatia de Bolo";
                    }

                    int index = _cartItems.IndexOf(item);
                    _cartItems[index] = item;
                    
                    UpdateTotals();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao alterar variação: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void DecreaseQuantity_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string cartItemId)
            {
                var item = _cartItems.FirstOrDefault(i => i.CartItemId == cartItemId);
                if (item == null) return;

                if (item.Quantity > 1)
                {
                    item.Quantity -= 1;
                    item.Subtotal = (int)Math.Round(item.Quantity * item.PriceUnit);
                    
                    int index = _cartItems.IndexOf(item);
                    _cartItems[index] = item;
                    UpdateTotals();
                }
                else
                {
                    _cartItems.Remove(item);
                    UpdateTotals();
                }
            }
        }

        private async void IncreaseQuantity_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string cartItemId)
            {
                var item = _cartItems.FirstOrDefault(i => i.CartItemId == cartItemId);
                if (item == null) return;

                try
                {
                    var connection = App.Database.GetConnection();
                    var product = await connection.Table<Product>().Where(p => p.Id == item.ProductId).FirstOrDefaultAsync();
                    
                    if (product != null)
                    {
                        if (item.Variation == "INTEIRO")
                        {
                            MessageBox.Show("Para bolo inteiro a quantidade é travada em 1.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        // Soma TODAS as linhas do mesmo produto no carrinho (não só a clicada),
                        // consistente com a validação do AddProductToCart — evita passar do
                        // estoque real quando o produto aparece em mais de uma linha.
                        double totalNoCarrinho = _cartItems.Where(i => i.ProductId == item.ProductId).Sum(i => i.Quantity);
                        if (totalNoCarrinho + 1 > product.LocalStockQuantity)
                        {
                            MessageBox.Show($"Estoque insuficiente! Atual: {product.LocalStockQuantity:F0}", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        item.Quantity += 1;
                        item.Subtotal = (int)Math.Round(item.Quantity * item.PriceUnit);

                        int index = _cartItems.IndexOf(item);
                        _cartItems[index] = item;
                        UpdateTotals();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao verificar estoque: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void UpdateTotals()
        {
            _subtotalCentavos = _cartItems.Sum(i => i.Subtotal);
            // Total = subtotal menos desconto acumulado, nunca abaixo de zero
            _totalCentavos = Math.Max(0, _subtotalCentavos - _discountCentavos);

            TotalText.Text = $"R$ {_totalCentavos / 100.0:F2}";
        }

        private async void FinishSaleFlow()
        {
            if (!_cartItems.Any())
            {
                MessageBox.Show("Adicione pelo menos um item ao carrinho.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_encerrando || !await _saleGate.WaitAsync(0)) return;

            try
            {
                var connection = App.Database.GetConnection();

                // Valida estoque antes de gravar — protege contra race condition
                foreach (var cartItem in _cartItems)
                {
                    var prod = await connection.FindAsync<Product>(cartItem.ProductId);
                    if (prod == null) continue;
                    if (prod.LocalStockQuantity < cartItem.Quantity)
                    {
                        string estoqueStr = prod.UnitMeasure == "KG"
                            ? $"{prod.LocalStockQuantity:F3} KG"
                            : $"{(int)prod.LocalStockQuantity} UN";
                        MessageBox.Show(
                            $"Estoque insuficiente para '{prod.Name}'.\nDisponível: {estoqueStr}.\nRemova ou reduza a quantidade no carrinho.",
                            "Sem Estoque", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                var saleId = Guid.NewGuid().ToString();

                string tenantId = CurrentUser.TenantId;
                string storeId = StoreIdentityService.Atual(CurrentUser.StoreId);

                var terminalName = EnvService.Get("TERMINAL_NAME");
                var sale = new Sale
                {
                    Id = saleId,
                    StoreId = storeId,
                    UserId = CurrentUser.Id,
                    TenantId = tenantId,
                    SaleDate = DateTime.Now,
                    Subtotal = _subtotalCentavos,
                    Discount = _discountCentavos,
                    Total = _totalCentavos,
                    PaymentMethod = _selectedPaymentMethod,
                    PaymentStatus = "APROVADO",
                    IsSynced = false,
                    Notes = string.IsNullOrEmpty(terminalName) ? null : $"[{terminalName}]"
                };

                if (_selectedPaymentMethod == "DINHEIRO")
                {
                    sale.ReceivedAmount = _valorRecebidoCentavos;
                    sale.ChangeAmount = _valorRecebidoCentavos - _totalCentavos;
                }

                await App.Database.RunInTransactionAsync((tx) =>
                {
                    tx.Insert(sale);

                    foreach (var item in _cartItems)
                    {
                        var saleItem = new SaleItem
                        {
                            Id = Guid.NewGuid().ToString(),
                            SaleId = saleId,
                            ProductId = item.ProductId,
                            Quantity = item.Quantity,
                            PriceUnit = item.PriceUnit,
                            Subtotal = item.Subtotal,
                            Type = item.ProductType
                        };

                        // Lê o produto ANTES de baixar, para gravar o saldo anterior e o final
                        // no movimento (histórico auditável sem precisar recalcular depois).
                        var product = tx.Find<Product>(item.ProductId);
                        double? saldoAntes = product?.LocalStockQuantity;
                        double? saldoDepois = product != null
                            ? Math.Max(0, product.LocalStockQuantity - item.Quantity)
                            : (double?)null;

                        var stockMovement = new StockMovement
                        {
                            Id = Guid.NewGuid().ToString(),
                            ProductId = item.ProductId,
                            StoreId = storeId,
                            UserId = CurrentUser.Id,
                            TenantId = tenantId,
                            Type = "SAIDA",
                            Quantity = item.Quantity,
                            Reason = "VENDA",
                            SaleId = saleId,
                            CreatedAt = DateTime.Now,
                            IsSynced = false,
                            BalanceBefore = saldoAntes,
                            BalanceAfter = saldoDepois
                        };

                        tx.Insert(saleItem);
                        tx.Insert(stockMovement);

                        if (product != null)
                        {
                            product.LocalStockQuantity = saldoDepois!.Value;
                            tx.Update(product);
                        }
                    }
                });

                // Prossiga com a impressão do cupom fiscal do PDV
                await ImprimirCupomFiscalAsync(saleId);

                MessageBox.Show("Venda realizada com sucesso!", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);

                _cartItems.Clear();
                _discountCentavos = 0;
                _selectedPaymentMethod = string.Empty;
                _valorRecebidoCentavos = 0;

                UpdateTotals();
                SetPdvState(PdvState.Consultation);

                _ = Task.Run(async () => await RunSincronizacaoSilenciosa());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Falha ao salvar venda localmente: {ex.Message}", "Erro Fatal", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _saleGate.Release();
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (MainTabControl.SelectedIndex != 0) return;

            if (Keyboard.Modifiers != ModifierKeys.None && Keyboard.Modifiers != ModifierKeys.Shift) return;

            var elapsed = DateTime.Now - _lastBarcodeEvent;
            _lastBarcodeEvent = DateTime.Now;

            if (elapsed.TotalMilliseconds > 80)
            {
                _barcodeAccumulator.Clear();
            }

            if ((e.Key >= Key.D0 && e.Key <= Key.D9) ||
                (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9) ||
                (e.Key >= Key.A && e.Key <= Key.Z))
            {
                string charTyped = "";
                if (e.Key >= Key.D0 && e.Key <= Key.D9)
                    charTyped = (e.Key - Key.D0).ToString();
                else if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
                    charTyped = (e.Key - Key.NumPad0).ToString();
                else
                    charTyped = e.Key.ToString();

                _barcodeAccumulator.Append(charTyped);
            }
            else if (e.Key == Key.Enter)
            {
                if (_barcodeAccumulator.Length >= 3 && elapsed.TotalMilliseconds <= 80)
                {
                    string barcode = _barcodeAccumulator.ToString();
                    _barcodeAccumulator.Clear();
                    e.Handled = true;
                    ProcessBarcodeScan(barcode);
                    return;
                }
                else
                {
                    _barcodeAccumulator.Clear();
                }
            }

            if (_currentState == PdvState.Consultation)
            {
                if (e.Key == Key.F1)
                {
                    e.Handled = true;
                    SetPdvState(PdvState.ActiveSale);
                }
            }
            else if (_currentState == PdvState.ActiveSale)
            {
                if (e.Key == Key.F12)
                {
                    e.Handled = true;
                    ConfirmSaleBtn_Click(ConfirmSaleBtn, new RoutedEventArgs());
                }
                else if (e.Key == Key.F4)
                {
                    e.Handled = true;
                    LaunchBreadFlow();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    if (MessageBox.Show("Deseja realmente cancelar esta venda?", "Aviso", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        SetPdvState(PdvState.Consultation);
                    }
                }
            }
        }

        private async void ProcessBarcodeScan(string barcode)
        {
            try
            {
                // Etiqueta de PESO (preço embutido) é resolvida antes da busca normal:
                // esse código não existe como produto no banco, ele carrega o preço dentro.
                // Só entra aqui se casar prefixo "21" + dígito verificador, então código de
                // fábrica nunca é confundido.
                if (Services.WeightBarcodeService.TentarLer(barcode, out string refPeso, out int precoPeso))
                {
                    await ProcessarEtiquetaPesoAsync(barcode, refPeso, precoPeso);
                    return;
                }

                var connection = App.Database.GetConnection();
                var product = await connection.Table<Product>()
                    .Where(p => p.Barcode == barcode && p.Active)
                    .FirstOrDefaultAsync();

                if (product == null)
                {
                    MessageBox.Show($"Produto com código '{barcode}' não encontrado.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (_currentState == PdvState.Consultation)
                {
                    ConsultationProductName.Text = product.Name;
                    ConsultationProductPrice.Text = $"R$ {product.PriceSale / 100.0:F2}";
                    ConsultationProductStock.Text = $"Estoque: {(product.UnitMeasure == "KG" ? $"{product.LocalStockQuantity:F3} KG" : $"{product.LocalStockQuantity:F0} UN")}";
                    
                    var category = await connection.FindAsync<Category>(product.CategoryId);
                    ConsultationProductCategory.Text = $"Categoria: {(category != null ? category.Name : "Geral")}";
                    ConsultationBarcode.Text = product.Barcode ?? "-";
                }
                else if (_currentState == PdvState.ActiveSale)
                {
                    if (product.Type == "PAO_FRANCES")
                    {
                        PromptPaoFrances(product);
                    }
                    else
                    {
                        AddProductToCart(product, 1.0);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao processar código de barras: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Trata a leitura de uma etiqueta de peso: descobre o produto pelo trecho de
        // identificação do código e usa o preço que veio embutido na própria etiqueta.
        private async Task ProcessarEtiquetaPesoAsync(string codigoLido, string refProduto, int precoCentavos)
        {
            var connection = App.Database.GetConnection();

            // O identificador do produto vem dos últimos dígitos da sequência do código
            // interno, então a comparação é feita em memória sobre os produtos ativos.
            var ativos = await connection.Table<Product>().Where(p => p.Active).ToListAsync();
            var product = ativos.FirstOrDefault(p =>
                Services.WeightBarcodeService.ExtrairRefProduto(p.Barcode) == refProduto);

            if (product == null)
            {
                MessageBox.Show($"Etiqueta de peso '{codigoLido}' não corresponde a nenhum produto cadastrado nesta loja.",
                    "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Recupera o peso a partir do preço impresso e do preço do quilo cadastrado.
            double pesoKg = product.PriceSale > 0 ? (double)precoCentavos / product.PriceSale : 0;

            if (_currentState == PdvState.Consultation)
            {
                ConsultationProductName.Text = product.Name;
                ConsultationProductPrice.Text = $"R$ {precoCentavos / 100.0:F2}  ({pesoKg:F3} kg)";
                ConsultationProductStock.Text = $"Estoque: {product.LocalStockQuantity:F3} KG";
                var categoria = await connection.FindAsync<Category>(product.CategoryId);
                ConsultationProductCategory.Text = $"Categoria: {(categoria != null ? categoria.Name : "Geral")}";
                ConsultationBarcode.Text = codigoLido;
                return;
            }

            if (_currentState == PdvState.ActiveSale)
            {
                AddWeightItemToCart(product, pesoKg, precoCentavos);
            }
        }

        // Adiciona ao carrinho um item vindo de etiqueta de peso. Cada etiqueta é um pacote
        // físico distinto, então vira SEMPRE uma linha nova (não funde com outra igual) e o
        // subtotal é exatamente o valor impresso na etiqueta.
        private void AddWeightItemToCart(Product product, double pesoKg, int precoCentavos)
        {
            double jaNoCarrinho = _cartItems.Where(i => i.ProductId == product.Id).Sum(i => i.Quantity);
            if (product.LocalStockQuantity - jaNoCarrinho < pesoKg)
            {
                MessageBox.Show(
                    $"Estoque insuficiente para '{product.Name}'.\nDisponível: {product.LocalStockQuantity:F3} KG.",
                    "Sem Estoque", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _cartItems.Add(new CartItemView
            {
                CartItemId = Guid.NewGuid().ToString(),
                ProductId = product.Id,
                ProductName = product.Name,
                ProductType = product.Type,
                Quantity = pesoKg,
                PriceUnit = product.PriceSale,
                Subtotal = precoCentavos,
                IsPesado = true,
                Details = $"{pesoKg:F3} kg x R$ {product.PriceSale / 100.0:F2}/kg"
            });

            UpdateTotals();
        }

        private void SetPdvState(PdvState state)
        {
            _currentState = state;
            
            if (GridConsulta == null || GridVenda == null || PanelPrecoVenda == null)
                return;

            switch (state)
            {
                case PdvState.Consultation:
                    GridConsulta.Visibility = Visibility.Visible;
                    GridVenda.Visibility = Visibility.Collapsed;
                    
                    ConsultationProductName.Text = "Passe um produto no leitor";
                    ConsultationProductPrice.Text = "R$ 0,00";
                    ConsultationProductStock.Text = "Estoque: --";
                    ConsultationProductCategory.Text = "Categoria: --";
                    ConsultationBarcode.Text = "Aguardando leitura...";
                    
                    _cartItems.Clear();
                    _discountCentavos = 0;
                    UpdateTotals();
                    ConsultationSearchBox.Text = string.Empty;
                    ConsultationSearchBox.Focus();
                    break;

                case PdvState.ActiveSale:
                    GridConsulta.Visibility = Visibility.Collapsed;
                    GridVenda.Visibility = Visibility.Visible;
                    PanelPrecoVenda.Visibility = Visibility.Visible;
                    
                    SearchBox.Text = string.Empty;
                    SearchBox.Focus();
                    break;
            }
        }

        private void StartSaleButton_Click(object sender, RoutedEventArgs e)
        {
            SetPdvState(PdvState.ActiveSale);
        }

        private void ConfirmSaleBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_cartItems.Any())
            {
                MessageBox.Show("Adicione pelo menos um item ao carrinho.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AbrirModalPagamento();
        }

        private void AbrirModalPagamento()
        {
            var paymentWindow = new Views.PaymentWindow(_totalCentavos);
            paymentWindow.Owner = this;
            if (paymentWindow.ShowDialog() == true)
            {
                _selectedPaymentMethod = paymentWindow.SelectedPaymentMethod;
                if (_selectedPaymentMethod == "DINHEIRO")
                {
                    _valorRecebidoCentavos = paymentWindow.ReceivedAmountCentavos;
                }
                else
                {
                    _valorRecebidoCentavos = 0;
                }

                FinishSaleFlow();
            }
        }

        #region Métodos de Impressão de Cupom

        private async Task ImprimirCupomFiscalAsync(string saleId)
        {
            try
            {
                var connection = App.Database.GetConnection();
                var sale = await connection.FindAsync<Sale>(saleId);
                if (sale == null) return;

                var items = await connection.Table<SaleItem>().Where(i => i.SaleId == saleId).ToListAsync();

                // Montar o cupom fiscal no estilo impressora térmica (40 colunas)
                var cupom = new System.Text.StringBuilder();
                cupom.AppendLine("========================================");
                cupom.AppendLine("            PADARIA VENÂNCIO            ");
                cupom.AppendLine("     CNPJ: 00.000.000/0001-00           ");
                cupom.AppendLine("       Rua da Padaria, 123 - Centro     ");
                cupom.AppendLine("========================================");
                cupom.AppendLine($"CUPOM FISCAL SIMULADO: {sale.Id.Substring(0, 8)}");
                cupom.AppendLine($"Data: {sale.SaleDate:dd/MM/yyyy HH:mm:ss}");
                cupom.AppendLine($"Método Pagto: {sale.PaymentMethod}");
                cupom.AppendLine("----------------------------------------");
                cupom.AppendLine("ITENS:");
                
                foreach (var item in items)
                {
                    var product = await connection.FindAsync<Product>(item.ProductId);
                    string name = product?.Name ?? "Produto Desconhecido";
                    if (name.Length > 20) name = name.Substring(0, 20);

                    string qtyStr = item.Type == "PAO_FRANCES" || item.Quantity % 1 == 0
                        ? $"{item.Quantity:F0} UN"
                        : $"{item.Quantity:F3} KG";

                    string priceStr = $"R$ {item.PriceUnit / 100.0:F2}";
                    string subtotalStr = $"R$ {item.Subtotal / 100.0:F2}";

                    cupom.AppendLine($"{name,-20}");
                    cupom.AppendLine($"  {qtyStr,-10} x {priceStr,-10} {subtotalStr,14}");
                }

                cupom.AppendLine("----------------------------------------");
                if (sale.Discount > 0)
                {
                    cupom.AppendLine($"Subtotal:                R$ {sale.Subtotal / 100.0:F2}");
                    cupom.AppendLine($"Desconto:               -R$ {sale.Discount / 100.0:F2}");
                }
                cupom.AppendLine($"TOTAL:                   R$ {sale.Total / 100.0:F2}");
                cupom.AppendLine("========================================");
                cupom.AppendLine("      Obrigado pela preferência!       ");
                cupom.AppendLine("========================================");

                // Gravar o cupom em arquivo de texto no AppData
                string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pdv-padaria", "comprovantes");
                if (!Directory.Exists(appDataPath))
                {
                    Directory.CreateDirectory(appDataPath);
                }

                string filePath = Path.Combine(appDataPath, $"cupom_{saleId}.txt");
                // File.WriteAllTextAsync não existe no .NET Framework 4.8: grava fora da
                // thread de UI com Task.Run para não travar o caixa.
                string conteudoCupom = cupom.ToString();
                await Task.Run(() => File.WriteAllText(filePath, conteudoCupom, System.Text.Encoding.UTF8));

                System.Diagnostics.Debug.WriteLine($"[Cupom Impresso]: {filePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Erro ao imprimir cupom]: {ex.Message}");
            }
        }

        #endregion

        private void ConsultationSearchButton_Click(object sender, RoutedEventArgs e)
        {
            ExecuteConsultationSearch();
        }

        private void ConsultationSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (ConsultationSugestoesPopup.IsOpen && NavegarSugestoes(e, ConsultationSugestoesList))
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter)
            {
                if (ConsultationSugestoesPopup.IsOpen && ConsultationSugestoesList.SelectedIndex >= 0)
                {
                    e.Handled = true;
                    UsarSugestaoConsulta();
                    return;
                }
                ExecuteConsultationSearch();
            }
            else if (e.Key == Key.Escape && ConsultationSugestoesPopup.IsOpen)
            {
                e.Handled = true;
                ConsultationSugestoesPopup.IsOpen = false;
            }
        }

        private async void ExecuteConsultationSearch()
        {
            var query = ConsultationSearchBox.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;

            ConsultationSearchBox.Text = string.Empty;

            try
            {
                var connection = App.Database.GetConnection();

                // Código de barras exato primeiro (caminho do leitor).
                var product = await connection.Table<Product>()
                    .Where(p => p.Barcode == query && p.Active)
                    .FirstOrDefaultAsync();

                if (product == null)
                {
                    // Nome ignorando acento; com mais de um resultado, mostra as opções em vez
                    // de escolher o primeiro por conta própria.
                    await ObterCatalogoAsync();
                    var achados = BuscarProdutos(query, 30);

                    if (achados.Count == 0)
                    {
                        MessageBox.Show($"Nenhum produto encontrado para '{query}'.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (achados.Count > 1)
                    {
                        ConsultationSearchBox.Text = query;
                        AtualizarSugestoes(query, ConsultationSugestoesList, ConsultationSugestoesPopup);
                        ConsultationSearchBox.Focus();
                        return;
                    }
                    product = achados[0];
                }

                await MostrarConsulta(product);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro na consulta: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void KioskToggle_Checked(object sender, RoutedEventArgs e)
        {
            // Salvar estado e tamanho da janela antes de entrar em modo Kiosk
            _prevLeft = this.Left;
            _prevTop = this.Top;
            _prevWidth = this.Width;
            _prevHeight = this.Height;
            _prevWindowState = this.WindowState;

            // 1. Primeiro, oculte a barra de tarefas via API (ShowWindow)
            SafeShowHideTaskbar(false);

            // 2. Mude o WindowState do Form para 'Normal'
            this.WindowState = WindowState.Normal;

            // 3. Defina o estilo de borda para None (no WPF: WindowStyle = WindowStyle.None e ResizeMode = ResizeMode.NoResize)
            this.WindowStyle = WindowStyle.None;
            this.ResizeMode = ResizeMode.NoResize;

            // 4. Force as dimensões manuais ignorando a WorkingArea usando SystemParameters
            this.Top = 0;
            this.Left = 0;
            this.Width = SystemParameters.PrimaryScreenWidth;
            this.Height = SystemParameters.PrimaryScreenHeight;

            // 5. Forçar o WPF a atualizar o layout imediatamente e trazer para a frente
            this.UpdateLayout();
            this.Topmost = true;
            this.Activate();
            this.Focus();
        }

        private void KioskToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            var passwordWindow = new Window
            {
                Title = "Senha Administrativa",
                Width = 300,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.ToolWindow,
                Background = AppColors.Surface,
                Foreground = AppColors.TextPrimary
            };

            var stack = new StackPanel { Margin = new Thickness(15) };
            var label = new TextBlock { Text = "Digite a senha administrativa:", Margin = new Thickness(0,0,0,5) };
            var passwordBox = new PasswordBox { Padding = new Thickness(5), Background = AppColors.BgBase, Foreground = System.Windows.Media.Brushes.White, BorderBrush = AppColors.BorderSoft };
            var button = new Button { Content = "Confirmar", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(5), Background = System.Windows.Media.Brushes.DarkOrange };

            bool authenticated = false;

            button.Click += (s, ev) => {
                // Verifica contra a senha real (hash BCrypt) do usuario logado —
                // sem senha hardcoded no binario.
                if (PasswordHasher.Verify(passwordBox.Password, CurrentUser.Password))
                {
                    authenticated = true;
                    passwordWindow.Close();
                }
                else
                {
                    MessageBox.Show("Senha incorreta.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            passwordBox.KeyDown += (s, ev) => {
                if (ev.Key == Key.Enter)
                {
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            };

            stack.Children.Add(label);
            stack.Children.Add(passwordBox);
            stack.Children.Add(button);
            passwordWindow.Content = stack;
            passwordBox.Focus();
            passwordWindow.ShowDialog();

            if (authenticated)
            {
                this.WindowStyle = WindowStyle.SingleBorderWindow;
                this.ResizeMode = ResizeMode.CanResize;
                
                // Mostrar a barra de tarefas de volta
                SafeShowHideTaskbar(true);

                // Restaurar dimensões e estado originais
                this.Left = _prevLeft;
                this.Top = _prevTop;
                this.Width = _prevWidth;
                this.Height = _prevHeight;
                this.WindowState = _prevWindowState;
                this.Topmost = false;
            }
            else
            {
                KioskToggle.IsChecked = true;
            }
        }

        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (KioskToggle.IsChecked == true)
            {
                e.Cancel = true;
                MessageBox.Show("Desative o Modo Tela Cheia com a senha administrativa para poder fechar o aplicativo.", "Segurança", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_sessaoEncerrada) return;   // segunda passada: já encerrou, pode fechar

            // Segura o fechamento para subir o que ainda está na fila. O WPF não espera por
            // método assíncrono aqui, então cancela-se esta passada e fecha-se de novo no fim.
            e.Cancel = true;
            if (_encerrandoEmCurso) return; // logout já está encerrando; ele fecha a janela
            await EncerrarSessaoAsync();
            Close();
        }

        /// <summary>
        /// Fim da sessão, pelos dois caminhos: logout e fechar a janela.
        ///
        /// A ordem importa. O envio final acontece ANTES de marcar _encerrando, porque essa
        /// marca desliga a sincronização — era por isso que o logout nunca empurrava nada,
        /// só esperava o que já estava em curso.
        /// </summary>
        private bool _encerrandoEmCurso;

        private async Task EncerrarSessaoAsync()
        {
            // O encerramento leva segundos (envio final). Sem esta trava, um segundo clique
            // no Sair — ou o X durante o logout — rodaria tudo de novo e abriria duas telas
            // de login.
            if (_sessaoEncerrada || _encerrandoEmCurso) return;
            _encerrandoEmCurso = true;

            _syncTimer?.Stop();

            // Espera venda e sync em curso antes de mexer na identidade desta base SQLite.
            await _saleGate.WaitAsync();
            _saleGate.Release();
            await _syncGate.WaitAsync();
            _syncGate.Release();

            SyncStatusText.Text = "Enviando o que falta...";
            var resultado = await _escopo.EncerrarAsync(
                async () => (await SincronizarDadosNuvem()).Item1,
                TimeSpan.FromSeconds(10));

            _encerrando = true;
            _sessaoEncerrada = true;

            if (resultado == ResultadoDoEncerramento.FicouPendente)
            {
                int pendentes = 0;
                try
                {
                    pendentes = await App.Database.GetConnection()
                        .ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Sale WHERE IsSynced = 0");
                }
                catch { }

                MessageBox.Show(
                    (pendentes > 0
                        ? $"{pendentes} venda(s) ainda não subiram para a nuvem.\n\n"
                        : "O envio final não completou.\n\n") +
                    "Nada foi perdido: fica guardado neste computador e sobe sozinho na " +
                    "próxima vez que o caixa abrir com internet.\n\n" +
                    "Se este computador for trocado ou formatado antes disso, avise o " +
                    "responsável antes.",
                    "Ficou coisa para enviar", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (KioskToggle.IsChecked == true)
            {
                MessageBox.Show("Desative o Modo Tela Cheia antes de fazer logout.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await EncerrarSessaoAsync();

            var login = new LoginWindow();
            login.Show();
            Close();
        }

        // ================= ABA 1: ESTOQUE LOCAL LOGIC =================

        private async void LoadStock(string filter = "")
        {
            try
            {
                var connection = App.Database.GetConnection();
                var products = await connection.Table<Product>().Where(p => p.Active).ToListAsync();
                var categories = await connection.Table<Category>().ToListAsync();
                var categoryMap = categories.ToDictionary(c => c.Id, c => c.Name);

                var views = products
                    .Where(p => string.IsNullOrEmpty(filter) || ContemIgnorandoCaixa(p.Name, filter))
                    .Select(p => new StockProductView
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Barcode = p.Barcode ?? "",
                        Type = p.Type,
                        UnitMeasure = p.UnitMeasure,
                        PriceSale = p.PriceSale,
                        PriceCost = p.PriceCost,
                        LocalStockQuantity = p.LocalStockQuantity,
                        MinStock = p.MinStock,
                        CategoryName = categoryMap.TryGetValue(p.CategoryId, out var name) ? name : "Geral"
                    })
                    .Where(v => {
                        if (_stockCategoryFilter == "COMERCIAIS") return v.Type.ToUpper() == "NORMAL";
                        if (_stockCategoryFilter == "PRODUCAO") return v.Type.ToUpper() != "NORMAL";
                        return true;
                    })
                    .OrderBy(p => p.Name)
                    .ToList();

                StockProductsList.ItemsSource = views;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao carregar estoque: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateStockFilterButtons()
        {
            if (BtnFilterStockAll == null || BtnFilterStockCommercial == null || BtnFilterStockProduction == null) return;

            BtnFilterStockAll.Background = _stockCategoryFilter == "TODOS" ? AppColors.Accent : AppColors.BorderSoft;
            BtnFilterStockAll.Foreground = _stockCategoryFilter == "TODOS" ? AppColors.BgBase : System.Windows.Media.Brushes.White;

            BtnFilterStockCommercial.Background = _stockCategoryFilter == "COMERCIAIS" ? AppColors.Accent : AppColors.BorderSoft;
            BtnFilterStockCommercial.Foreground = _stockCategoryFilter == "COMERCIAIS" ? AppColors.BgBase : System.Windows.Media.Brushes.White;

            BtnFilterStockProduction.Background = _stockCategoryFilter == "PRODUCAO" ? AppColors.Accent : AppColors.BorderSoft;
            BtnFilterStockProduction.Foreground = _stockCategoryFilter == "PRODUCAO" ? AppColors.BgBase : System.Windows.Media.Brushes.White;
        }

        private void BtnFilterStockAll_Click(object sender, RoutedEventArgs e)
        {
            _stockCategoryFilter = "TODOS";
            UpdateStockFilterButtons();
            if (string.IsNullOrEmpty(_stockRemoteStoreId))
                LoadStock(SearchStockBox.Text.Trim());
            else
                ApplyRemoteStockFilter();
        }

        private void BtnFilterStockCommercial_Click(object sender, RoutedEventArgs e)
        {
            _stockCategoryFilter = "COMERCIAIS";
            UpdateStockFilterButtons();
            if (string.IsNullOrEmpty(_stockRemoteStoreId))
                LoadStock(SearchStockBox.Text.Trim());
            else
                ApplyRemoteStockFilter();
        }

        private void BtnFilterStockProduction_Click(object sender, RoutedEventArgs e)
        {
            _stockCategoryFilter = "PRODUCAO";
            UpdateStockFilterButtons();
            if (string.IsNullOrEmpty(_stockRemoteStoreId))
                LoadStock(SearchStockBox.Text.Trim());
            else
                ApplyRemoteStockFilter();
        }

        private void ApplyRemoteStockFilter()
        {
            if (_remoteStockCache == null) return;
            var views = _remoteStockCache.Where(v => {
                if (_stockCategoryFilter == "COMERCIAIS") return v.Type.ToUpper() == "NORMAL";
                if (_stockCategoryFilter == "PRODUCAO") return v.Type.ToUpper() != "NORMAL";
                return true;
            }).ToList();
            StockProductsList.ItemsSource = views;
        }

        private async void LoadLowStockAlerts()
        {
            try
            {
                var connection = App.Database.GetConnection();

                // Antes o filtro era só "LocalStockQuantity <= MinStock". Como nenhum produto
                // tem mínimo definido (MinStock = 0), qualquer produto zerado satisfazia 0 <= 0
                // e entrava: 111 dos 115 apareciam em alerta. Um alerta que nunca fica limpo
                // deixa de ser lido.
                //
                // A maioria não tinha acabado: nunca teve estoque lançado, porque a loja ainda
                // não fez a contagem inicial. São situações diferentes:
                //
                //   ACABOU          zerado e JÁ lançado aqui  -> repor, é o alerta de verdade
                //   NO MÍNIMO       0 < qtd <= mínimo (> 0)   -> repor antes de faltar
                //   SEM CONTAGEM    zerado e nunca lançado    -> cadastro pendente, fora da lista
                //
                // "Já lançado" sai do histórico de movimentos desta máquina, que agora inclui
                // as entradas do dono (ver ApplyOwnerAdjustmentsAsync).
                var lancadosRows = await connection.QueryAsync<ProdutoIdRow>(
                    "SELECT DISTINCT ProductId FROM StockMovement WHERE StoreId = ?",
                    StoreIdentityService.StoreId);
                var lancados = new HashSet<string>(lancadosRows.Select(r => r.ProductId));

                var ativos = await connection.Table<Product>().Where(p => p.Active).ToListAsync();

                bool Acabou(Product p) => p.LocalStockQuantity <= 0 && lancados.Contains(p.Id);
                bool NoMinimo(Product p) => p.LocalStockQuantity > 0 && p.MinStock > 0
                                            && p.LocalStockQuantity <= p.MinStock;
                bool SemContagem(Product p) => p.LocalStockQuantity <= 0 && !lancados.Contains(p.Id);

                var products = ativos.Where(p => Acabou(p) || NoMinimo(p)).ToList();

                var categories = await connection.Table<Category>().ToListAsync();
                var categoryMap = categories.ToDictionary(c => c.Id, c => c.Name);

                var alerts = products
                    .Select(p => new StockAlertView
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Barcode = p.Barcode ?? "",
                        Type = p.Type,
                        UnitMeasure = p.UnitMeasure,
                        LocalStockQuantity = p.LocalStockQuantity,
                        MinStock = p.MinStock,
                        CategoryName = categoryMap.TryGetValue(p.CategoryId, out var name) ? name : "Geral"
                    })
                    .OrderBy(p => p.Name)
                    .ToList();

                // Contadores. "Sem contagem" não é alerta: mostra o tamanho da contagem
                // inicial que ainda falta, sem competir com o que pede reposição.
                int acabouCount      = ativos.Count(Acabou);
                int noMinimoCount    = ativos.Count(NoMinimo);
                int semContagemCount = ativos.Count(SemContagem);

                string Itens(int n) => n == 1 ? "1 item" : $"{n} itens";

                AlertZeroStockCountText.Text     = Itens(acabouCount);
                AlertLowStockCountText.Text      = Itens(noMinimoCount);
                AlertTotalCriticalCountText.Text = Itens(semContagemCount);

                StockAlertsList.ItemsSource = alerts;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao carregar alertas de estoque: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SearchStockBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchStockPlaceholder.Visibility = string.IsNullOrEmpty(SearchStockBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Filtro só vale para a loja local; no modo remoto a lista vem da nuvem.
            if (!string.IsNullOrEmpty(_stockRemoteStoreId)) return;
            LoadStock(SearchStockBox.Text.Trim());
        }

        private async void AdjustStockButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string productId)
            {
                // Modo DONO editando loja remota: grava na nuvem, não no banco local.
                if (!string.IsNullOrEmpty(_stockRemoteStoreId))
                {
                    await AdjustStockRemote(productId);
                    return;
                }
                try
                {
                    var connection = App.Database.GetConnection();
                    var product = await connection.FindAsync<Product>(productId);
                    if (product == null) return;

                    var adjustWindow = new Window
                    {
                        Title = $"Ajustar Estoque - {product.Name}",
                        Width = 350,
                        Height = 220,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Owner = this,
                        WindowStyle = WindowStyle.ToolWindow,
                        Background = AppColors.Surface,
                        Foreground = AppColors.TextPrimary
                    };

                    var stack = new StackPanel { Margin = new Thickness(20) };
                    var infoLabel = new TextBlock { Text = $"Estoque Atual: {(product.UnitMeasure == "KG" ? $"{product.LocalStockQuantity:F3} KG" : $"{product.LocalStockQuantity:F0} UN")}", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 15) };
                    
                    var grid = new Grid { Margin = new Thickness(0, 0, 0, 15) };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

                    var textBox = new TextBox { Padding = new Thickness(8), FontSize = 14, Background = AppColors.BgBase, Foreground = System.Windows.Media.Brushes.White, BorderBrush = AppColors.BorderSoft };
                    Grid.SetColumn(textBox, 0);

                    var combo = new ComboBox { SelectedIndex = 0, Margin = new Thickness(5, 0, 0, 0) };
                    combo.Items.Add("Somar");
                    combo.Items.Add("Definir");
                    Grid.SetColumn(combo, 1);

                    grid.Children.Add(textBox);
                    grid.Children.Add(combo);

                    var confirmBtn = new Button 
                    { 
                        Content = "Salvar Ajuste", 
                        Padding = new Thickness(10),
                        Background = AppColors.Accent,
                        Foreground = AppColors.BgBase,
                        FontWeight = FontWeights.Bold
                    };

                    confirmBtn.Click += async (s, ev) =>
                    {
                        if (TryParseDouble(textBox.Text, out double inputVal))
                        {
                            bool somar = combo.SelectedIndex == 0; // 0 = "Somar", 1 = "Definir"

                            try
                            {
                                string tenantId = CurrentUser.TenantId;
                                string storeId = StoreIdentityService.Atual(CurrentUser.StoreId);

                                // "Definir" e absoluto e portanto precisa acontecer no servidor,
                                // depois de esvaziar a fila local. Fazer isso como delta offline
                                // daria resultado errado quando dois computadores usam a loja.
                                if (!somar)
                                {
                                    if (inputVal < 0)
                                    {
                                        MessageBox.Show("O saldo do estoque não pode ser negativo.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                                        return;
                                    }

                                    confirmBtn.IsEnabled = false;
                                    var (sincronizou, erroSync) = await SincronizarDadosNuvem(aguardarCiclo: true);
                                    if (!sincronizou)
                                    {
                                        confirmBtn.IsEnabled = true;
                                        MessageBox.Show($"Conecte a internet e sincronize antes de definir um saldo.\n{erroSync}",
                                            "Ajuste não salvo", MessageBoxButton.OK, MessageBoxImage.Warning);
                                        return;
                                    }

                                    var (salvou, erroSet) = await SetEstoqueLojaAsync(storeId, product.Id, inputVal);
                                    if (!salvou)
                                    {
                                        confirmBtn.IsEnabled = true;
                                        MessageBox.Show($"Não foi possível definir o saldo: {erroSet}",
                                            "Ajuste não salvo", MessageBoxButton.OK, MessageBoxImage.Error);
                                        return;
                                    }

                                    await SincronizarDadosNuvem(aguardarCiclo: true);
                                    adjustWindow.Close();
                                    LoadStock(SearchStockBox.Text.Trim());
                                    return;
                                }

                                bool saldoNegativo = false;
                                bool produtoSumiu = false;

                                await App.Database.RunInTransactionAsync((tx) =>
                                {
                                    // Relê o produto DENTRO da transação. O objeto carregado na
                                    // abertura do diálogo já pode estar velho: o sync roda em
                                    // background e aplica ajustes do dono enquanto esta janela
                                    // está aberta. Usar o valor velho gravaria um saldo anterior
                                    // mentiroso no histórico e, no modo "Definir", apagaria o que
                                    // tivesse entrado nesse meio-tempo.
                                    var atual = tx.Find<Product>(product.Id);
                                    if (atual == null) { produtoSumiu = true; return; }

                                    double oldQty = atual.LocalStockQuantity;
                                    double newQty = oldQty + inputVal;

                                    if (newQty < 0) { saldoNegativo = true; return; }

                                    tx.Insert(new StockMovement
                                    {
                                        Id = Guid.NewGuid().ToString(),
                                        ProductId = atual.Id,
                                        StoreId = storeId,
                                        UserId = CurrentUser.Id,
                                        TenantId = tenantId,
                                        Type = newQty >= oldQty ? "ENTRADA" : "SAIDA",
                                        Quantity = Math.Abs(newQty - oldQty),
                                        Reason = "AJUSTE_MANUAL",
                                        CreatedAt = DateTime.Now,
                                        IsSynced = false,
                                        BalanceBefore = oldQty,
                                        BalanceAfter = newQty
                                    });

                                    atual.LocalStockQuantity = newQty;
                                    tx.Update(atual);

                                    // Mantém o objeto que a tela segura coerente com o gravado.
                                    product.LocalStockQuantity = newQty;
                                });

                                if (produtoSumiu)
                                {
                                    MessageBox.Show("Este produto não existe mais no estoque local.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                                    return;
                                }
                                if (saldoNegativo)
                                {
                                    MessageBox.Show("O saldo do estoque não pode ser negativo.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                                    return;
                                }

                                adjustWindow.Close();
                                if (MainTabControl.SelectedIndex == 1)
                                {
                                    LoadStock(SearchStockBox.Text.Trim());
                                }
                                else if (MainTabControl.SelectedIndex == 4)
                                {
                                    LoadLowStockAlerts();
                                }
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show($"Erro ao salvar estoque: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        }
                        else
                        {
                            MessageBox.Show("Digite uma quantidade válida.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    };

                    stack.Children.Add(infoLabel);
                    stack.Children.Add(grid);
                    stack.Children.Add(confirmBtn);
                    adjustWindow.Content = stack;

                    textBox.Focus();
                    adjustWindow.ShowDialog();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao abrir ajuste: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // ============ ESTOQUE DA REDE: dono edita o estoque de cada loja ============

        // Monta o seletor de loja na aba Estoque (somente DONO). "Minha loja" + uma opção por loja.
        private async Task SetupStockStoreSelector()
        {
            if (CurrentUser.Role.ToUpper() != "DONO")
            {
                StockStorePanel.Visibility = Visibility.Collapsed;
                return;
            }
            StockStorePanel.Visibility = Visibility.Visible;
            if (_stockSelectorReady) return;

            var lojas = _lojasCache.Count > 0 ? _lojasCache : await FetchLojasAsync();
            _suppressStockStoreEvent = true;
            StockStoreSelector.Items.Clear();
            // 1ª opção = estoque LOCAL desta máquina, onde o cadastro (novo/editar/excluir) é
            // permitido. As demais opções são as lojas da rede (via nuvem), só ajuste de saldo.
            StockStoreSelector.Items.Add(new ComboBoxItem { Content = "📝 Modo Cadastro (Estoque Local)", Tag = "" });
            foreach (var l in lojas)
            {
                if (string.IsNullOrEmpty(l.StoreId)) continue;
                StockStoreSelector.Items.Add(new ComboBoxItem { Content = l.Nome, Tag = l.StoreId });
            }
            StockStoreSelector.SelectedIndex = 0; // abre no Modo Cadastro (local)
            _suppressStockStoreEvent = false;
            if (lojas.Count > 0)
            {
                _stockSelectorReady = true;
            }
            _stockRemoteStoreId = ""; // 1ª opção = local
        }

        // Busca a lista de lojas da rede (id + nome) reusando a RPC do painel do dono.
        private async Task<List<RedeLojaView>> FetchLojasAsync()
        {
            var result = new List<RedeLojaView>();
            try
            {
                var (from, to) = GetRedePeriodo();
                string baseUrl = EnvService.Get("SUPABASE_URL").TrimEnd('/');
                string anon = EnvService.Get("SUPABASE_ANON_KEY");
                if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(anon)) return result;

                var payload = new { p_email = _donoEmail, p_password = _donoPassword, p_from = from, p_to = to };
                var content = new System.Net.Http.StringContent(
                    Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/get_dashboard_rede");
                req.Content = content;
                req.Headers.Add("apikey", anon);
                req.Headers.Add("Authorization", "Bearer " + anon);

                var resp = await _redeHttp.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return result;
                var j = Newtonsoft.Json.Linq.JObject.Parse(await resp.Content.ReadAsStringAsync());
                if (j["lojas"] is Newtonsoft.Json.Linq.JArray arr)
                    foreach (var l in arr)
                        result.Add(new RedeLojaView
                        {
                            StoreId = l["storeId"]?.ToString() ?? string.Empty,
                            Nome = l["nome"]?.ToString() ?? string.Empty
                        });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FetchLojas Error]: {ex.Message}");
            }
            return result;
        }

        private void StockStoreSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressStockStoreEvent) return;
            string storeId = (StockStoreSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            _stockRemoteStoreId = storeId;

            // Resetar filtro de estoque ao mudar de loja para evitar inconsistência
            _stockCategoryFilter = "TODOS";
            UpdateStockFilterButtons();

            SearchStockBox.IsEnabled = string.IsNullOrEmpty(storeId);
            if (string.IsNullOrEmpty(storeId)) LoadStock(SearchStockBox.Text.Trim());
            else _ = LoadRemoteStock(storeId);
        }

        // Carrega o estoque de uma loja remota a partir da nuvem (somente dono).
        private async Task LoadRemoteStock(string storeId)
        {
            try
            {
                var (from, to) = GetRedePeriodo();
                string baseUrl = EnvService.Get("SUPABASE_URL").TrimEnd('/');
                string anon = EnvService.Get("SUPABASE_ANON_KEY");
                if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(anon))
                {
                    MessageBox.Show("Configuração da nuvem ausente no .env.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var payload = new { p_email = _donoEmail, p_password = _donoPassword, p_store_id = storeId, p_from = from, p_to = to };
                var content = new System.Net.Http.StringContent(
                    Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/get_loja_detalhe");
                req.Content = content;
                req.Headers.Add("apikey", anon);
                req.Headers.Add("Authorization", "Bearer " + anon);

                var resp = await _redeHttp.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    MessageBox.Show($"Sem conexão com a nuvem ({(int)resp.StatusCode}).", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    StockProductsList.ItemsSource = null;
                    return;
                }

                var j = Newtonsoft.Json.Linq.JObject.Parse(body);
                if (j["error"] != null)
                {
                    string err = j["error"]!.ToString();
                    MessageBox.Show(err == "invalid_credentials"
                        ? "Faça login ONLINE como dono para editar o estoque das lojas."
                        : err == "forbidden" ? "Seu usuário não tem acesso." : err,
                        "Estoque da rede", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StockProductsList.ItemsSource = null;
                    return;
                }

                var views = new List<StockProductView>();
                if (j["estoque"] is Newtonsoft.Json.Linq.JArray estoqueArr)
                {
                    foreach (var p in estoqueArr)
                    {
                        views.Add(new StockProductView
                        {
                            Id = p["produto_id"]?.ToString() ?? string.Empty,
                            Name = p["nome"]?.ToString() ?? "Produto",
                            CategoryName = string.Empty,
                            Barcode = p["barcode"]?.ToString() ?? string.Empty,
                            Type = p["tipo"]?.ToString() ?? string.Empty,
                            UnitMeasure = p["unidade"]?.ToString() ?? "UN",
                            PriceSale = (int?)p["preco_venda"] ?? 0,
                            PriceCost = (int?)p["preco_custo"] ?? 0,
                            LocalStockQuantity = (double?)p["quantidade"] ?? 0,
                            MinStock = (double?)p["minimo"] ?? 0
                        });
                    }
                }
                _remoteStockCache = views;
                ApplyRemoteStockFilter();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Sem conexão com a nuvem.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"[LoadRemoteStock Error]: {ex.Message}");
            }
        }

        // Ajuste de saldo de uma loja remota — grava na nuvem; a loja aplica no próximo sync.
        private Task AdjustStockRemote(string productId)
        {
            var lista = StockProductsList.ItemsSource as List<StockProductView>;
            var item = lista?.FirstOrDefault(v => v.Id == productId);
            if (item == null) return Task.CompletedTask;

            string lojaNome = (StockStoreSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "loja";
            bool isKg = item.UnitMeasure == "KG";

            var win = new Window
            {
                Title = $"Ajustar Estoque ({lojaNome}) - {item.Name}",
                Width = 360,
                Height = 230,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.ToolWindow,
                Background = AppColors.Surface,
                Foreground = AppColors.TextPrimary
            };
            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock
            {
                Text = $"Estoque atual na {lojaNome}: " + (isKg ? $"{item.LocalStockQuantity:F3} KG" : $"{item.LocalStockQuantity:F0} UN"),
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 15),
                TextWrapping = TextWrapping.Wrap
            });

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 15) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            var textBox = new TextBox { Padding = new Thickness(8), FontSize = 14, Background = AppColors.BgBase, Foreground = System.Windows.Media.Brushes.White, BorderBrush = AppColors.BorderSoft };
            Grid.SetColumn(textBox, 0);
            var combo = new ComboBox { SelectedIndex = 0, Margin = new Thickness(5, 0, 0, 0) };
            combo.Items.Add("Somar");
            combo.Items.Add("Definir");
            Grid.SetColumn(combo, 1);
            grid.Children.Add(textBox);
            grid.Children.Add(combo);

            var confirmBtn = new Button { Content = "Salvar na nuvem", Padding = new Thickness(10), Background = AppColors.Accent, Foreground = AppColors.BgBase, FontWeight = FontWeights.Bold };
            confirmBtn.Click += async (s, ev) =>
            {
                if (!TryParseDouble(textBox.Text, out double inputVal))
                {
                    MessageBox.Show("Digite uma quantidade válida.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                double newQty = combo.SelectedIndex == 0 ? item.LocalStockQuantity + inputVal : inputVal;
                if (newQty < 0)
                {
                    MessageBox.Show("O saldo do estoque não pode ser negativo.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                confirmBtn.IsEnabled = false;
                var (ok, err) = await SetEstoqueLojaAsync(_stockRemoteStoreId, productId, newQty);
                if (ok)
                {
                    win.Close();
                    await LoadRemoteStock(_stockRemoteStoreId);
                }
                else
                {
                    confirmBtn.IsEnabled = true;
                    MessageBox.Show($"Não foi possível salvar: {err}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            stack.Children.Add(grid);
            stack.Children.Add(confirmBtn);
            win.Content = stack;
            textBox.Focus();
            win.ShowDialog();
            return Task.CompletedTask;
        }

        // POST na RPC set_estoque_loja (dono grava o saldo da loja na nuvem).
        private async Task<(bool ok, string err)> SetEstoqueLojaAsync(string storeId, string productId, double quantity)
        {
            try
            {
                string baseUrl = EnvService.Get("SUPABASE_URL").TrimEnd('/');
                string anon = EnvService.Get("SUPABASE_ANON_KEY");
                if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(anon)) return (false, "Config da nuvem ausente.");

                var payload = new
                {
                    p_email = _donoEmail,
                    p_password = _donoPassword,
                    p_store_id = storeId,
                    p_items = new[] { new { productId, quantity } }
                };
                var content = new System.Net.Http.StringContent(
                    Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/set_estoque_loja");
                req.Content = content;
                req.Headers.Add("apikey", anon);
                req.Headers.Add("Authorization", "Bearer " + anon);

                var resp = await _redeHttp.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) return (false, $"HTTP {(int)resp.StatusCode}");
                var j = Newtonsoft.Json.Linq.JObject.Parse(body);
                if (j["error"] != null)
                {
                    string err = j["error"]!.ToString();
                    return (false, err == "invalid_credentials" ? "faça login online como dono"
                        : err == "forbidden" ? "sem permissão" : err);
                }
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // Preenche um seletor de loja: "Minha loja" + uma opção por loja da rede.
        private void FillStoreSelector(ComboBox cb, List<RedeLojaView> lojas)
        {
            _suppressStockStoreEvent = true;
            cb.Items.Clear();
            foreach (var l in lojas)
            {
                if (string.IsNullOrEmpty(l.StoreId)) continue;
                cb.Items.Add(new ComboBoxItem { Content = l.Nome, Tag = l.StoreId });
            }
            cb.SelectedIndex = 0;
            _suppressStockStoreEvent = false;
        }

        // ----- Alertas de Estoque por loja (DONO) -----

        private async Task SetupAlertStoreSelector()
        {
            if (CurrentUser.Role.ToUpper() != "DONO") { AlertStorePanel.Visibility = Visibility.Collapsed; return; }
            AlertStorePanel.Visibility = Visibility.Visible;
            if (_alertSelectorReady) return;
            var lojas = _lojasCache.Count > 0 ? _lojasCache : await FetchLojasAsync();
            FillStoreSelector(AlertStoreSelector, lojas);
            if (lojas.Count > 0)
            {
                _alertSelectorReady = true;
            }
            // Alinha o estado à 1ª loja exibida no dropdown (alertas do dono abrem nessa loja via nuvem).
            _alertRemoteStoreId = (AlertStoreSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        }

        private void AlertStoreSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressStockStoreEvent) return;
            string storeId = (AlertStoreSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            _alertRemoteStoreId = storeId;
            if (string.IsNullOrEmpty(storeId)) LoadLowStockAlerts();
            else _ = LoadRemoteAlerts(storeId);
        }

        // Carrega os alertas de estoque de uma loja remota a partir da nuvem.
        private async Task LoadRemoteAlerts(string storeId)
        {
            try
            {
                var arr = await FetchLojaDetalheEstoque(storeId);
                if (arr == null) { StockAlertsList.ItemsSource = null; return; }

                var alerts = new List<StockAlertView>();
                foreach (var p in arr)
                {
                    if (!(bool)(p["baixo"] ?? false)) continue;
                    alerts.Add(new StockAlertView
                    {
                        Id = p["produto_id"]!.ToString(),
                        Name = p["nome"]!.ToString(),
                        Barcode = p["barcode"]?.ToString() ?? string.Empty,
                        Type = p["tipo"]?.ToString() ?? string.Empty,
                        UnitMeasure = p["unidade"]?.ToString() ?? "UN",
                        LocalStockQuantity = (double)(p["quantidade"] ?? 0),
                        MinStock = (double)(p["minimo"] ?? 0),
                        CategoryName = string.Empty
                    });
                }
                alerts = alerts.OrderBy(a => a.Name).ToList();

                int criticalCount = alerts.Count(a => a.LocalStockQuantity <= 0);
                int lowCount = alerts.Count(a => a.LocalStockQuantity > 0);
                int totalCount = alerts.Count;
                AlertZeroStockCountText.Text = criticalCount == 1 ? "1 item" : $"{criticalCount} itens";
                AlertLowStockCountText.Text = lowCount == 1 ? "1 item" : $"{lowCount} itens";
                AlertTotalCriticalCountText.Text = totalCount == 1 ? "1 item" : $"{totalCount} itens";

                StockAlertsList.ItemsSource = alerts;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Sem conexão com a nuvem.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"[LoadRemoteAlerts Error]: {ex.Message}");
            }
        }

        // Busca o array de estoque de uma loja via get_loja_detalhe (reusado por Estoque e Alertas).
        private async Task<Newtonsoft.Json.Linq.JArray?> FetchLojaDetalheEstoque(string storeId)
        {
            var (from, to) = GetRedePeriodo();
            string baseUrl = EnvService.Get("SUPABASE_URL").TrimEnd('/');
            string anon = EnvService.Get("SUPABASE_ANON_KEY");
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(anon)) return null;

            var payload = new { p_email = _donoEmail, p_password = _donoPassword, p_store_id = storeId, p_from = from, p_to = to };
            var content = new System.Net.Http.StringContent(
                Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
            using var req = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/get_loja_detalhe");
            req.Content = content;
            req.Headers.Add("apikey", anon);
            req.Headers.Add("Authorization", "Bearer " + anon);

            var resp = await _redeHttp.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return null;
            var j = Newtonsoft.Json.Linq.JObject.Parse(body);
            if (j["error"] != null)
            {
                string err = j["error"]!.ToString();
                MessageBox.Show(err == "invalid_credentials"
                    ? "Faça login ONLINE como dono para ver as lojas da rede."
                    : err == "forbidden" ? "Seu usuário não tem acesso." : err,
                    "Rede", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            return j["estoque"] as Newtonsoft.Json.Linq.JArray;
        }

        // ----- Histórico / Cancelamentos por loja (DONO) -----

        private async Task SetupHistoryStoreSelector()
        {
            if (CurrentUser.Role.ToUpper() != "DONO") { HistoryStorePanel.Visibility = Visibility.Collapsed; return; }
            HistoryStorePanel.Visibility = Visibility.Visible;
            if (_historySelectorReady) return;
            var lojas = _lojasCache.Count > 0 ? _lojasCache : await FetchLojasAsync();
            // 1ª opção = "Todas as sedes" (consolida todas as lojas via nuvem). As demais filtram
            // por loja. O histórico do dono abre em "Todas as sedes" por padrão.
            _suppressStockStoreEvent = true;
            HistoryStoreSelector.Items.Clear();
            HistoryStoreSelector.Items.Add(new ComboBoxItem { Content = "Todas as sedes", Tag = "TODAS" });
            foreach (var l in lojas)
            {
                if (string.IsNullOrEmpty(l.StoreId)) continue;
                HistoryStoreSelector.Items.Add(new ComboBoxItem { Content = l.Nome, Tag = l.StoreId });
            }
            HistoryStoreSelector.SelectedIndex = 0;
            _suppressStockStoreEvent = false;
            if (lojas.Count > 0)
            {
                _historySelectorReady = true;
            }
            _historyRemoteStoreId = "TODAS";
        }

        private void HistoryStoreSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressStockStoreEvent) return;
            string storeId = (HistoryStoreSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            _historyRemoteStoreId = storeId;
            if (storeId == "TODAS") _ = LoadAllStoresHistory();
            else if (string.IsNullOrEmpty(storeId)) LoadSalesHistory();
            else _ = LoadRemoteHistory(storeId);
        }

        // Carrega o histórico de vendas/cancelamentos de uma loja remota a partir da nuvem.
        private async Task LoadRemoteHistory(string storeId)
        {
            try
            {
                DateTime startDate = HistoryStartDatePicker.SelectedDate ?? DateTime.Today;
                DateTime endDate = HistoryEndDatePicker.SelectedDate ?? DateTime.Today;
                if (!TimeSpan.TryParse(HistoryStartTimeTextBox.Text.Trim(), out TimeSpan startTime)) startTime = new TimeSpan(0, 0, 0);
                if (!TimeSpan.TryParse(HistoryEndTimeTextBox.Text.Trim(), out TimeSpan endTime)) endTime = new TimeSpan(23, 59, 59);
                string from = startDate.Date.Add(startTime).ToString("yyyy-MM-ddTHH:mm:ss");
                string to = endDate.Date.Add(endTime).ToString("yyyy-MM-ddTHH:mm:ss");
                string paymentFilter = (HistoryPaymentFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "TODOS";

                string baseUrl = EnvService.Get("SUPABASE_URL").TrimEnd('/');
                string anon = EnvService.Get("SUPABASE_ANON_KEY");
                if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(anon))
                {
                    MessageBox.Show("Configuração da nuvem ausente no .env.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var payload = new { p_email = _donoEmail, p_password = _donoPassword, p_store_id = storeId, p_from = from, p_to = to, p_payment = paymentFilter };
                var content = new System.Net.Http.StringContent(
                    Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/get_vendas_loja");
                req.Content = content;
                req.Headers.Add("apikey", anon);
                req.Headers.Add("Authorization", "Bearer " + anon);

                var resp = await _redeHttp.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    MessageBox.Show($"Sem conexão com a nuvem ({(int)resp.StatusCode}).", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    SalesHistoryList.ItemsSource = null;
                    return;
                }

                var j = Newtonsoft.Json.Linq.JObject.Parse(body);
                if (j["error"] != null)
                {
                    string err = j["error"]!.ToString();
                    MessageBox.Show(err == "invalid_credentials"
                        ? "Faça login ONLINE como dono para ver o histórico das lojas."
                        : err == "forbidden" ? "Seu usuário não tem acesso." : err,
                        "Rede", MessageBoxButton.OK, MessageBoxImage.Warning);
                    SalesHistoryList.ItemsSource = null;
                    return;
                }

                var views = new List<SalesHistoryView>();
                foreach (var v in (Newtonsoft.Json.Linq.JArray)j["vendas"]!)
                {
                    views.Add(new SalesHistoryView
                    {
                        Id = v["id"]?.ToString() ?? string.Empty,
                        SaleDate = (DateTime)v["data"]!,
                        Total = (int)(v["total_centavos"] ?? 0),
                        PaymentMethod = v["metodo"]?.ToString() ?? string.Empty,
                        PaymentStatus = v["status"]?.ToString() ?? string.Empty,
                        ItemCount = (double)(v["itens"] ?? 0)
                    });
                }
                SalesHistoryList.ItemsSource = views;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Sem conexão com a nuvem.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"[LoadRemoteHistory Error]: {ex.Message}");
            }
        }

        // Carrega o histórico consolidado de TODAS as lojas (modo "Todas as sedes") via get_vendas_rede.
        private async Task LoadAllStoresHistory()
        {
            try
            {
                DateTime startDate = HistoryStartDatePicker.SelectedDate ?? DateTime.Today;
                DateTime endDate = HistoryEndDatePicker.SelectedDate ?? DateTime.Today;
                if (!TimeSpan.TryParse(HistoryStartTimeTextBox.Text.Trim(), out TimeSpan startTime)) startTime = new TimeSpan(0, 0, 0);
                if (!TimeSpan.TryParse(HistoryEndTimeTextBox.Text.Trim(), out TimeSpan endTime)) endTime = new TimeSpan(23, 59, 59);
                string from = startDate.Date.Add(startTime).ToString("yyyy-MM-ddTHH:mm:ss");
                string to = endDate.Date.Add(endTime).ToString("yyyy-MM-ddTHH:mm:ss");
                string paymentFilter = (HistoryPaymentFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "TODOS";

                string baseUrl = EnvService.Get("SUPABASE_URL").TrimEnd('/');
                string anon = EnvService.Get("SUPABASE_ANON_KEY");
                if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(anon))
                {
                    MessageBox.Show("Configuração da nuvem ausente no .env.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // p_store_id null = todas as lojas do tenant.
                var payload = new { p_email = _donoEmail, p_password = _donoPassword, p_from = from, p_to = to, p_store_id = (string?)null, p_payment = paymentFilter };
                var content = new System.Net.Http.StringContent(
                    Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/get_vendas_rede");
                req.Content = content;
                req.Headers.Add("apikey", anon);
                req.Headers.Add("Authorization", "Bearer " + anon);

                var resp = await _redeHttp.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    MessageBox.Show($"Sem conexão com a nuvem ({(int)resp.StatusCode}).", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    SalesHistoryList.ItemsSource = null;
                    return;
                }

                var j = Newtonsoft.Json.Linq.JObject.Parse(body);
                if (j["error"] != null)
                {
                    string err = j["error"]!.ToString();
                    MessageBox.Show(err == "invalid_credentials"
                        ? "Faça login ONLINE como dono para ver o histórico das lojas."
                        : err == "forbidden" ? "Seu usuário não tem acesso." : err,
                        "Rede", MessageBoxButton.OK, MessageBoxImage.Warning);
                    SalesHistoryList.ItemsSource = null;
                    return;
                }

                var views = new List<SalesHistoryView>();
                foreach (var v in (Newtonsoft.Json.Linq.JArray)j["vendas"]!)
                {
                    views.Add(new SalesHistoryView
                    {
                        Id = v["id"]?.ToString() ?? string.Empty,
                        StoreId = v["storeId"]?.ToString() ?? string.Empty,
                        SaleDate = (DateTime)v["data"]!,
                        Total = (int)(v["total_centavos"] ?? 0),
                        PaymentMethod = v["metodo"]?.ToString() ?? string.Empty,
                        PaymentStatus = v["status"]?.ToString() ?? string.Empty,
                        ItemCount = (double)(v["itens"] ?? 0)
                    });
                }
                SalesHistoryList.ItemsSource = views;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Sem conexão com a nuvem.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"[LoadAllStoresHistory Error]: {ex.Message}");
            }
        }

        private void AddProductButton_Click(object sender, RoutedEventArgs e)
        {
            // Cadastro de produto agora é na nuvem (vale para todas as lojas), não mais local.
            OpenNovoProdutoNuvem();
        }

        // No modo "editar loja remota" o dono só ajusta saldo; cadastro de produto é feito na loja.
        private bool StockRemoteBlocked()
        {
            if (string.IsNullOrEmpty(_stockRemoteStoreId)) return false;
            MessageBox.Show("Cadastro de produtos (novo/editar/excluir) é feito na própria loja.\n" +
                "Aqui você ajusta apenas o saldo de estoque da loja selecionada.",
                "Estoque da rede", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }

        private async void EditProductButton_Click(object sender, RoutedEventArgs e)
        {
            if (StockRemoteBlocked()) return;
            if (sender is Button btn && btn.Tag is string productId)
            {
                try
                {
                    var connection = App.Database.GetConnection();
                    var product = await connection.FindAsync<Product>(productId);
                    if (product != null)
                    {
                        OpenProductFormWindow(product);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao buscar produto: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // Cadastro de produto NOVO: cria no catálogo da NUVEM (disponível em todas as lojas,
        // saldo zero). Cada loja ajusta a quantidade depois. Substitui o cadastro local antigo,
        // que ficava preso a uma única loja. Edição segue em OpenProductFormWindow.
        private async void OpenNovoProdutoNuvem()
        {
            if (string.IsNullOrEmpty(_donoEmail) || string.IsNullOrEmpty(_donoPassword))
            {
                MessageBox.Show("Para cadastrar um produto na rede, faça login ONLINE como dono.",
                    "Novo produto", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string baseUrl = EnvService.Get("SUPABASE_URL").TrimEnd('/');
            string anon = EnvService.Get("SUPABASE_ANON_KEY");
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(anon))
            {
                MessageBox.Show("Configuração da nuvem ausente no .env.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var formWindow = new Window
            {
                Title = "Novo Produto (rede)",
                Width = 450,
                Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.ToolWindow,
                Background = AppColors.Surface,
                Foreground = AppColors.TextPrimary
            };
            var stack = new StackPanel { Margin = new Thickness(20) };
            Func<string, FrameworkElement, StackPanel> createField = (labelStr, element) =>
            {
                var p = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                p.Children.Add(new TextBlock { Text = labelStr, Foreground = AppColors.TextMuted, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
                p.Children.Add(element);
                return p;
            };
            Func<TextBox> mkBox = () => new TextBox { Padding = new Thickness(8), FontSize = 14, Background = AppColors.BgBase, Foreground = System.Windows.Media.Brushes.White, BorderBrush = AppColors.BorderSoft };

            var txtNome = mkBox();
            var txtBarcode = mkBox();
            // Fundo CLARO de propósito: o popup do ComboBox do WPF usa cores de sistema,
            // então texto preto sobre AppColors.Surface (#141419) ficava invisível — o
            // usuário não enxergava a categoria selecionada e acabava salvando a errada.
            var cmbCategoria = new ComboBox { Background = System.Windows.Media.Brushes.White, Foreground = System.Windows.Media.Brushes.Black, FontSize = 14, Padding = new Thickness(6, 4, 6, 4) };
            var cmbUnidade = new ComboBox { SelectedIndex = 0, Background = System.Windows.Media.Brushes.White, Foreground = System.Windows.Media.Brushes.Black, FontSize = 14, Padding = new Thickness(6, 4, 6, 4) };
            cmbUnidade.Items.Add("UN");
            cmbUnidade.Items.Add("KG");
            var txtPrecoVenda = mkBox(); txtPrecoVenda.Text = "0,00";
            var txtPrecoCusto = mkBox(); txtPrecoCusto.Text = "0,00";

            stack.Children.Add(new TextBlock { Text = "O produto fica disponível em todas as lojas (saldo zero). Cada loja ajusta a quantidade depois.", Foreground = AppColors.TextMuted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            stack.Children.Add(createField("Nome do Produto", txtNome));

            // Código de barras + botão "Gerar" para produtos SEM código de fábrica
            // (coxinha, salgado, bolo). O código vem do servidor (gerar_codigo_interno):
            // EAN-13 com prefixo 2, reservado pela GS1 para uso interno da loja — nunca
            // colide com produto de fábrica (789/790) nem com outro gerado (sequência).
            var barcodeGrid = new Grid();
            barcodeGrid.ColumnDefinitions.Add(new ColumnDefinition());
            barcodeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            barcodeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var btnGerarCodigo = new Button
            {
                Content = "Gerar",
                Padding = new Thickness(12, 8, 12, 8),
                Background = AppColors.Surface,
                Foreground = AppColors.Accent,
                BorderBrush = AppColors.BorderSoft,
                BorderThickness = new Thickness(1),
                ToolTip = "Gera um código interno para produto sem código de barras (coxinha, salgado, bolo)"
            };
            barcodeGrid.Children.Add(txtBarcode);
            Grid.SetColumn(txtBarcode, 0);
            barcodeGrid.Children.Add(btnGerarCodigo);
            Grid.SetColumn(btnGerarCodigo, 2);
            stack.Children.Add(createField("Código de Barras * (sem código? clique em Gerar)", barcodeGrid));

            btnGerarCodigo.Click += async (s, ev) =>
            {
                btnGerarCodigo.IsEnabled = false;
                var conteudoAnterior = btnGerarCodigo.Content;
                btnGerarCodigo.Content = "...";
                try
                {
                    var payloadGer = new { p_email = _donoEmail, p_password = _donoPassword };
                    using var reqGer = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/gerar_codigo_interno");
                    reqGer.Content = new System.Net.Http.StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(payloadGer), Encoding.UTF8, "application/json");
                    reqGer.Headers.Add("apikey", anon);
                    reqGer.Headers.Add("Authorization", "Bearer " + anon);
                    var respGer = await _redeHttp.SendAsync(reqGer);
                    var jGer = Newtonsoft.Json.Linq.JObject.Parse(await respGer.Content.ReadAsStringAsync());
                    if (jGer["error"] != null)
                    {
                        string errGer = jGer["error"]!.ToString();
                        MessageBox.Show(errGer == "invalid_credentials" ? "Sessão do dono expirada, faça login online de novo." : errGer,
                            "Gerar código", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        txtBarcode.Text = jGer["codigo"]?.ToString() ?? "";
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Sem conexão com a nuvem para gerar o código.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    System.Diagnostics.Debug.WriteLine($"[gerar_codigo_interno Error]: {ex.Message}");
                }
                finally
                {
                    btnGerarCodigo.Content = conteudoAnterior;
                    btnGerarCodigo.IsEnabled = true;
                }
            };

            stack.Children.Add(createField("Categoria", cmbCategoria));

            // Tipo do produto ESCOLHIDO pelo usuário. Antes era deduzido do nome da categoria,
            // o que tornava impossível cadastrar um pão: o tipo PAO_FRANCES nunca era gerado e
            // o atalho de pão do caixa (F4) não encontrava o produto recém-criado.
            var cmbTipoProduto = new ComboBox { Background = System.Windows.Media.Brushes.White, Foreground = System.Windows.Media.Brushes.Black, FontSize = 14, Padding = new Thickness(6, 4, 6, 4) };
            cmbTipoProduto.Items.Add(new ComboBoxItem { Content = "Mercadoria (revenda)", Tag = "NORMAL" });
            cmbTipoProduto.Items.Add(new ComboBoxItem { Content = "Produção própria (bolo, salgado, torta)", Tag = "PRODUCAO" });
            cmbTipoProduto.Items.Add(new ComboBoxItem { Content = "Pão (vendido por valor, tecla F4)", Tag = "PAO_FRANCES" });
            cmbTipoProduto.SelectedIndex = 0;
            stack.Children.Add(createField("Tipo do produto", cmbTipoProduto));

            stack.Children.Add(createField("Unidade de Medida", cmbUnidade));

            // Ajuda o operador: ao escolher "Produção Própria" na categoria, sugere o tipo
            // correspondente. Continua sendo só uma sugestão — a escolha final é do usuário.
            cmbCategoria.SelectionChanged += (s, ev) =>
            {
                var cat = (cmbCategoria.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
                bool ehProducao = cat.ToLower().Contains("produ");
                // Não mexe se o usuário já marcou "Pão" de propósito.
                var tipoAtual = (cmbTipoProduto.SelectedItem as ComboBoxItem)?.Tag as string;
                if (tipoAtual == "PAO_FRANCES") return;
                cmbTipoProduto.SelectedIndex = ehProducao ? 1 : 0;
            };

            var pricesGrid = new Grid { Margin = new Thickness(0, 0, 0, 15) };
            pricesGrid.ColumnDefinitions.Add(new ColumnDefinition());
            pricesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            pricesGrid.ColumnDefinitions.Add(new ColumnDefinition());
            var custoPanel = createField("Preço Custo (R$)", txtPrecoCusto);
            var vendaPanel = createField("Preço Venda (R$)", txtPrecoVenda);
            pricesGrid.Children.Add(custoPanel); Grid.SetColumn(custoPanel, 0);
            pricesGrid.Children.Add(vendaPanel); Grid.SetColumn(vendaPanel, 2);
            stack.Children.Add(pricesGrid);

            // Em KG o preço cadastrado é o preço do QUILO (a etiqueta de peso multiplica
            // pelo peso pesado). Deixa isso explícito no rótulo para evitar cadastrar
            // o preço de uma peça inteira por engano.
            cmbUnidade.SelectionChanged += (s, ev) =>
            {
                bool ehKg = (cmbUnidade.SelectedItem?.ToString() ?? "UN") == "KG";
                if (custoPanel.Children[0] is TextBlock lblCusto)
                    lblCusto.Text = ehKg ? "Custo por KG (R$)" : "Preço Custo (R$)";
                if (vendaPanel.Children[0] is TextBlock lblVenda)
                    lblVenda.Text = ehKg ? "Preço por KG (R$)" : "Preço Venda (R$)";
            };

            var confirmBtn = new Button
            {
                Content = "Cadastrar Produto",
                Padding = new Thickness(12),
                Background = AppColors.Accent,
                Foreground = AppColors.BgBase,
                FontWeight = FontWeights.Bold,
                IsEnabled = false
            };
            stack.Children.Add(confirmBtn);
            formWindow.Content = new ScrollViewer { Content = stack };

            // Carrega as categorias da nuvem para o dropdown
            try
            {
                var payloadCat = new { p_email = _donoEmail, p_password = _donoPassword };
                using var reqCat = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/get_categorias");
                reqCat.Content = new System.Net.Http.StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(payloadCat), Encoding.UTF8, "application/json");
                reqCat.Headers.Add("apikey", anon);
                reqCat.Headers.Add("Authorization", "Bearer " + anon);
                var respCat = await _redeHttp.SendAsync(reqCat);
                var jCat = Newtonsoft.Json.Linq.JObject.Parse(await respCat.Content.ReadAsStringAsync());
                if (jCat["categorias"] is Newtonsoft.Json.Linq.JArray arr)
                    foreach (var c in arr)
                        cmbCategoria.Items.Add(new ComboBoxItem { Content = c["nome"]?.ToString() ?? "", Tag = c["id"]?.ToString() ?? "" });
                if (cmbCategoria.Items.Count > 0) { cmbCategoria.SelectedIndex = 0; confirmBtn.IsEnabled = true; }
                else MessageBox.Show("Nenhuma categoria encontrada na nuvem.", "Novo produto", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Sem conexão com a nuvem para carregar as categorias.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"[get_categorias Error]: {ex.Message}");
                return;
            }

            confirmBtn.Click += async (s, ev) =>
            {
                string nome = txtNome.Text.Trim();
                if (string.IsNullOrEmpty(nome)) { MessageBox.Show("O nome do produto é obrigatório.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                string barcode = txtBarcode.Text.Trim();
                if (string.IsNullOrEmpty(barcode)) { MessageBox.Show("O código de barras é obrigatório.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                if (!TryParseDouble(txtPrecoVenda.Text, out double vendaVal) || vendaVal < 0 ||
                    !TryParseDouble(txtPrecoCusto.Text, out double custoVal) || custoVal < 0)
                {
                    MessageBox.Show("Preços inválidos.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning); return;
                }
                var catItem = cmbCategoria.SelectedItem as ComboBoxItem;
                string catId = catItem?.Tag as string ?? "";
                string catNome = catItem?.Content?.ToString() ?? "";
                if (string.IsNullOrEmpty(catId)) { MessageBox.Show("Selecione uma categoria.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                // O tipo vem da ESCOLHA do usuário, não mais deduzido da categoria. Deduzir
                // impossibilitava cadastrar pão (o tipo PAO_FRANCES nunca era gerado), então o
                // produto era criado mas o atalho de pão do caixa não o reconhecia.
                string tipo = (cmbTipoProduto.SelectedItem as ComboBoxItem)?.Tag as string ?? "NORMAL";
                string unidade = cmbUnidade.SelectedItem?.ToString() ?? "UN";

                confirmBtn.IsEnabled = false; confirmBtn.Content = "Cadastrando...";
                try
                {
                    var payload = new
                    {
                        p_email = _donoEmail,
                        p_password = _donoPassword,
                        p_nome = nome,
                        p_tipo = tipo,
                        p_unidade = unidade,
                        p_preco_venda = (int)Math.Round(vendaVal * 100),
                        p_preco_custo = (int)Math.Round(custoVal * 100),
                        p_categoria_id = catId,
                        p_codigo_barras = string.IsNullOrEmpty(barcode) ? null : barcode
                    };
                    using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/criar_produto");
                    req.Content = new System.Net.Http.StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                    req.Headers.Add("apikey", anon);
                    req.Headers.Add("Authorization", "Bearer " + anon);
                    var resp = await _redeHttp.SendAsync(req);
                    var j = Newtonsoft.Json.Linq.JObject.Parse(await resp.Content.ReadAsStringAsync());
                    if (j["error"] != null)
                    {
                        string err = j["error"]!.ToString();
                        MessageBox.Show(err == "invalid_credentials" ? "Sessão do dono expirada, faça login online de novo." :
                            err == "categoria_invalida" ? "Categoria inválida." :
                            err == "nome_obrigatorio" ? "Informe o nome do produto." :
                            err == "barcode_obrigatorio" ? "Informe o código de barras do produto." :
                            err == "barcode_duplicado" ? "Já existe um produto ATIVO com este código de barras." : err,
                            "Novo produto", MessageBoxButton.OK, MessageBoxImage.Warning);
                        confirmBtn.IsEnabled = true; confirmBtn.Content = "Cadastrar Produto";
                        return;
                    }

                    // Quando um produto com este código de barras já existia mas estava INATIVO
                    // (excluído antes), a RPC reativa a linha em vez de criar uma nova — evita
                    // duplicata silenciosa e explica a diferença ao usuário.
                    bool reativado = (bool?)j["reativado"] ?? false;
                    string msgSucesso = reativado
                        ? $"Produto \"{nome}\" já existia (estava excluído) e foi REATIVADO com os novos dados.\nSincronizando para esta loja..."
                        : $"Produto \"{nome}\" cadastrado e disponível em todas as lojas.\nSincronizando para esta loja...";

                    formWindow.Close();
                    MessageBox.Show(msgSucesso, "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
                    await RunSincronizacaoSilenciosa();
                    // Atualiza a lista visível, seja o Modo Cadastro Local, seja uma loja remota —
                    // antes só recarregava no modo local, então criar produto vendo o estoque de
                    // outra loja "sumia" da tela (RPC criava certo, só a UI não atualizava).
                    if (string.IsNullOrEmpty(_stockRemoteStoreId)) LoadStock(SearchStockBox.Text.Trim());
                    else _ = LoadRemoteStock(_stockRemoteStoreId);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Sem conexão com a nuvem para cadastrar o produto.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    System.Diagnostics.Debug.WriteLine($"[criar_produto Error]: {ex.Message}");
                    confirmBtn.IsEnabled = true; confirmBtn.Content = "Cadastrar Produto";
                }
            };

            formWindow.ShowDialog();
        }

        // Edição de produto EXISTENTE: propaga para a NUVEM via atualizar_produto (mesmo padrão
        // de criar_produto/excluir_produto), depois aplica local e sincroniza. Antes esta função
        // só gravava no SQLite local — o próximo pull de sync baixava o preço antigo da nuvem e
        // SOBRESCREVIA a edição (bug: "diz sucesso mas volta pro preço antigo"). Único chamador é
        // EditProductButton_Click, sempre com productToEdit != null (o antigo branch de criação
        // aqui virou código morto desde que "Novo Produto" passou a usar OpenNovoProdutoNuvem).
        private async void OpenProductFormWindow(Product productToEdit)
        {
            if (string.IsNullOrEmpty(_donoEmail) || string.IsNullOrEmpty(_donoPassword))
            {
                MessageBox.Show("Para editar um produto da rede, faça login ONLINE como dono.",
                    "Editar produto", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string baseUrl = EnvService.Get("SUPABASE_URL").TrimEnd('/');
            string anon = EnvService.Get("SUPABASE_ANON_KEY");
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(anon))
            {
                MessageBox.Show("Configuração da nuvem ausente no .env.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var formWindow = new Window
            {
                Title = $"Editar Produto - {productToEdit.Name}",
                Width = 450,
                Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.ToolWindow,
                Background = AppColors.Surface,
                Foreground = AppColors.TextPrimary
            };

            var stack = new StackPanel { Margin = new Thickness(20) };

            Func<string, FrameworkElement, StackPanel> createField = (labelStr, element) =>
            {
                var p = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                p.Children.Add(new TextBlock { Text = labelStr, Foreground = AppColors.TextMuted, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
                p.Children.Add(element);
                return p;
            };

            Func<TextBox> createTextBox = () => new TextBox { Padding = new Thickness(8), FontSize = 14, Background = AppColors.BgBase, Foreground = System.Windows.Media.Brushes.White, BorderBrush = AppColors.BorderSoft };

            var txtNome = createTextBox();
            var txtBarcode = createTextBox();
            // Fundo CLARO de propósito: o popup do ComboBox do WPF usa cores de sistema,
            // então texto preto sobre AppColors.Surface (#141419) ficava invisível — o
            // usuário não enxergava a categoria selecionada e acabava salvando a errada.
            var cmbCategoria = new ComboBox { Background = System.Windows.Media.Brushes.White, Foreground = System.Windows.Media.Brushes.Black, FontSize = 14, Padding = new Thickness(6, 4, 6, 4) };
            var cmbUnidade = new ComboBox { SelectedIndex = 0, Background = System.Windows.Media.Brushes.White, Foreground = System.Windows.Media.Brushes.Black, FontSize = 14, Padding = new Thickness(6, 4, 6, 4) };
            cmbUnidade.Items.Add("UN");
            cmbUnidade.Items.Add("KG");
            var txtPrecoVenda = createTextBox();
            var txtPrecoCusto = createTextBox();
            var txtMinStock = createTextBox();

            txtNome.Text = productToEdit.Name;
            txtBarcode.Text = productToEdit.Barcode;
            cmbUnidade.SelectedItem = productToEdit.UnitMeasure;
            txtPrecoVenda.Text = (productToEdit.PriceSale / 100.0).ToString("F2");
            txtPrecoCusto.Text = (productToEdit.PriceCost / 100.0).ToString("F2");
            txtMinStock.Text = productToEdit.MinStock.ToString();

            stack.Children.Add(createField("Nome do Produto", txtNome));
            stack.Children.Add(createField("Código de Barras", txtBarcode));
            stack.Children.Add(createField("Categoria", cmbCategoria));

            // Tipo do produto, agora PRESERVADO na edição. Antes era recalculado a partir da
            // categoria, então editar qualquer coisa de um pão o transformava em "PRODUCAO" e
            // quebrava o atalho de pão do caixa (foi o que aconteceu com o Pão Massa Fina).
            var cmbTipoProduto = new ComboBox { Background = System.Windows.Media.Brushes.White, Foreground = System.Windows.Media.Brushes.Black, FontSize = 14, Padding = new Thickness(6, 4, 6, 4) };
            cmbTipoProduto.Items.Add(new ComboBoxItem { Content = "Mercadoria (revenda)", Tag = "NORMAL" });
            cmbTipoProduto.Items.Add(new ComboBoxItem { Content = "Produção própria (bolo, salgado, torta)", Tag = "PRODUCAO" });
            cmbTipoProduto.Items.Add(new ComboBoxItem { Content = "Pão (vendido por valor, tecla F4)", Tag = "PAO_FRANCES" });
            foreach (ComboBoxItem it in cmbTipoProduto.Items)
            {
                if ((it.Tag as string) == productToEdit.Type) { cmbTipoProduto.SelectedItem = it; break; }
            }
            // Tipos legados que não estão na lista (ex.: SALGADO, BOLO) caem em produção própria.
            if (cmbTipoProduto.SelectedItem == null)
                cmbTipoProduto.SelectedIndex = (productToEdit.Type == "NORMAL") ? 0 : 1;
            stack.Children.Add(createField("Tipo do produto", cmbTipoProduto));

            stack.Children.Add(createField("Unidade de Medida", cmbUnidade));

            var pricesGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            pricesGrid.ColumnDefinitions.Add(new ColumnDefinition());
            pricesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            pricesGrid.ColumnDefinitions.Add(new ColumnDefinition());
            var costPanel = createField("Preço Custo (R$)", txtPrecoCusto);
            var salePanel = createField("Preço Venda (R$)", txtPrecoVenda);
            pricesGrid.Children.Add(costPanel);
            Grid.SetColumn(costPanel, 0);
            pricesGrid.Children.Add(salePanel);
            Grid.SetColumn(salePanel, 2);
            stack.Children.Add(pricesGrid);

            // Estoque Mínimo é campo LOCAL (alerta de baixo estoque desta máquina) — não é
            // enviado à nuvem, igual sempre foi (o estoque em si é tratado à parte do cadastro).
            stack.Children.Add(createField("Estoque Mínimo (local)", txtMinStock));

            var confirmBtn = new Button
            {
                Content = "Salvar Alterações",
                Padding = new Thickness(12),
                Background = AppColors.Accent,
                Foreground = AppColors.BgBase,
                FontWeight = FontWeights.Bold,
                IsEnabled = false
            };
            stack.Children.Add(confirmBtn);

            var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            formWindow.Content = scroll;

            // Carrega categorias da nuvem e pré-seleciona a atual do produto
            try
            {
                var payloadCat = new { p_email = _donoEmail, p_password = _donoPassword };
                using var reqCat = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/get_categorias");
                reqCat.Content = new System.Net.Http.StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(payloadCat), Encoding.UTF8, "application/json");
                reqCat.Headers.Add("apikey", anon);
                reqCat.Headers.Add("Authorization", "Bearer " + anon);
                var respCat = await _redeHttp.SendAsync(reqCat);
                var jCat = Newtonsoft.Json.Linq.JObject.Parse(await respCat.Content.ReadAsStringAsync());
                if (jCat["categorias"] is Newtonsoft.Json.Linq.JArray arr)
                {
                    foreach (var c in arr)
                    {
                        var item = new ComboBoxItem { Content = c["nome"]?.ToString() ?? "", Tag = c["id"]?.ToString() ?? "" };
                        cmbCategoria.Items.Add(item);
                        if ((c["id"]?.ToString() ?? "") == productToEdit.CategoryId) cmbCategoria.SelectedItem = item;
                    }
                }
                if (cmbCategoria.SelectedItem == null && cmbCategoria.Items.Count > 0) cmbCategoria.SelectedIndex = 0;
                confirmBtn.IsEnabled = cmbCategoria.Items.Count > 0;
                if (cmbCategoria.Items.Count == 0)
                    MessageBox.Show("Nenhuma categoria encontrada na nuvem.", "Editar produto", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Sem conexão com a nuvem para carregar as categorias.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"[get_categorias Error]: {ex.Message}");
                formWindow.Close();
                return;
            }

            confirmBtn.Click += async (s, ev) =>
            {
                var name = txtNome.Text.Trim();
                var barcode = (txtBarcode?.Text ?? string.Empty).Trim();
                var unidade = cmbUnidade.SelectedItem?.ToString() ?? "UN";
                var catItem = cmbCategoria.SelectedItem as ComboBoxItem;
                string catId = catItem?.Tag as string ?? "";
                string tipo = (cmbTipoProduto.SelectedItem as ComboBoxItem)?.Tag as string ?? productToEdit.Type;

                if (string.IsNullOrEmpty(name))
                {
                    MessageBox.Show("O nome do produto é obrigatório.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrEmpty(catId))
                {
                    MessageBox.Show("Selecione uma categoria.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!TryParseDouble(txtPrecoVenda.Text, out double priceSaleVal) || priceSaleVal < 0 ||
                    !TryParseDouble(txtPrecoCusto.Text, out double priceCostVal) || priceCostVal < 0 ||
                    !TryParseDouble(txtMinStock.Text, out double minStockVal) || minStockVal < 0)
                {
                    MessageBox.Show("Preencha valores numéricos válidos e não negativos.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int priceSaleCents = (int)Math.Round(priceSaleVal * 100);
                int priceCostCents = (int)Math.Round(priceCostVal * 100);

                confirmBtn.IsEnabled = false; confirmBtn.Content = "Salvando...";
                try
                {
                    var payload = new
                    {
                        p_email = _donoEmail,
                        p_password = _donoPassword,
                        p_product_id = productToEdit.Id,
                        p_nome = name,
                        p_tipo = tipo,
                        p_unidade = unidade,
                        p_preco_venda = priceSaleCents,
                        p_preco_custo = priceCostCents,
                        p_categoria_id = catId,
                        p_codigo_barras = string.IsNullOrEmpty(barcode) ? null : barcode
                    };
                    using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/atualizar_produto");
                    req.Content = new System.Net.Http.StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                    req.Headers.Add("apikey", anon);
                    req.Headers.Add("Authorization", "Bearer " + anon);
                    var resp = await _redeHttp.SendAsync(req);
                    var j = Newtonsoft.Json.Linq.JObject.Parse(await resp.Content.ReadAsStringAsync());
                    if (j["error"] != null)
                    {
                        string err = j["error"]!.ToString();
                        MessageBox.Show(err == "invalid_credentials" ? "Sessão do dono expirada, faça login online de novo." :
                            err == "categoria_invalida" ? "Categoria inválida." :
                            err == "not_found" ? "Produto não encontrado na nuvem." :
                            err == "nome_obrigatorio" ? "Informe o nome do produto." : err,
                            "Editar produto", MessageBoxButton.OK, MessageBoxImage.Warning);
                        confirmBtn.IsEnabled = true; confirmBtn.Content = "Salvar Alterações";
                        return;
                    }

                    // Cloud confirmou: aplica localmente de imediato (mesmo padrão do Delete) —
                    // não depende do próximo ciclo de sync para refletir na tela.
                    var conn = App.Database.GetConnection();
                    productToEdit.Name = name;
                    productToEdit.Barcode = string.IsNullOrEmpty(barcode) ? null : barcode;
                    productToEdit.CategoryId = catId;
                    productToEdit.Type = tipo;
                    productToEdit.UnitMeasure = unidade;
                    productToEdit.PriceSale = priceSaleCents;
                    productToEdit.PriceCost = priceCostCents;
                    productToEdit.MinStock = minStockVal;
                    productToEdit.UpdatedAt = DateTime.Now;
                    await conn.UpdateAsync(productToEdit);

                    formWindow.Close();
                    MessageBox.Show("Produto atualizado com sucesso!", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);

                    if (string.IsNullOrEmpty(_stockRemoteStoreId)) LoadStock(SearchStockBox.Text.Trim());
                    else _ = LoadRemoteStock(_stockRemoteStoreId);

                    _ = Task.Run(async () => await RunSincronizacaoSilenciosa());
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Sem conexão com a nuvem para salvar as alterações.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    System.Diagnostics.Debug.WriteLine($"[atualizar_produto Error]: {ex.Message}");
                    confirmBtn.IsEnabled = true; confirmBtn.Content = "Salvar Alterações";
                }
            };

            txtNome.Focus();
            formWindow.ShowDialog();
        }

        private async void DeleteProductButton_Click(object sender, RoutedEventArgs e)
        {
            if (StockRemoteBlocked()) return;
            if (string.IsNullOrEmpty(_donoEmail) || string.IsNullOrEmpty(_donoPassword))
            {
                MessageBox.Show("Para excluir/inativar um produto na rede, faça login ONLINE como dono.",
                    "Excluir produto", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (sender is Button btn && btn.Tag is string productId)
            {
                try
                {
                    var connection = App.Database.GetConnection();
                    var product = await connection.FindAsync<Product>(productId);
                    if (product == null) return;

                    if (product.Type == "PAO_FRANCES")
                    {
                        MessageBox.Show("O produto principal Pão não pode ser excluído por regras de segurança do caixa.", "Erro", MessageBoxButton.OK, MessageBoxImage.Stop);
                        return;
                    }

                    if (MessageBox.Show($"Deseja realmente excluir/inativar o produto '{product.Name}'? Ele não estará mais disponível para venda.", "Confirmação de Exclusão", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        string baseUrl = EnvService.Get("SUPABASE_URL").TrimEnd('/');
                        string anon = EnvService.Get("SUPABASE_ANON_KEY");
                        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(anon))
                        {
                            MessageBox.Show("Configuração da nuvem ausente no .env.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }

                        // 1. Inativa o produto na nuvem Supabase via RPC excluir_produto
                        var payload = new
                        {
                            p_email = _donoEmail,
                            p_password = _donoPassword,
                            p_product_id = productId
                        };
                        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/excluir_produto");
                        req.Content = new System.Net.Http.StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                        req.Headers.Add("apikey", anon);
                        req.Headers.Add("Authorization", "Bearer " + anon);

                        var resp = await _redeHttp.SendAsync(req);
                        if (!resp.IsSuccessStatusCode)
                        {
                            MessageBox.Show($"Sem conexão com a nuvem para excluir o produto ({(int)resp.StatusCode}).", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }

                        var j = Newtonsoft.Json.Linq.JObject.Parse(await resp.Content.ReadAsStringAsync());
                        if (j["error"] != null)
                        {
                            string err = j["error"]!.ToString();
                            MessageBox.Show(err == "invalid_credentials" ? "Sessão do dono expirada, faça login online de novo." :
                                err == "forbidden" ? "Sem permissão para inativar produtos." :
                                err == "not_found" ? "Produto não encontrado na nuvem." : err,
                                "Excluir produto", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        // 2. Se inativou com sucesso na nuvem, aplica localmente no SQLite
                        product.Active = false;
                        product.UpdatedAt = DateTime.Now;

                        await connection.UpdateAsync(product);
                        MessageBox.Show("Produto inativado com sucesso!", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
                        
                        LoadStock(SearchStockBox.Text.Trim());

                        _ = Task.Run(async () => await RunSincronizacaoSilenciosa());
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao excluir produto: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // ================= ABA 2: HISTÓRICO & CANCELAMENTO LOGIC =================

        private async void LoadSalesHistory()
        {
            try
            {
                var connection = App.Database.GetConnection();
                
                DateTime startDate = HistoryStartDatePicker.SelectedDate ?? DateTime.Today;
                DateTime endDate = HistoryEndDatePicker.SelectedDate ?? DateTime.Today;

                string startTimeStr = HistoryStartTimeTextBox.Text.Trim();
                string endTimeStr = HistoryEndTimeTextBox.Text.Trim();
                if (!TimeSpan.TryParse(startTimeStr, out TimeSpan startTime)) startTime = new TimeSpan(0, 0, 0);
                if (!TimeSpan.TryParse(endTimeStr, out TimeSpan endTime)) endTime = new TimeSpan(23, 59, 59);

                DateTime fullStart = startDate.Date.Add(startTime);
                DateTime fullEnd = endDate.Date.Add(endTime);

                var sales = await connection.Table<Sale>()
                    .Where(s => s.StoreId == StoreIdentityService.StoreId
                             && s.SaleDate >= fullStart && s.SaleDate <= fullEnd)
                    .ToListAsync();

                string paymentFilter = (HistoryPaymentFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "TODOS";
                if (paymentFilter != "TODOS")
                {
                    sales = sales.Where(s => s.PaymentMethod == paymentFilter).ToList();
                }

                sales = sales.OrderByDescending(s => s.SaleDate).ToList();

                var saleIds = sales.Select(s => s.Id).ToList();
                var saleItems = new List<SaleItem>();
                if (saleIds.Any())
                {
                    var allItems = await connection.Table<SaleItem>().ToListAsync();
                    saleItems = allItems.Where(i => saleIds.Contains(i.SaleId)).ToList();
                }

                var itemsGroup = saleItems.GroupBy(i => i.SaleId)
                    .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

                var views = sales.Select(s => new SalesHistoryView
                {
                    Id = s.Id,
                    SaleDate = s.SaleDate,
                    Total = s.Total,
                    PaymentMethod = s.PaymentMethod,
                    PaymentStatus = s.PaymentStatus,
                    ItemCount = itemsGroup.TryGetValue(s.Id, out var qty) ? qty : 0
                }).ToList();

                SalesHistoryList.ItemsSource = views;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao carregar histórico: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void HistoryFilter_Click(object sender, RoutedEventArgs e)
        {
            if (_historyRemoteStoreId == "TODAS") _ = LoadAllStoresHistory();
            else if (string.IsNullOrEmpty(_historyRemoteStoreId)) LoadSalesHistory();
            else _ = LoadRemoteHistory(_historyRemoteStoreId);
        }

        private void SalesHistoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (SalesHistoryList.SelectedItem is SalesHistoryView selectedSale)
            {
                // No modo "Todas as sedes" cada venda tem sua própria loja; usa a loja da venda
                // (não o "TODAS" do seletor) para o detalhe/cancelamento abrir na loja certa.
                string detailStore = _historyRemoteStoreId == "TODAS" ? selectedSale.StoreId : _historyRemoteStoreId;
                var win = new Views.SaleDetailsWindow(selectedSale.Id, detailStore, CurrentUser, _donoEmail, _donoPassword)
                {
                    Owner = this
                };
                win.ShowDialog();

                // Se a venda foi cancelada dentro da janela de detalhes, recarrega o histórico
                if (win.WasCanceled)
                {
                    if (_historyRemoteStoreId == "TODAS") _ = LoadAllStoresHistory();
                    else if (string.IsNullOrEmpty(_historyRemoteStoreId)) LoadSalesHistory();
                    else _ = LoadRemoteHistory(_historyRemoteStoreId);

                    _ = Task.Run(async () => await RunSincronizacaoSilenciosa());
                }

                // Desmarca a seleção para poder clicar de novo
                SalesHistoryList.SelectedItem = null;
            }
        }

        // ================= ABA 3: DASHBOARD LOGIC =================

        private async Task SetupDashStoreSelector()
        {
            if (CurrentUser.Role.ToUpper() != "DONO")
            {
                DashStorePanel.Visibility = Visibility.Collapsed;
                DashProfitCard.Visibility = Visibility.Collapsed;
                return;
            }
            DashStorePanel.Visibility = Visibility.Visible;
            // Mostra o card de Lucro Bruto apenas para o DONO
            DashProfitCard.Visibility = Visibility.Visible;
            DashProfitColumn.Width = new GridLength(1, GridUnitType.Star);
            if (_dashSelectorReady) return;

            var lojas = _lojasCache.Count > 0 ? _lojasCache : await FetchLojasAsync();
            _suppressStockStoreEvent = true;
            DashStoreSelector.Items.Clear();
            DashStoreSelector.Items.Add(new ComboBoxItem { Content = "Todas as sedes", Tag = "TODAS" });
            foreach (var l in lojas)
            {
                if (string.IsNullOrEmpty(l.StoreId)) continue;
                DashStoreSelector.Items.Add(new ComboBoxItem { Content = l.Nome, Tag = l.StoreId });
            }
            DashStoreSelector.SelectedIndex = 0;
            _suppressStockStoreEvent = false;
            if (lojas.Count > 0)
            {
                _dashSelectorReady = true;
            }
        }

        private async Task LoadRemoteDashboardRede(DateTime from, DateTime to)
        {
            try
            {
                string baseUrl = EnvService.Get("SUPABASE_URL").TrimEnd('/');
                string anon = EnvService.Get("SUPABASE_ANON_KEY");
                if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(anon)) return;

                var payload = new { p_email = _donoEmail, p_password = _donoPassword, p_from = from, p_to = to };
                var content = new System.Net.Http.StringContent(
                    Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/get_dashboard_rede");
                req.Content = content;
                req.Headers.Add("apikey", anon);
                req.Headers.Add("Authorization", "Bearer " + anon);

                var resp = await _redeHttp.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    MessageBox.Show($"Sem conexão com a nuvem ({(int)resp.StatusCode}).", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var j = Newtonsoft.Json.Linq.JObject.Parse(body);
                if (j["error"] != null)
                {
                    string err = j["error"]!.ToString();
                    MessageBox.Show(err == "invalid_credentials"
                        ? "Faça login ONLINE como dono para ver o painel consolidado."
                        : err, "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var rede = j["rede"];
                long totalBilling = 0;
                int approvedCount = 0;
                if (rede != null)
                {
                    totalBilling = (long?)rede["faturamento_centavos"] ?? 0;
                    approvedCount = (int?)rede["vendas_qtd"] ?? 0;
                }
                double avgTicket = approvedCount > 0 ? (double)totalBilling / approvedCount : 0;

                DashBillingText.Text = $"R$ {totalBilling / 100.0:F2}";
                DashSalesCountText.Text = $"{approvedCount} vendas";
                DashAvgTicketText.Text = $"R$ {avgTicket / 100.0:F2}";
                DashCanceledSalesText.Text = "--";

                // Lucro bruto (faturamento - custo) retornado pela RPC atualizada
                long totalCost = (long?)rede?["custo_centavos"] ?? 0;
                long profit = totalBilling - totalCost;
                DashProfitText.Text = $"R$ {profit / 100.0:F2}";

                DashCashAmountText.Text = "--";
                DashPixAmountText.Text = "--";
                DashCardAmountText.Text = "--";

                var topProducts = new List<DashboardTopProduct>();
                if (j["top_produtos"] is Newtonsoft.Json.Linq.JArray topArr)
                {
                    foreach (var t in topArr)
                    {
                        topProducts.Add(new DashboardTopProduct
                        {
                            ProductName = t["nome"]?.ToString() ?? "Desconhecido",
                            Quantity = (double?)t["quantidade"] ?? 0,
                            UnitMeasure = t["unidade"]?.ToString() ?? "UN",
                            TotalCents = (int?)t["total_centavos"] ?? 0
                        });
                    }
                }
                DashTopProductsList.ItemsSource = topProducts;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao carregar Dashboard consolidado: " + ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadRemoteDashboardLoja(string storeId, DateTime from, DateTime to)
        {
            try
            {
                string baseUrl = EnvService.Get("SUPABASE_URL").TrimEnd('/');
                string anon = EnvService.Get("SUPABASE_ANON_KEY");
                if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(anon)) return;

                var payload = new { p_email = _donoEmail, p_password = _donoPassword, p_store_id = storeId, p_from = from, p_to = to, p_payment = "TODOS" };
                var content = new System.Net.Http.StringContent(
                    Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/get_vendas_loja");
                req.Content = content;
                req.Headers.Add("apikey", anon);
                req.Headers.Add("Authorization", "Bearer " + anon);

                var resp = await _redeHttp.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    MessageBox.Show($"Sem conexão com a nuvem ({(int)resp.StatusCode}).", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var j = Newtonsoft.Json.Linq.JObject.Parse(body);
                if (j["error"] != null)
                {
                    string err = j["error"]!.ToString();
                    MessageBox.Show(err == "invalid_credentials"
                        ? "Faça login ONLINE como dono para ver o dashboard da loja."
                        : err, "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var salesList = new List<Sale>();
                foreach (var v in (Newtonsoft.Json.Linq.JArray)j["vendas"]!)
                {
                    salesList.Add(new Sale
                    {
                        Id = v["id"]?.ToString() ?? string.Empty,
                        SaleDate = (DateTime)v["data"]!,
                        Total = (int)(v["total_centavos"] ?? 0),
                        PaymentMethod = v["metodo"]?.ToString() ?? string.Empty,
                        PaymentStatus = v["status"]?.ToString() ?? string.Empty
                    });
                }

                string paymentFilter = (DashPaymentFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "TODOS";
                if (paymentFilter != "TODOS")
                {
                    salesList = salesList.Where(s => s.PaymentMethod == paymentFilter).ToList();
                }

                var approvedSales = salesList.Where(s => s.PaymentStatus == "APROVADO").ToList();
                var canceledSalesCount = salesList.Count(s => s.PaymentStatus == "CANCELADO");

                int totalBilling = approvedSales.Sum(s => s.Total);
                int approvedCount = approvedSales.Count;
                double avgTicket = approvedCount > 0 ? (double)totalBilling / approvedCount : 0;

                DashBillingText.Text = $"R$ {totalBilling / 100.0:F2}";
                DashSalesCountText.Text = $"{approvedCount} vendas";
                DashAvgTicketText.Text = $"R$ {avgTicket / 100.0:F2}";
                DashCanceledSalesText.Text = $"{canceledSalesCount} cupons";

                int cashTotal = approvedSales.Where(s => s.PaymentMethod == "DINHEIRO").Sum(s => s.Total);
                int pixTotal = approvedSales.Where(s => s.PaymentMethod == "PIX").Sum(s => s.Total);
                int cardTotal = approvedSales.Where(s => s.PaymentMethod == "CARTAO_CREDITO" || s.PaymentMethod == "CARTAO_DEBITO").Sum(s => s.Total);

                DashCashAmountText.Text = $"R$ {cashTotal / 100.0:F2}";
                DashPixAmountText.Text = $"R$ {pixTotal / 100.0:F2}";
                DashCardAmountText.Text = $"R$ {cardTotal / 100.0:F2}";

                // Lucro bruto por loja: soma (priceCost * quantity) dos itens vendidos
                // Não temos os itens individuais nessa RPC, mas a RPC atualizada retorna custo por loja nas 'lojas'
                // Aqui pegamos do JSON da loja selecionada; para simplificar, buscamos da lista de lojas do JSON principal
                // Porém esta branch recebe da RPC get_vendas_loja que não retorna custo.
                // Vamos calcular custo cruzando Product.PriceCost localmente quando possível.
                DashProfitText.Text = "--";

                // Não é possível popular produtos na loja remota sem puxar todos os itens da nuvem
                DashTopProductsList.ItemsSource = null;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao carregar Dashboard da loja remota: " + ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void LoadDashboard()
        {
            try
            {
                DateTime startDate = DashStartDatePicker.SelectedDate ?? DateTime.Today;
                DateTime endDate = DashEndDatePicker.SelectedDate ?? DateTime.Today;

                string startTimeStr = DashStartTimeTextBox.Text.Trim();
                string endTimeStr = DashEndTimeTextBox.Text.Trim();
                if (!TimeSpan.TryParse(startTimeStr, out TimeSpan startTime)) startTime = new TimeSpan(0, 0, 0);
                if (!TimeSpan.TryParse(endTimeStr, out TimeSpan endTime)) endTime = new TimeSpan(23, 59, 59);

                DateTime fullStart = startDate.Date.Add(startTime);
                DateTime fullEnd = endDate.Date.Add(endTime);

                // Se o dono filtrou por loja na nuvem
                if (CurrentUser.Role.ToUpper() == "DONO" && DashStoreSelector.SelectedItem != null)
                {
                    string selectedStore = (DashStoreSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "TODAS";
                    if (selectedStore == "TODAS")
                    {
                        await LoadRemoteDashboardRede(fullStart, fullEnd);
                        return;
                    }
                    else if (!string.IsNullOrEmpty(selectedStore))
                    {
                        await LoadRemoteDashboardLoja(selectedStore, fullStart, fullEnd);
                        return;
                    }
                }

                // Lógica de carregamento local (caixa local offline-first)
                var connection = App.Database.GetConnection();
                var sales = await connection.Table<Sale>()
                    .Where(s => s.StoreId == StoreIdentityService.StoreId
                             && s.SaleDate >= fullStart && s.SaleDate <= fullEnd)
                    .ToListAsync();

                string paymentFilter = (DashPaymentFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "TODOS";
                if (paymentFilter != "TODOS")
                {
                    sales = sales.Where(s => s.PaymentMethod == paymentFilter).ToList();
                }

                var approvedSales = sales.Where(s => s.PaymentStatus == "APROVADO").ToList();
                var canceledSalesCount = sales.Count(s => s.PaymentStatus == "CANCELADO");

                // Métricas Gerais
                int totalBilling = approvedSales.Sum(s => s.Total);
                int approvedCount = approvedSales.Count;
                double avgTicket = approvedCount > 0 ? (double)totalBilling / approvedCount : 0;

                DashBillingText.Text = $"R$ {totalBilling / 100.0:F2}";
                DashSalesCountText.Text = $"{approvedCount} vendas";
                DashAvgTicketText.Text = $"R$ {avgTicket / 100.0:F2}";
                DashCanceledSalesText.Text = $"{canceledSalesCount} cupons";

                // Métricas por Método de Pagamento
                int cashTotal = approvedSales.Where(s => s.PaymentMethod == "DINHEIRO").Sum(s => s.Total);
                int pixTotal = approvedSales.Where(s => s.PaymentMethod == "PIX").Sum(s => s.Total);
                int cardTotal = approvedSales.Where(s => s.PaymentMethod == "CARTAO_CREDITO" || s.PaymentMethod == "CARTAO_DEBITO").Sum(s => s.Total);

                DashCashAmountText.Text = $"R$ {cashTotal / 100.0:F2}";
                DashPixAmountText.Text = $"R$ {pixTotal / 100.0:F2}";
                DashCardAmountText.Text = $"R$ {cardTotal / 100.0:F2}";

                // TOP 5 Produtos Mais Vendidos
                var saleIds = approvedSales.Select(s => s.Id).ToList();
                var allItems = await connection.Table<SaleItem>().ToListAsync();
                var todayItems = allItems.Where(i => saleIds.Contains(i.SaleId)).ToList();
                var productsList = await connection.Table<Product>().ToListAsync();
                var productMap = productsList.ToDictionary(p => p.Id, p => p);

                var topProducts = todayItems
                    .GroupBy(i => i.ProductId)
                    .Select(g => {
                        var prod = productMap.TryGetValue(g.Key, out var p) ? p : null;
                        return new DashboardTopProduct
                        {
                            ProductName = prod?.Name ?? "Produto Desconhecido",
                            Quantity = g.Sum(i => i.Quantity),
                            UnitMeasure = prod?.UnitMeasure ?? "UN",
                            TotalCents = g.Sum(i => i.Subtotal)
                        };
                    })
                    .OrderByDescending(tp => tp.TotalCents)
                    .Take(5)
                    .ToList();

                DashTopProductsList.ItemsSource = topProducts;

                // Lucro bruto local: faturamento - custo (PriceCost × Quantity por item)
                if (CurrentUser.Role.ToUpper() == "DONO")
                {
                    long totalCost = 0;
                    foreach (var item in todayItems)
                    {
                        if (productMap.TryGetValue(item.ProductId, out var p))
                            totalCost += (long)(p.PriceCost * item.Quantity);
                    }
                    long profit = totalBilling - totalCost;
                    DashProfitText.Text = $"R$ {profit / 100.0:F2}";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao processar estatísticas do Dashboard: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DashFilter_Click(object sender, RoutedEventArgs e)
        {
            LoadDashboard();
        }

        // ================= CONECTIVIDADE E SINCRONIZAÇÃO =================

        private async void SyncButton_Click(object sender, RoutedEventArgs e)
        {
            SyncButton.IsEnabled = false;
            SyncButton.Content = "Sincronizando...";
            SyncStatusText.Text = "Sincronizando dados...";
            SyncStatusIndicator.Fill = System.Windows.Media.Brushes.Orange;

            var (success, error) = await SincronizarDadosNuvem();

            if (success)
            {
                SyncStatusText.Text = "Sincronizado";
                SyncStatusIndicator.Fill = System.Windows.Media.Brushes.Green;
            }
            else
            {
                SyncStatusText.Text = "Sem conexão / Falha";
                SyncStatusIndicator.Fill = System.Windows.Media.Brushes.Red;
                
                // Exibe erro detalhado para ajudar no debug
                MessageBox.Show($"Falha na sincronização:\n{error}", "Erro de Sincronização", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            SyncButton.IsEnabled = true;
            SyncButton.Content = "Sincronizar Agora";

            // Recarrega aba aberta (respeitando o filtro de loja do dono)
            if (MainTabControl.SelectedIndex == 1)
            {
                if (string.IsNullOrEmpty(_stockRemoteStoreId)) LoadStock(SearchStockBox.Text.Trim());
                else _ = LoadRemoteStock(_stockRemoteStoreId);
            }
            else if (MainTabControl.SelectedIndex == 2)
            {
                if (_historyRemoteStoreId == "TODAS") _ = LoadAllStoresHistory();
                else if (string.IsNullOrEmpty(_historyRemoteStoreId)) LoadSalesHistory();
                else _ = LoadRemoteHistory(_historyRemoteStoreId);
            }
            else if (MainTabControl.SelectedIndex == 3) LoadDashboard();
        }

        private async Task RunSincronizacaoSilenciosa()
        {
            await SincronizarDadosNuvem();
        }

        private async Task<(bool Success, string Error)> SincronizarDadosNuvem(bool aguardarCiclo = false)
        {
            if (_encerrando) return (true, string.Empty);

            // Trava anti-sobreposição ATÔMICA: timer de 60s, botão manual e o sync disparado após
            // venda/cancelamento podem vir de threads diferentes. WaitAsync(0) não bloqueia — se já
            // houver um sync em curso, sai sem erro (evita dois ciclos simultâneos corromperem o SQLite).
            if (aguardarCiclo)
                await _syncGate.WaitAsync();
            else if (!await _syncGate.WaitAsync(0))
                return (true, string.Empty);
            try
            {
                if (_encerrando) return (true, string.Empty);

                using var syncService = new SyncService(App.Database.GetSyncConnection(), _escopo.Sessao);

                string tenantId = CurrentUser.TenantId;
                string storeId = StoreIdentityService.Atual(CurrentUser.StoreId);

                bool pushSuccess = await syncService.PushSalesAsync(tenantId, storeId);
                bool pullSuccess = pushSuccess
                    && await syncService.PullUpdatesAsync(tenantId, storeId);
                bool stockSuccess = pullSuccess
                    && await syncService.PushStockSnapshotAsync(tenantId, storeId);

                var pendingCount = await App.Database.GetConnection().Table<Sale>()
                    .Where(s => !s.IsSynced && s.StoreId == storeId)
                    .CountAsync();

                Dispatcher.Invoke(() => {
                    if (pendingCount > 0)
                    {
                        SyncStatusText.Text = $"{pendingCount} venda(s) pendente(s)";
                        SyncStatusIndicator.Fill = System.Windows.Media.Brushes.Yellow;
                    }
                    else if (pushSuccess && pullSuccess && stockSuccess)
                    {
                        SyncStatusText.Text = "Sincronizado";
                        SyncStatusIndicator.Fill = System.Windows.Media.Brushes.Green;
                    }
                    else
                    {
                        SyncStatusText.Text = "Falha na sincronizacao";
                        SyncStatusIndicator.Fill = System.Windows.Media.Brushes.Red;
                    }
                });

                if (!pushSuccess || !pullSuccess || !stockSuccess)
                {
                    return (false, syncService.LastError);
                }

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Sincronizacao Silenciosa Error]: {ex.Message}");
                return (false, ex.Message);
            }
            finally
            {
                _syncGate.Release();
            }
        }

        // ================= ABA 5: PAINEL DA REDE (DONO) =================

        public class RedeLojaView
        {
            public string StoreId { get; set; } = string.Empty;
            public string Nome { get; set; } = string.Empty;
            public string Resumo { get; set; } = string.Empty;
            public string FatString { get; set; } = string.Empty;
            public string AlertaText { get; set; } = string.Empty;
            public Visibility TemAlerta { get; set; } = Visibility.Collapsed;
        }

        public class RedeTopView
        {
            public string Nome { get; set; } = string.Empty;
            public string QtdString { get; set; } = string.Empty;
            public string FatString { get; set; } = string.Empty;
        }

        private void RedePeriodo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string s && int.TryParse(s, out int d))
            {
                _redePeriodoDias = d;
                HighlightRedePeriodo(b);
                _ = LoadRede();
            }
        }

        private void RedeRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadRede();

        private void HighlightRedePeriodo(Button active)
        {
            foreach (var b in new[] { RedeHojeBtn, Rede7Btn, Rede30Btn })
            {
                b.Background = AppColors.Surface;
                b.Foreground = AppColors.TextMuted;
            }
            active.Background = AppColors.Accent;
            active.Foreground = System.Windows.Media.Brushes.Black;
        }

        // Busca o painel consolidado das lojas na nuvem (mesma RPC do painel web).
        private async Task LoadRede()
        {
            RedeStatus.Visibility = Visibility.Collapsed;
            try
            {
                var (from, to) = GetRedePeriodo();
                string baseUrl = EnvService.Get("SUPABASE_URL").TrimEnd('/');
                string anon = EnvService.Get("SUPABASE_ANON_KEY");
                if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(anon))
                {
                    ShowRedeError("Configuração da nuvem ausente no .env.");
                    return;
                }

                var payload = new { p_email = _donoEmail, p_password = _donoPassword, p_from = from, p_to = to };
                var content = new System.Net.Http.StringContent(
                    Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/get_dashboard_rede");
                req.Content = content;
                req.Headers.Add("apikey", anon);
                req.Headers.Add("Authorization", "Bearer " + anon);

                var resp = await _redeHttp.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    ShowRedeError($"Sem conexão com a nuvem ({(int)resp.StatusCode}).");
                    return;
                }

                var j = Newtonsoft.Json.Linq.JObject.Parse(body);
                if (j["error"] != null)
                {
                    string err = j["error"]!.ToString();
                    ShowRedeError(err == "invalid_credentials"
                        ? "Para ver o painel da rede, faça login ONLINE como dono (e-mail/senha da nuvem)."
                        : err == "forbidden" ? "Seu usuário não tem acesso ao painel da rede." : err);
                    return;
                }

                var rede = j["rede"];
                long totalBilling = 0;
                int approvedCount = 0;
                int totalStores = 0;
                if (rede != null)
                {
                    totalBilling = (long?)rede["faturamento_centavos"] ?? 0;
                    approvedCount = (int?)rede["vendas_qtd"] ?? 0;
                    totalStores = (int?)rede["lojas_total"] ?? 0;
                }
                RedeFat.Text = FormatBRL(totalBilling);
                RedeVendas.Text = approvedCount.ToString();
                RedeLojas.Text = totalStores.ToString();

                var lojas = new List<RedeLojaView>();
                if (j["lojas"] is Newtonsoft.Json.Linq.JArray lojasArr)
                {
                    foreach (var l in lojasArr)
                    {
                        int baixo = (int?)l["estoque_baixo"] ?? 0;
                        lojas.Add(new RedeLojaView
                        {
                            StoreId = l["storeId"]?.ToString() ?? string.Empty,
                            Nome = l["nome"]?.ToString() ?? "Loja",
                            Resumo = $"{(l["vendas_qtd"] ?? 0)} venda(s) · {(l["estoque_produtos"] ?? 0)} produtos",
                            FatString = FormatBRL((long?)l["faturamento_centavos"] ?? 0),
                            AlertaText = baixo > 0 ? $"{baixo} em falta" : string.Empty,
                            TemAlerta = baixo > 0 ? Visibility.Visible : Visibility.Collapsed
                        });
                    }
                }
                RedeLojasList.ItemsSource = lojas;
                _lojasCache = lojas;
                BuildLojaTabs(lojas);

                var tops = new List<RedeTopView>();
                if (j["top_produtos"] is Newtonsoft.Json.Linq.JArray topsArr)
                {
                    foreach (var t in topsArr)
                    {
                        tops.Add(new RedeTopView
                        {
                            Nome = t["nome"]?.ToString() ?? "Desconhecido",
                            QtdString = $"×{(t["qtd"] ?? 0)}",
                            FatString = FormatBRL((long?)t["faturamento_centavos"] ?? 0)
                        });
                    }
                }
                RedeTopList.ItemsSource = tops;
            }
            catch (Exception ex)
            {
                ShowRedeError("Sem conexão com a nuvem.");
                System.Diagnostics.Debug.WriteLine($"[LoadRede Error]: {ex.Message}");
            }
        }

        private void ShowRedeError(string msg)
        {
            RedeStatus.Text = msg;
            RedeStatus.Visibility = Visibility.Visible;
            RedeLojasList.ItemsSource = null;
            RedeTopList.ItemsSource = null;
            RedeFat.Text = "—";
            RedeVendas.Text = "—";
            RedeLojas.Text = "—";
        }

        private static string FormatBRL(long centavos)
            => (centavos / 100.0).ToString("C2", new System.Globalization.CultureInfo("pt-BR"));

        // ---- Detalhe por loja (abas dinâmicas no painel do dono) ----

        private (string from, string to) GetRedePeriodo()
        {
            var now = DateTime.Now;
            DateTime de = _redePeriodoDias == 0 ? now.Date : now.Date.AddDays(-_redePeriodoDias);
            DateTime ate = now.Date.AddDays(1).AddSeconds(-1);
            return (de.ToString("yyyy-MM-ddTHH:mm:ss"), ate.ToString("yyyy-MM-ddTHH:mm:ss"));
        }

        private static string FmtQtd(double q)
        {
            if (Math.Abs(q - Math.Round(q)) < 0.001) return ((long)Math.Round(q)).ToString();
            return q.ToString("0.###", new System.Globalization.CultureInfo("pt-BR"));
        }

        // Reconstrói as sub-abas por loja, mantendo a aba "Geral" (índice 0).
        private void BuildLojaTabs(List<RedeLojaView> lojas)
        {
            string? selecionada = (RedeSubTabs.SelectedItem as TabItem)?.Tag as string;

            for (int i = RedeSubTabs.Items.Count - 1; i >= 1; i--)
                RedeSubTabs.Items.RemoveAt(i);

            var template = (DataTemplate)RedeSubTabs.FindResource("LojaDetalheTemplate");
            foreach (var loja in lojas)
            {
                if (string.IsNullOrEmpty(loja.StoreId)) continue;
                var vm = new LojaDetalheVM { StoreId = loja.StoreId, Nome = loja.Nome };
                var ti = new TabItem
                {
                    Header = loja.Nome,
                    Tag = loja.StoreId,
                    Content = vm,
                    ContentTemplate = template
                };
                RedeSubTabs.Items.Add(ti);
                if (loja.StoreId == selecionada)
                    RedeSubTabs.SelectedItem = ti; // dispara SelectionChanged -> carrega detalhe
            }
        }

        private void RedeSubTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, RedeSubTabs)) return;
            if (RedeSubTabs.SelectedItem is TabItem ti && ti.Content is LojaDetalheVM vm && !vm.Loaded)
                _ = LoadLojaDetalhe(vm);
        }

        private async Task LoadLojaDetalhe(LojaDetalheVM vm)
        {
            vm.StatusVisible = Visibility.Collapsed;
            try
            {
                var (from, to) = GetRedePeriodo();
                string baseUrl = EnvService.Get("SUPABASE_URL").TrimEnd('/');
                string anon = EnvService.Get("SUPABASE_ANON_KEY");
                if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(anon))
                {
                    vm.ShowError("Configuração da nuvem ausente no .env.");
                    return;
                }

                var payload = new
                {
                    p_email = _donoEmail,
                    p_password = _donoPassword,
                    p_store_id = vm.StoreId,
                    p_from = from,
                    p_to = to
                };
                var content = new System.Net.Http.StringContent(
                    Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, baseUrl + "/rest/v1/rpc/get_loja_detalhe");
                req.Content = content;
                req.Headers.Add("apikey", anon);
                req.Headers.Add("Authorization", "Bearer " + anon);

                var resp = await _redeHttp.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    vm.ShowError($"Sem conexão com a nuvem ({(int)resp.StatusCode}).");
                    return;
                }

                var j = Newtonsoft.Json.Linq.JObject.Parse(body);
                if (j["error"] != null)
                {
                    string err = j["error"]!.ToString();
                    vm.ShowError(err == "invalid_credentials"
                        ? "Faça login ONLINE como dono para ver o detalhe da loja."
                        : err == "forbidden" ? "Seu usuário não tem acesso ao painel da rede." : err);
                    return;
                }

                var resumo = j["resumo"]!;
                vm.PeriodoText = _redePeriodoDias == 0 ? "Hoje" : $"Últimos {_redePeriodoDias} dias";
                vm.FatString = FormatBRL((long)resumo["faturamento_centavos"]!);
                vm.VendasString = resumo["vendas_qtd"]!.ToString();
                long cancQtd = (long)resumo["cancelados_qtd"]!;
                vm.CanceladosString = cancQtd == 0 ? "0"
                    : $"{cancQtd} · {FormatBRL((long)resumo["cancelados_centavos"]!)}";
                vm.EstoqueString = $"{resumo["estoque_produtos"]} / {resumo["estoque_baixo"]}";

                var estoque = new List<EstoqueItemView>();
                var estoqueBaixo = new List<EstoqueItemView>();
                foreach (var p in (Newtonsoft.Json.Linq.JArray)j["estoque"]!)
                {
                    double q = (double)p["quantidade"]!;
                    double m = (double)p["minimo"]!;
                    bool baixo = (bool)p["baixo"]!;
                    var item = new EstoqueItemView
                    {
                        Nome = p["nome"]!.ToString(),
                        QtdMinString = $"{FmtQtd(q)} / mín {FmtQtd(m)}",
                        TemAlerta = baixo ? Visibility.Visible : Visibility.Collapsed
                    };
                    estoque.Add(item);
                    if (baixo) estoqueBaixo.Add(item);
                }

                var cancelamentos = new List<CancelItemView>();
                foreach (var c in (Newtonsoft.Json.Linq.JArray)j["cancelamentos"]!)
                {
                    DateTime data = (DateTime)c["data"]!;
                    cancelamentos.Add(new CancelItemView
                    {
                        DataString = data.ToString("dd/MM/yyyy HH:mm"),
                        Detalhe = $"{c["metodo"]} · {c["usuario"]}",
                        ValorString = "− " + FormatBRL((long)c["total_centavos"]!)
                    });
                }

                vm.Estoque = estoque;
                vm.EstoqueBaixo = estoqueBaixo;
                vm.Cancelamentos = cancelamentos;
                vm.TemAlerta = estoqueBaixo.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                vm.SemCancelamentos = cancelamentos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                vm.Loaded = true; // marca carregado só após sucesso
            }
            catch (Exception ex)
            {
                vm.ShowError("Sem conexão com a nuvem.");
                System.Diagnostics.Debug.WriteLine($"[LoadLojaDetalhe Error]: {ex.Message}");
            }
        }

        public class EstoqueItemView
        {
            public string Nome { get; set; } = string.Empty;
            public string QtdMinString { get; set; } = string.Empty;
            public Visibility TemAlerta { get; set; } = Visibility.Collapsed;
        }

        public class CancelItemView
        {
            public string DataString { get; set; } = string.Empty;
            public string Detalhe { get; set; } = string.Empty;
            public string ValorString { get; set; } = string.Empty;
        }

        public class LojaDetalheVM : System.ComponentModel.INotifyPropertyChanged
        {
            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            private void On([System.Runtime.CompilerServices.CallerMemberName] string? n = null)
                => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

            public string StoreId { get; set; } = string.Empty;
            public bool Loaded { get; set; }

            private string _nome = string.Empty;
            public string Nome { get => _nome; set { _nome = value; On(); } }

            private string _periodoText = "Carregando…";
            public string PeriodoText { get => _periodoText; set { _periodoText = value; On(); } }

            private string _statusText = string.Empty;
            public string StatusText { get => _statusText; set { _statusText = value; On(); } }

            private Visibility _statusVisible = Visibility.Collapsed;
            public Visibility StatusVisible { get => _statusVisible; set { _statusVisible = value; On(); } }

            private string _fat = "—";
            public string FatString { get => _fat; set { _fat = value; On(); } }

            private string _vendas = "—";
            public string VendasString { get => _vendas; set { _vendas = value; On(); } }

            private string _cancelados = "—";
            public string CanceladosString { get => _cancelados; set { _cancelados = value; On(); } }

            private string _estoque = "—";
            public string EstoqueString { get => _estoque; set { _estoque = value; On(); } }

            private Visibility _temAlerta = Visibility.Collapsed;
            public Visibility TemAlerta { get => _temAlerta; set { _temAlerta = value; On(); } }

            private Visibility _semCanc = Visibility.Collapsed;
            public Visibility SemCancelamentos { get => _semCanc; set { _semCanc = value; On(); } }

            private List<EstoqueItemView> _estoqueList = new();
            public List<EstoqueItemView> Estoque { get => _estoqueList; set { _estoqueList = value; On(); } }

            private List<EstoqueItemView> _baixoList = new();
            public List<EstoqueItemView> EstoqueBaixo { get => _baixoList; set { _baixoList = value; On(); } }

            private List<CancelItemView> _cancList = new();
            public List<CancelItemView> Cancelamentos { get => _cancList; set { _cancList = value; On(); } }

            public void ShowError(string msg)
            {
                StatusText = msg;
                StatusVisible = Visibility.Visible;
            }
        }
    }
}
