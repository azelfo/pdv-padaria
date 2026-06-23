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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using PdvPadaria.Models;
using PdvPadaria.Services;
using PdvPadaria.Views;

namespace PdvPadaria
{
    public partial class MainWindow : Window
    {
        public User CurrentUser { get; private set; }
        private readonly ObservableCollection<CartItemView> _cartItems = new ObservableCollection<CartItemView>();
        private string _selectedPaymentMethod = string.Empty;
        private int _subtotalCentavos = 0;
        private int _discountCentavos = 0;
        private int _totalCentavos = 0;
        private DispatcherTimer _syncTimer = null!;
        private readonly System.Threading.SemaphoreSlim _syncGate = new System.Threading.SemaphoreSlim(1, 1);
        private string _donoEmail = string.Empty;
        private string _donoPassword = string.Empty;
        private int _redePeriodoDias = 0;
        private static readonly System.Net.Http.HttpClient _redeHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private string? _activeSaleId;
        private System.Threading.CancellationTokenSource? _pixCts;

        public enum PdvState
        {
            Consultation,
            ActiveSale,
            PaymentSelection,
            CashPayment
        }

        private PdvState _currentState = PdvState.Consultation;
        private DateTime _lastBarcodeEvent = DateTime.MinValue;
        private readonly System.Text.StringBuilder _barcodeAccumulator = new System.Text.StringBuilder();

        public MainWindow(User user, string loginEmail = "", string loginPassword = "")
        {
            InitializeComponent();
            CurrentUser = user;
            // Credenciais guardadas em memória só para o painel da rede (RPC exige login do dono).
            _donoEmail = loginEmail;
            _donoPassword = loginPassword;

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

            Loaded += (s, e) => SetPdvState(PdvState.Consultation);
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

            public string QuantityString => ProductType == "PAO_FRANCES" || Quantity % 1 == 0 
                ? $"{Quantity:F0} UN" 
                : $"{Quantity:F3} KG";

            public string PriceUnitString => $"R$ {PriceUnit / 100.0:F2}";
            public string SubtotalString => $"R$ {Subtotal / 100.0:F2}";

            public bool IsBolo => ProductType == "BOLO";
            public bool IsNotPao => ProductType != "PAO_FRANCES";
            public Visibility BoloVisibility => IsBolo ? Visibility.Visible : Visibility.Collapsed;
            public Visibility NormalQuantityVisibility => IsNotPao ? Visibility.Visible : Visibility.Collapsed;
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

            public string StatusAlerta => LocalStockQuantity <= 0 ? "CRÍTICO" : "ATENÇÃO";

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

            // Roda a primeira sincronização assim que abre
            await RunSincronizacaoSilenciosa();
        }

        // Navegação Lateral
        private void SidebarBtn_Click(object sender, RoutedEventArgs e)
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

                // Destaca o botão selecionado
                btn.Background = AppColors.Surface;
                btn.Foreground = AppColors.Accent;

                // Troca a aba do TabControl
                MainTabControl.SelectedIndex = index;

                // Carrega dados específicos da aba
                if (index == 1)
                {
                    LoadStock();
                }
                else if (index == 2)
                {
                    LoadSalesHistory();
                }
                else if (index == 3)
                {
                    LoadDashboard();
                }
                else if (index == 4)
                {
                    LoadLowStockAlerts();
                }
                else if (index == 5)
                {
                    _ = LoadRede();
                }
            }
        }

        // ================= ABA 0: FRENTE DE CAIXA (PDV) LOGIC =================

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            SearchPlaceholder.Visibility = Visibility.Collapsed;
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchPlaceholder.Visibility = Visibility.Visible;
            }
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ExecuteSearch();
            }
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
                var product = await connection.Table<Product>()
                    .Where(p => p.Barcode == query && p.Active)
                    .FirstOrDefaultAsync();

                if (product == null)
                {
                    var matches = await connection.Table<Product>()
                        .Where(p => p.Name.Contains(query) && p.Active)
                        .ToListAsync();

                    if (matches.Any())
                    {
                        product = matches.First();
                    }
                }

                if (product != null)
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
                else
                {
                    MessageBox.Show("Produto não encontrado no catálogo local.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro na consulta: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AddProductToCart(Product product, double quantity)
        {
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
                var bread = await connection.Table<Product>()
                    .Where(p => p.Type == "PAO_FRANCES" && p.Active)
                    .FirstOrDefaultAsync();

                if (bread != null)
                {
                    PromptPaoFrances(bread);
                }
                else
                {
                    MessageBox.Show("Configuração de Pão Francês não encontrada.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao buscar pão: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                var breadConfig = await connection.Table<BreadConfig>().FirstOrDefaultAsync();
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
                Width = 380,
                Height = 310,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.ToolWindow,
                Background = AppColors.Surface,
                Foreground = AppColors.TextPrimary
            };

            var stack = new StackPanel { Margin = new Thickness(20) };
            
            var variationLabel = new TextBlock { Text = "Selecione a Variação do Pão:", Margin = new Thickness(0, 0, 0, 8), Foreground = AppColors.TextMuted, FontSize = 13, FontWeight = FontWeights.SemiBold };
            
            string selectedVariationCode = bread.Id == "prod-pao-massa-fina" ? "MASSA_FINA" : "CARIOCA";

            var btnCarioca = new Button 
            { 
                Content = "Pão Carioca", 
                Padding = new Thickness(10, 8, 10, 8),
                Background = new System.Windows.Media.SolidColorBrush(selectedVariationCode == "CARIOCA" ? AppColors.AccentColor : AppColors.BorderSoftColor),
                Foreground = selectedVariationCode == "CARIOCA" ? AppColors.BgBase : System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand
            };
            btnCarioca.Resources.Add(typeof(Border), new Style(typeof(Border)) { Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(6)) } });

            var btnMassaFina = new Button 
            { 
                Content = "Pão Massa Fina", 
                Padding = new Thickness(10, 8, 10, 8),
                Background = new System.Windows.Media.SolidColorBrush(selectedVariationCode == "MASSA_FINA" ? AppColors.AccentColor : AppColors.BorderSoftColor),
                Foreground = selectedVariationCode == "MASSA_FINA" ? AppColors.BgBase : System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand
            };
            btnMassaFina.Resources.Add(typeof(Border), new Style(typeof(Border)) { Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(6)) } });

            var buttonsGrid = new Grid { Margin = new Thickness(0, 0, 0, 15) };
            buttonsGrid.ColumnDefinitions.Add(new ColumnDefinition());
            buttonsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            buttonsGrid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(btnCarioca, 0);
            Grid.SetColumn(btnMassaFina, 2);
            buttonsGrid.Children.Add(btnCarioca);
            buttonsGrid.Children.Add(btnMassaFina);

            btnCarioca.Click += (s, ev) => {
                selectedVariationCode = "CARIOCA";
                btnCarioca.Background = AppColors.Accent;
                btnCarioca.Foreground = AppColors.BgBase;
                btnMassaFina.Background = AppColors.BorderSoft;
                btnMassaFina.Foreground = System.Windows.Media.Brushes.White;
            };

            btnMassaFina.Click += (s, ev) => {
                selectedVariationCode = "MASSA_FINA";
                btnMassaFina.Background = AppColors.Accent;
                btnMassaFina.Foreground = AppColors.BgBase;
                btnCarioca.Background = AppColors.BorderSoft;
                btnCarioca.Foreground = System.Windows.Media.Brushes.White;
            };

            var label = new TextBlock { Text = "Digite o valor em dinheiro do pão (R$):", Margin = new Thickness(0, 0, 0, 5), Foreground = AppColors.TextMuted };
            var textBox = new TextBox { Padding = new Thickness(8), FontSize = 16, Background = AppColors.BgBase, Foreground = System.Windows.Media.Brushes.White, BorderBrush = AppColors.BorderSoft };
            
            var previewLabel = new TextBlock 
            { 
                Text = "Quantidade: 0 pães", 
                Foreground = AppColors.Accent,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 10, 0, 5),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var button = new Button 
            { 
                Content = "Confirmar Lançamento", 
                Margin = new Thickness(0, 10, 0, 0), 
                Padding = new Thickness(10),
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

            button.Click += async (s, ev) => {
                if (TryParseDouble(textBox.Text, out double val) && val > 0)
                {
                    int valueCents = (int)Math.Round(val * 100);
                    int totalDePaes = CalcularQuantidadePaes(valueCents, brackets, priceUnit);

                    if (totalDePaes <= 0)
                    {
                        MessageBox.Show("Valor insuficiente.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    string targetId = selectedVariationCode == "CARIOCA" ? "prod-pao-carioca" : "prod-pao-massa-fina";
                    try
                    {
                        var conn = App.Database.GetConnection();
                        var targetProduct = await conn.Table<Product>().Where(p => p.Id == targetId).FirstOrDefaultAsync();
                        if (targetProduct != null)
                        {
                            AddBreadProductToCart(targetProduct, val, totalDePaes, selectedVariationCode);
                        }
                        else
                        {
                            AddBreadProductToCart(bread, val, totalDePaes, selectedVariationCode);
                        }
                    }
                    catch
                    {
                        AddBreadProductToCart(bread, val, totalDePaes, selectedVariationCode);
                    }
                    
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

                        if (item.Quantity + 1 > product.LocalStockQuantity)
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

            UpdateTroco();
        }

        private void ApplyDiscount_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string valStr && int.TryParse(valStr, out int discountVal))
            {
                _discountCentavos += discountVal;
                UpdateTotals();
            }
        }

        // Método PaymentMethod_Click removido (substituído por seleção automática de abas no TabControl)

        private void CashReceivedInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateTroco();
        }

        private void UpdateTroco()
        {
            if (TryParseDouble(CashReceivedInput.Text, out double receivedVal))
            {
                int receivedCentavos = (int)Math.Round(receivedVal * 100);
                int changeCentavos = receivedCentavos - _totalCentavos;

                if (changeCentavos >= 0)
                {
                    ChangeText.Text = $"R$ {changeCentavos / 100.0:F2}";
                }
                else
                {
                    ChangeText.Text = "R$ 0,00";
                }
            }
            else
            {
                ChangeText.Text = "R$ 0,00";
            }
        }

        private void ConfirmCashReceived_Click(object sender, RoutedEventArgs e)
        {
            FinishSaleFlow();
        }

        private void FinishSaleButton_Click(object sender, RoutedEventArgs e)
        {
            FinishSaleFlow();
        }

        private async void FinishSaleFlow()
        {
            if (!_cartItems.Any())
            {
                MessageBox.Show("Adicione pelo menos um item ao carrinho.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(_selectedPaymentMethod))
            {
                MessageBox.Show("Selecione um método de pagamento.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var connection = App.Database.GetConnection();
                var saleId = Guid.NewGuid().ToString();

                string tenantId = EnvService.Get("TENANT_ID", CurrentUser.TenantId);
                string storeId = EnvService.Get("STORE_ID", CurrentUser.StoreId);

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

                if (_selectedPaymentMethod == "DINHEIRO" && TryParseDouble(CashReceivedInput.Text, out double rec))
                {
                    sale.ReceivedAmount = (int)(rec * 100);
                    sale.ChangeAmount = sale.ReceivedAmount - _totalCentavos;
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
                            IsSynced = false
                        };

                        tx.Insert(saleItem);
                        tx.Insert(stockMovement);

                        var product = tx.Find<Product>(item.ProductId);
                        if (product != null)
                        {
                            product.LocalStockQuantity = Math.Max(0, product.LocalStockQuantity - item.Quantity);
                            tx.Update(product);
                        }
                    }
                });

                MessageBox.Show("Venda realizada com sucesso!", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);

                _cartItems.Clear();
                _discountCentavos = 0;
                _selectedPaymentMethod = string.Empty;
                CashReceivedInput.Text = string.Empty;
                ChangeText.Text = "R$ 0,00";

                // Reseta estado de pagamento do PIX
                _pixCts?.Cancel();
                _pixCts = null;
                _activeSaleId = null;

                UpdateTotals();
                SetPdvState(PdvState.Consultation);

                _ = Task.Run(async () => await RunSincronizacaoSilenciosa());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Falha ao salvar venda localmente: {ex.Message}", "Erro Fatal", MessageBoxButton.OK, MessageBoxImage.Error);
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
            else if (_currentState == PdvState.PaymentSelection || _currentState == PdvState.CashPayment)
            {
                if (e.Key == Key.F5)
                {
                    e.Handled = true;
                    PaymentTabControl.SelectedIndex = 0;
                    CashReceivedInput.Focus();
                }
                else if (e.Key == Key.F6)
                {
                    e.Handled = true;
                    PaymentTabControl.SelectedIndex = 1;
                }
                else if (e.Key == Key.F7)
                {
                    e.Handled = true;
                    PaymentTabControl.SelectedIndex = 2;
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    SetPdvState(PdvState.ActiveSale);
                }
                else if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    if (PaymentTabControl.SelectedIndex == 0) // Dinheiro
                    {
                        if (!string.IsNullOrEmpty(CashReceivedInput.Text))
                        {
                            FinishSaleFlow();
                        }
                    }
                    else // PIX ou Cartão
                    {
                        FinishSaleFlow();
                    }
                }
            }
        }

        private async void ProcessBarcodeScan(string barcode)
        {
            try
            {
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

        private void SetPdvState(PdvState state)
        {
            _currentState = state;
            
            if (GridConsulta == null || GridVenda == null || PanelPrecoVenda == null || PaymentTabControl == null)
                return;

            switch (state)
            {
                case PdvState.Consultation:
                    GridConsulta.Visibility = Visibility.Visible;
                    GridVenda.Visibility = Visibility.Collapsed;
                    PaymentTabControl.Visibility = Visibility.Collapsed;
                    
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
                    PaymentTabControl.Visibility = Visibility.Collapsed;
                    
                    SearchBox.Text = string.Empty;
                    SearchBox.Focus();
                    break;

                case PdvState.PaymentSelection:
                case PdvState.CashPayment:
                    _activeSaleId = Guid.NewGuid().ToString();
                    GridConsulta.Visibility = Visibility.Collapsed;
                    GridVenda.Visibility = Visibility.Visible;
                    PanelPrecoVenda.Visibility = Visibility.Collapsed;
                    PaymentTabControl.Visibility = Visibility.Visible;
                    
                    PaymentTabControl.SelectedIndex = 0;
                    _selectedPaymentMethod = "DINHEIRO";
                    CashReceivedInput.Text = string.Empty;
                    ChangeText.Text = "R$ 0,00";
                    CashReceivedInput.Focus();
                    break;
            }
        }

        private void StartSaleButton_Click(object sender, RoutedEventArgs e)
        {
            SetPdvState(PdvState.ActiveSale);
        }

        private void ConfirmSaleBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_cartItems.Any())
            {
                SetPdvState(PdvState.PaymentSelection);
            }
            else
            {
                MessageBox.Show("Adicione pelo menos um item ao carrinho.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CancelPaymentSelection_Click(object sender, RoutedEventArgs e)
        {
            SetPdvState(PdvState.ActiveSale);
        }

        private void PaymentTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != PaymentTabControl) return;

            if (PaymentTabControl.SelectedIndex == 0)
            {
                _selectedPaymentMethod = "DINHEIRO";
                _pixCts?.Cancel();
                _pixCts = null;
                CashReceivedInput.Focus();
            }
            else if (PaymentTabControl.SelectedIndex == 1)
            {
                _selectedPaymentMethod = "PIX";
                _ = IniciarFluxoPix();
            }
            else if (PaymentTabControl.SelectedIndex == 2)
            {
                _selectedPaymentMethod = "CARTAO_CREDITO";
                _pixCts?.Cancel();
                _pixCts = null;
            }
        }

        private async Task IniciarFluxoPix()
        {
            _pixCts?.Cancel();
            _pixCts = new System.Threading.CancellationTokenSource();
            var token = _pixCts.Token;

            // Limpa o estado visual do XAML
            PixQrCodeImage.Source = null;
            PixCopiaColaTextBox.Text = string.Empty;

            // Mostra o carregamento
            PanelPixLoading.Visibility = Visibility.Visible;
            PanelPixMain.Visibility = Visibility.Collapsed;

            try
            {
                // Cria cobrança real no Banco Inter PJ
                var cob = await InterPixService.CriarCobrançaPixAsync(_totalCentavos);

                if (token.IsCancellationRequested) return;

                string pixCopiaCola = cob.pixCopiaCola ?? "";
                string txid = cob.txid ?? "";

                if (string.IsNullOrEmpty(pixCopiaCola))
                {
                    throw new Exception("Resposta do Banco Inter não continha a string Pix Copia e Cola.");
                }

                // Renderiza QR Code
                RenderizarQrCode(pixCopiaCola);
                PixCopiaColaTextBox.Text = pixCopiaCola;

                // Troca visibilidade dos painéis
                PanelPixLoading.Visibility = Visibility.Collapsed;
                PanelPixMain.Visibility = Visibility.Visible;
                BorderPixOnlineStatus.Visibility = Visibility.Visible;
                TxtPixInstrucao.Text = "Aponte o app do seu banco para pagar o QR Code do PIX.";

                // Inicia o polling de verificação de pagamento de 2 em 2 segundos
                _ = IniciarPollingPix(txid, token);
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;

                System.Diagnostics.Debug.WriteLine($"[PIX Inter Error] {ex.Message}");

                // Avisa o lojista
                MessageBox.Show(
                    $"Não foi possível gerar a cobrança PIX no Banco Inter.\n\nDetalhes do Erro:\n{ex.Message}\n\nO caixa voltará à confirmação manual para esta transação.",
                    "Falha na API Banco Inter",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );

                // Em caso de erro, mostramos o layout mas permitimos apenas a confirmação manual (Enter)
                PanelPixLoading.Visibility = Visibility.Collapsed;
                PanelPixMain.Visibility = Visibility.Visible;
                BorderPixOnlineStatus.Visibility = Visibility.Collapsed;
                TxtPixInstrucao.Text = "⚠️ ERRO API: Confirme o PIX no app do banco e clique em CONFIRMAR PAGAMENTO MANUAL.";
            }
        }

        private void RenderizarQrCode(string text)
        {
            try
            {
                using (var qrGenerator = new QRCodeGenerator())
                {
                    var qrCodeData = qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
                    var qrCode = new PngByteQRCode(qrCodeData);
                    byte[] qrCodeBytes = qrCode.GetGraphic(20);

                    var bitmapImage = new BitmapImage();
                    using (var ms = new MemoryStream(qrCodeBytes))
                    {
                        bitmapImage.BeginInit();
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.StreamSource = ms;
                        bitmapImage.EndInit();
                    }
                    bitmapImage.Freeze();
                    PixQrCodeImage.Source = bitmapImage;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao renderizar o QR Code: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task IniciarPollingPix(string txid, System.Threading.CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, token);

                    string status = await InterPixService.ConsultarPixAsync(txid);

                    if (status.ToUpper() == "CONCLUIDA" || status.ToUpper() == "CONCLUIDO")
                    {
                        Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show("Pagamento PIX recebido e confirmado na conta PJ do Banco Inter!", "PIX Confirmado", MessageBoxButton.OK, MessageBoxImage.Information);
                            FinishSaleFlow();
                        });
                        break;
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Polling Inter Error] {ex.Message}");
                }
            }
        }

        private void CopyPixCodeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(PixCopiaColaTextBox.Text))
            {
                Clipboard.SetText(PixCopiaColaTextBox.Text);
                MessageBox.Show("Código PIX copiado para a área de transferência!", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ConsultationSearchButton_Click(object sender, RoutedEventArgs e)
        {
            ExecuteConsultationSearch();
        }

        private void ConsultationSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ExecuteConsultationSearch();
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
                var product = await connection.Table<Product>()
                    .Where(p => p.Barcode == query && p.Active)
                    .FirstOrDefaultAsync();

                if (product == null)
                {
                    var matches = await connection.Table<Product>()
                        .Where(p => p.Name.Contains(query) && p.Active)
                        .ToListAsync();

                    if (matches.Any())
                    {
                        product = matches.First();
                    }
                }

                if (product != null)
                {
                    ConsultationProductName.Text = product.Name;
                    ConsultationProductPrice.Text = $"R$ {product.PriceSale / 100.0:F2}";
                    ConsultationProductStock.Text = $"Estoque: {(product.UnitMeasure == "KG" ? $"{product.LocalStockQuantity:F3} KG" : $"{product.LocalStockQuantity:F0} UN")}";
                    
                    var category = await connection.FindAsync<Category>(product.CategoryId);
                    ConsultationProductCategory.Text = $"Categoria: {(category != null ? category.Name : "Geral")}";
                    ConsultationBarcode.Text = product.Barcode ?? "-";
                }
                else
                {
                    MessageBox.Show("Produto não encontrado.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro na consulta: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void KioskToggle_Checked(object sender, RoutedEventArgs e)
        {
            this.WindowStyle = WindowStyle.None;
            this.WindowState = WindowState.Maximized;
            this.Topmost = true;
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
                this.WindowState = WindowState.Normal;
                this.Topmost = false;
            }
            else
            {
                KioskToggle.IsChecked = true;
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (KioskToggle.IsChecked == true)
            {
                e.Cancel = true;
                MessageBox.Show("Desative o Modo Tela Cheia com a senha administrativa para poder fechar o aplicativo.", "Segurança", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (KioskToggle.IsChecked == true)
            {
                MessageBox.Show("Desative o Modo Tela Cheia antes de fazer logout.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var login = new LoginWindow();
            login.Show();
            _syncTimer?.Stop();
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
                    .Where(p => string.IsNullOrEmpty(filter) || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
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
                    .OrderBy(p => p.Name)
                    .ToList();

                StockProductsList.ItemsSource = views;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao carregar estoque: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void LoadLowStockAlerts()
        {
            try
            {
                var connection = App.Database.GetConnection();
                var products = await connection.Table<Product>()
                    .Where(p => p.Active && p.LocalStockQuantity <= p.MinStock)
                    .ToListAsync();
                
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

                // Atualiza contadores
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
                MessageBox.Show($"Erro ao carregar alertas de estoque: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SearchStockBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchStockPlaceholder.Visibility = string.IsNullOrEmpty(SearchStockBox.Text) 
                ? Visibility.Visible 
                : Visibility.Collapsed;

            LoadStock(SearchStockBox.Text.Trim());
        }

        private async void AdjustStockButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string productId)
            {
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
                            double oldQty = product.LocalStockQuantity;
                            double newQty = combo.SelectedIndex == 0 ? oldQty + inputVal : inputVal;

                            if (newQty < 0)
                            {
                                MessageBox.Show("O saldo do estoque não pode ser negativo.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                                return;
                            }

                            try
                            {
                                string tenantId = EnvService.Get("TENANT_ID", CurrentUser.TenantId);
                                string storeId = EnvService.Get("STORE_ID", CurrentUser.StoreId);

                                var movType = newQty >= oldQty ? "ENTRADA" : "SAIDA";
                                var diff = Math.Abs(newQty - oldQty);

                                var movement = new StockMovement
                                {
                                    Id = Guid.NewGuid().ToString(),
                                    ProductId = product.Id,
                                    StoreId = storeId,
                                    UserId = CurrentUser.Id,
                                    TenantId = tenantId,
                                    Type = movType,
                                    Quantity = diff,
                                    Reason = "AJUSTE_MANUAL",
                                    CreatedAt = DateTime.Now,
                                    IsSynced = false
                                };

                                await App.Database.RunInTransactionAsync((tx) =>
                                {
                                    tx.Insert(movement);
                                    product.LocalStockQuantity = newQty;
                                    tx.Update(product);
                                });

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

        private void AddProductButton_Click(object sender, RoutedEventArgs e)
        {
            OpenProductFormWindow(null);
        }

        private async void EditProductButton_Click(object sender, RoutedEventArgs e)
        {
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

        private async void OpenProductFormWindow(Product? productToEdit)
        {
            bool isEdit = productToEdit != null;
            var formWindow = new Window
            {
                Title = isEdit ? $"Editar Produto - {productToEdit!.Name}" : "Novo Produto",
                Width = 450,
                Height = 580,
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
            var txtCategoria = createTextBox();
            
            var cmbTipo = new ComboBox { SelectedIndex = 0, Background = AppColors.Surface, Foreground = System.Windows.Media.Brushes.Black };
            cmbTipo.Items.Add("NORMAL");
            cmbTipo.Items.Add("PAO_FRANCES");
            cmbTipo.Items.Add("SALGADO");
            cmbTipo.Items.Add("BOLO");

            var cmbUnidade = new ComboBox { SelectedIndex = 0, Background = AppColors.Surface, Foreground = System.Windows.Media.Brushes.Black };
            cmbUnidade.Items.Add("UN");
            cmbUnidade.Items.Add("KG");

            var txtPrecoVenda = createTextBox();
            var txtPrecoCusto = createTextBox();
            var txtMinStock = createTextBox();
            var txtEstoqueInicial = createTextBox();

            if (isEdit)
            {
                txtNome.Text = productToEdit!.Name;
                txtBarcode.Text = productToEdit.Barcode;
                
                try
                {
                    var conn = App.Database.GetConnection();
                    var category = await conn.FindAsync<Category>(productToEdit.CategoryId);
                    txtCategoria.Text = category?.Name ?? "Geral";
                }
                catch { txtCategoria.Text = "Geral"; }

                cmbTipo.SelectedItem = productToEdit.Type;
                cmbUnidade.SelectedItem = productToEdit.UnitMeasure;
                txtPrecoVenda.Text = (productToEdit.PriceSale / 100.0).ToString("F2");
                txtPrecoCusto.Text = (productToEdit.PriceCost / 100.0).ToString("F2");
                txtMinStock.Text = productToEdit.MinStock.ToString();
                txtEstoqueInicial.Text = productToEdit.LocalStockQuantity.ToString();
                txtEstoqueInicial.IsEnabled = false;
            }
            else
            {
                txtPrecoVenda.Text = "0,00";
                txtPrecoCusto.Text = "0,00";
                txtMinStock.Text = "5";
                txtEstoqueInicial.Text = "0";
            }

            stack.Children.Add(createField("Nome do Produto", txtNome));
            stack.Children.Add(createField("Código de Barras", txtBarcode));
            stack.Children.Add(createField("Categoria", txtCategoria));
            stack.Children.Add(createField("Tipo de Produto", cmbTipo));
            stack.Children.Add(createField("Unidade de Medida", cmbUnidade));

            var pricesGrid = new Grid { Margin = new Thickness(0,0,0,12) };
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

            var stockGrid = new Grid { Margin = new Thickness(0,0,0,15) };
            stockGrid.ColumnDefinitions.Add(new ColumnDefinition());
            stockGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            stockGrid.ColumnDefinitions.Add(new ColumnDefinition());
            
            var minStockPanel = createField("Estoque Mínimo", txtMinStock);
            var initStockPanel = createField("Estoque Inicial", txtEstoqueInicial);
            stockGrid.Children.Add(minStockPanel);
            Grid.SetColumn(minStockPanel, 0);
            stockGrid.Children.Add(initStockPanel);
            Grid.SetColumn(initStockPanel, 2);
            stack.Children.Add(stockGrid);

            var confirmBtn = new Button
            {
                Content = isEdit ? "Salvar Alterações" : "Cadastrar Produto",
                Padding = new Thickness(12),
                Background = AppColors.Accent,
                Foreground = AppColors.BgBase,
                FontWeight = FontWeights.Bold
            };

            confirmBtn.Click += async (s, ev) =>
            {
                var name = txtNome.Text.Trim();
                var barcode = (txtBarcode?.Text ?? string.Empty).Trim();
                var categoryName = txtCategoria.Text.Trim();
                var tipo = cmbTipo.SelectedItem?.ToString() ?? "NORMAL";
                var unidade = cmbUnidade.SelectedItem?.ToString() ?? "UN";

                if (string.IsNullOrEmpty(name))
                {
                    MessageBox.Show("O nome do produto é obrigatório.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrEmpty(categoryName))
                {
                    categoryName = "Geral";
                }

                if (!TryParseDouble(txtPrecoVenda.Text, out double priceSaleVal) || priceSaleVal < 0 ||
                    !TryParseDouble(txtPrecoCusto.Text, out double priceCostVal) || priceCostVal < 0 ||
                    !TryParseDouble(txtMinStock.Text, out double minStockVal) || minStockVal < 0 ||
                    !TryParseDouble(txtEstoqueInicial.Text, out double initialStockVal) || initialStockVal < 0)
                {
                    MessageBox.Show("Preencha valores numéricos válidos e não negativos.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int priceSaleCents = (int)Math.Round(priceSaleVal * 100);
                int priceCostCents = (int)Math.Round(priceCostVal * 100);

                try
                {
                    var conn = App.Database.GetConnection();

                    if (!string.IsNullOrEmpty(barcode))
                    {
                        var dupQuery = conn.Table<Product>().Where(p => p.Barcode == barcode && p.Active);
                        if (isEdit)
                        {
                            dupQuery = dupQuery.Where(p => p.Id != productToEdit!.Id);
                        }
                        var duplicate = await dupQuery.FirstOrDefaultAsync();
                        if (duplicate != null)
                        {
                            MessageBox.Show("Já existe outro produto ativo cadastrado com este código de barras.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }

                    var category = await conn.Table<Category>().Where(c => c.Name == categoryName).FirstOrDefaultAsync();
                    if (category == null)
                    {
                        category = new Category
                        {
                            Id = Guid.NewGuid().ToString(),
                            Name = categoryName,
                            TenantId = CurrentUser.TenantId
                        };
                        await conn.InsertAsync(category);
                    }

                    if (isEdit)
                    {
                        productToEdit!.Name = name;
                        productToEdit.Barcode = string.IsNullOrEmpty(barcode) ? null : barcode;
                        productToEdit.CategoryId = category.Id;
                        productToEdit.Type = tipo;
                        productToEdit.UnitMeasure = unidade;
                        productToEdit.PriceSale = priceSaleCents;
                        productToEdit.PriceCost = priceCostCents;
                        productToEdit.MinStock = minStockVal;
                        productToEdit.UpdatedAt = DateTime.Now;

                        await conn.UpdateAsync(productToEdit);
                        MessageBox.Show("Produto atualizado com sucesso!", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        var newProd = new Product
                        {
                            Id = Guid.NewGuid().ToString(),
                            Name = name,
                            Barcode = string.IsNullOrEmpty(barcode) ? null : barcode,
                            CategoryId = category.Id,
                            Type = tipo,
                            UnitMeasure = unidade,
                            PriceSale = priceSaleCents,
                            PriceCost = priceCostCents,
                            MinStock = minStockVal,
                            LocalStockQuantity = initialStockVal,
                            Active = true,
                            TenantId = CurrentUser.TenantId,
                            CreatedAt = DateTime.Now,
                            UpdatedAt = DateTime.Now
                        };

                        string tenantId = EnvService.Get("TENANT_ID", CurrentUser.TenantId);
                        string storeId = EnvService.Get("STORE_ID", CurrentUser.StoreId);

                        await App.Database.RunInTransactionAsync((tx) =>
                        {
                            tx.Insert(newProd);

                            if (initialStockVal > 0)
                            {
                                var movement = new StockMovement
                                {
                                    Id = Guid.NewGuid().ToString(),
                                    ProductId = newProd.Id,
                                    StoreId = storeId,
                                    UserId = CurrentUser.Id,
                                    TenantId = tenantId,
                                    Type = "ENTRADA",
                                    Quantity = initialStockVal,
                                    Reason = "REPOSICAO",
                                    CreatedAt = DateTime.Now,
                                    IsSynced = false
                                };
                                tx.Insert(movement);
                            }
                        });

                        MessageBox.Show("Produto cadastrado com sucesso!", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
                    }

                    formWindow.Close();
                    LoadStock(SearchStockBox.Text.Trim());

                    _ = Task.Run(async () => await RunSincronizacaoSilenciosa());
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao salvar produto: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            stack.Children.Add(confirmBtn);

            var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            formWindow.Content = scroll;

            txtNome.Focus();
            formWindow.ShowDialog();
        }

        private async void DeleteProductButton_Click(object sender, RoutedEventArgs e)
        {
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
                    .Where(s => s.SaleDate >= fullStart && s.SaleDate <= fullEnd)
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
            LoadSalesHistory();
        }

        private async void CancelSaleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string saleId)
            {
                if (MessageBox.Show("Deseja realmente cancelar/estornar esta venda? O estoque dos produtos será devolvido.", "Confirmação", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }

                try
                {
                    var connection = App.Database.GetConnection();
                    var sale = await connection.FindAsync<Sale>(saleId);
                    if (sale == null || sale.PaymentStatus == "CANCELADO") return;

                    var saleItems = await connection.Table<SaleItem>().Where(i => i.SaleId == saleId).ToListAsync();

                    await App.Database.RunInTransactionAsync((tx) =>
                    {
                        // 1. Marca venda como cancelada
                        sale.PaymentStatus = "CANCELADO";
                        sale.IsSynced = false;
                        tx.Update(sale);

                        // 2. Devolve os produtos para o estoque e registra entradas
                        foreach (var item in saleItems)
                        {
                            var movement = new StockMovement
                            {
                                Id = Guid.NewGuid().ToString(),
                                ProductId = item.ProductId,
                                StoreId = sale.StoreId,
                                UserId = CurrentUser.Id,
                                TenantId = sale.TenantId,
                                Type = "ENTRADA",
                                Quantity = item.Quantity,
                                Reason = "CANCELAMENTO_VENDA",
                                SaleId = sale.Id,
                                CreatedAt = DateTime.Now,
                                IsSynced = false
                            };
                            tx.Insert(movement);

                            var product = tx.Find<Product>(item.ProductId);
                            if (product != null)
                            {
                                product.LocalStockQuantity += item.Quantity;
                                tx.Update(product);
                            }
                        }
                    });

                    MessageBox.Show("Venda cancelada com sucesso! Estoque devolvido.", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
                    LoadSalesHistory();

                    _ = Task.Run(async () => await RunSincronizacaoSilenciosa());
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao cancelar venda: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // ================= ABA 3: DASHBOARD LOGIC =================

        private async void LoadDashboard()
        {
            try
            {
                var connection = App.Database.GetConnection();
                
                DateTime startDate = DashStartDatePicker.SelectedDate ?? DateTime.Today;
                DateTime endDate = DashEndDatePicker.SelectedDate ?? DateTime.Today;

                string startTimeStr = DashStartTimeTextBox.Text.Trim();
                string endTimeStr = DashEndTimeTextBox.Text.Trim();
                if (!TimeSpan.TryParse(startTimeStr, out TimeSpan startTime)) startTime = new TimeSpan(0, 0, 0);
                if (!TimeSpan.TryParse(endTimeStr, out TimeSpan endTime)) endTime = new TimeSpan(23, 59, 59);

                DateTime fullStart = startDate.Date.Add(startTime);
                DateTime fullEnd = endDate.Date.Add(endTime);

                var sales = await connection.Table<Sale>()
                    .Where(s => s.SaleDate >= fullStart && s.SaleDate <= fullEnd)
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

            // Recarrega aba aberta
            if (MainTabControl.SelectedIndex == 1) LoadStock(SearchStockBox.Text.Trim());
            else if (MainTabControl.SelectedIndex == 2) LoadSalesHistory();
            else if (MainTabControl.SelectedIndex == 3) LoadDashboard();
        }

        private async Task RunSincronizacaoSilenciosa()
        {
            await SincronizarDadosNuvem();
        }

        private async Task<(bool Success, string Error)> SincronizarDadosNuvem()
        {
            // Trava anti-sobreposição ATÔMICA: timer de 60s, botão manual e o sync disparado após
            // venda/cancelamento podem vir de threads diferentes. WaitAsync(0) não bloqueia — se já
            // houver um sync em curso, sai sem erro (evita dois ciclos simultâneos corromperem o SQLite).
            if (!await _syncGate.WaitAsync(0)) return (true, string.Empty);
            try
            {
                using var syncService = new SyncService(App.Database.GetSyncConnection());

                string tenantId = EnvService.Get("TENANT_ID", CurrentUser.TenantId);
                string storeId = EnvService.Get("STORE_ID", CurrentUser.StoreId);

                bool pushSuccess = await syncService.PushSalesAsync(tenantId, storeId);
                bool pullSuccess = await syncService.PullUpdatesAsync(tenantId, storeId);

                // Envia a "foto" do estoque local desta loja para a nuvem (alimenta o painel do dono).
                // Só envia se a VENDA e o CATÁLOGO subiram com sucesso: garante que o snapshot publicado
                // já reflete as vendas que o causaram (senão saldo e histórico divergem no painel) e que
                // produtos novos já foram semeados localmente (evita subir estoque zerado).
                bool stockSuccess = true;
                if (pushSuccess && pullSuccess)
                {
                    stockSuccess = await syncService.PushStockSnapshotAsync(tenantId, storeId);
                }

                var pendingCount = await App.Database.GetConnection().Table<Sale>().Where(s => !s.IsSynced).CountAsync();

                Dispatcher.Invoke(() => {
                    if (pendingCount > 0)
                    {
                        SyncStatusText.Text = $"{pendingCount} venda(s) pendente(s)";
                        SyncStatusIndicator.Fill = System.Windows.Media.Brushes.Yellow;
                    }
                    else if (pushSuccess && pullSuccess)
                    {
                        SyncStatusText.Text = "Sincronizado";
                        SyncStatusIndicator.Fill = System.Windows.Media.Brushes.Green;
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
                var now = DateTime.Now;
                DateTime de = _redePeriodoDias == 0 ? now.Date : now.Date.AddDays(-_redePeriodoDias);
                DateTime ate = now.Date.AddDays(1).AddSeconds(-1);
                string from = de.ToString("yyyy-MM-ddTHH:mm:ss");
                string to = ate.ToString("yyyy-MM-ddTHH:mm:ss");

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

                var rede = j["rede"]!;
                RedeFat.Text = FormatBRL((long)rede["faturamento_centavos"]!);
                RedeVendas.Text = rede["vendas_qtd"]!.ToString();
                RedeLojas.Text = rede["lojas_total"]!.ToString();

                var lojas = new List<RedeLojaView>();
                foreach (var l in (Newtonsoft.Json.Linq.JArray)j["lojas"]!)
                {
                    int baixo = (int)l["estoque_baixo"]!;
                    lojas.Add(new RedeLojaView
                    {
                        Nome = l["nome"]!.ToString(),
                        Resumo = $"{l["vendas_qtd"]} venda(s) · {l["estoque_produtos"]} produtos",
                        FatString = FormatBRL((long)l["faturamento_centavos"]!),
                        AlertaText = baixo > 0 ? $"{baixo} em falta" : string.Empty,
                        TemAlerta = baixo > 0 ? Visibility.Visible : Visibility.Collapsed
                    });
                }
                RedeLojasList.ItemsSource = lojas;

                var tops = new List<RedeTopView>();
                foreach (var t in (Newtonsoft.Json.Linq.JArray)j["top_produtos"]!)
                {
                    tops.Add(new RedeTopView
                    {
                        Nome = t["nome"]!.ToString(),
                        QtdString = $"×{t["qtd"]}",
                        FatString = FormatBRL((long)t["faturamento_centavos"]!)
                    });
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
    }
}