using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using PdvPadaria.Models;
using PdvPadaria.Services;
using Newtonsoft.Json;

namespace PdvPadaria.Views
{
    public partial class SaleDetailsWindow : Window
    {
        // HttpClient ÚNICO — reusar evita esgotar sockets (TIME_WAIT 240s no Windows).
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        private readonly string _saleId;
        private readonly string _remoteStoreId; // Vazio se for venda local
        private readonly User _currentUser;
        private readonly string _donoEmail;
        private readonly string _donoPassword;
        private bool _wasCanceled = false;

        public bool WasCanceled => _wasCanceled;

        public SaleDetailsWindow(string saleId, string remoteStoreId, User currentUser, string donoEmail = "", string donoPassword = "")
        {
            InitializeComponent();
            _saleId = saleId;
            _remoteStoreId = remoteStoreId;
            _currentUser = currentUser;
            _donoEmail = donoEmail;
            _donoPassword = donoPassword;

            Loaded += SaleDetailsWindow_Loaded;
        }

        private async void SaleDetailsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadSaleData();
        }

        private async Task LoadSaleData()
        {
            try
            {
                if (string.IsNullOrEmpty(_remoteStoreId))
                {
                    // Carregamento local (SQLite)
                    await LoadLocalSale();
                }
                else
                {
                    // Carregamento remoto (Supabase)
                    await LoadRemoteSale();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao carregar detalhes da venda: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private async Task LoadLocalSale()
        {
            var connection = App.Database.GetConnection();
            var sale = await connection.FindAsync<Sale>(_saleId);
            if (sale == null)
            {
                MessageBox.Show("Venda não encontrada localmente.", "Erro", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                return;
            }

            TxtSaleId.Text = $"ID: {sale.Id}";
            TxtDate.Text = sale.SaleDate.ToString("dd/MM/yyyy HH:mm");
            TxtPaymentMethod.Text = sale.PaymentMethod;
            TxtTotal.Text = $"R$ {sale.Total / 100.0:F2}";

            // Configura status
            bool isCanceled = sale.PaymentStatus == "CANCELADO";
            TxtStatus.Text = sale.PaymentStatus;
            BorderStatus.Background = isCanceled ? AppColors.DangerBadge : AppColors.SuccessBadge;
            TxtStatus.Foreground = isCanceled ? AppColors.Danger : AppColors.Success;

            // Botão cancelar visível apenas se for local e não cancelada
            BtnCancel.Visibility = isCanceled ? Visibility.Collapsed : Visibility.Visible;

            // Busca itens da venda
            var items = await connection.Table<SaleItem>().Where(i => i.SaleId == _saleId).ToListAsync();
            var products = await connection.Table<Product>().ToListAsync();
            var productMap = products
                .Where(p => p.Id != null)
                .GroupBy(p => p.Id)
                .ToDictionary(g => g.Key, g => g.First());

            var views = items.Select(item =>
            {
                productMap.TryGetValue(item.ProductId, out var prod);
                return new SaleDetailItemView
                {
                    ProductName = prod?.Name ?? "Produto Desconhecido",
                    UnitMeasure = prod?.UnitMeasure ?? "UN",
                    Quantity = item.Quantity,
                    PriceUnit = item.PriceUnit,
                    Subtotal = item.Subtotal
                };
            }).ToList();

            ItemsList.ItemsSource = views;
        }

        private async Task LoadRemoteSale()
        {
            string baseUrl = EnvService.Get("SUPABASE_URL").TrimEnd('/');
            string anon = EnvService.Get("SUPABASE_ANON_KEY");
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(anon))
            {
                MessageBox.Show("Configuração da nuvem ausente no .env.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            // Para histórico remoto, o cancelamento direto não é permitido na filial atual
            BtnCancel.Visibility = Visibility.Collapsed;

            // Busca a Venda de forma consolidada e segura através da RPC get_venda_detalhe
            var payload = new
            {
                p_email = _donoEmail,
                p_password = _donoPassword,
                p_sale_id = _saleId
            };
            var content = new StringContent(
                JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/rest/v1/rpc/get_venda_detalhe");
            req.Content = content;
            req.Headers.Add("apikey", anon);
            req.Headers.Add("Authorization", $"Bearer {anon}");

            var resp = await _httpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                MessageBox.Show($"Erro ao carregar dados da venda na nuvem ({(int)resp.StatusCode}).", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            string body = await resp.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<RemoteVendaDetalheResult>(body);
            if (result == null || !string.IsNullOrEmpty(result.Error))
            {
                string err = result?.Error ?? "Venda remota não encontrada.";
                MessageBox.Show(err == "invalid_credentials" ? "Sessão do dono expirada, faça login online de novo." :
                    err == "forbidden" ? "Sem permissão para visualizar vendas." : err,
                    "Venda remota", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                return;
            }

            TxtSaleId.Text = $"ID: {result.Id}";
            if (DateTime.TryParse(result.Data, out DateTime dateVal))
            {
                TxtDate.Text = dateVal.ToString("dd/MM/yyyy HH:mm");
            }
            else
            {
                TxtDate.Text = result.Data;
            }
            TxtPaymentMethod.Text = result.Metodo;
            TxtTotal.Text = $"R$ {result.TotalCentavos / 100.0:F2}";

            bool isCanceled = result.Status == "CANCELADO";
            TxtStatus.Text = result.Status;
            BorderStatus.Background = isCanceled ? AppColors.DangerBadge : AppColors.SuccessBadge;
            TxtStatus.Foreground = isCanceled ? AppColors.Danger : AppColors.Success;

            // Preenche os itens na tela
            var views = new List<SaleDetailItemView>();
            if (result.Itens != null)
            {
                foreach (var item in result.Itens)
                {
                    views.Add(new SaleDetailItemView
                    {
                        ProductName = item.Nome,
                        UnitMeasure = item.Unidade,
                        Quantity = item.Quantidade,
                        PriceUnit = item.PrecoUnitCentavos,
                        Subtotal = item.SubtotalCentavos
                    });
                }
            }

            ItemsList.ItemsSource = views;
        }

        private async void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Deseja realmente cancelar/estornar esta venda? O estoque dos produtos será devolvido.", "Confirmação", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var connection = App.Database.GetConnection();
                var sale = await connection.FindAsync<Sale>(_saleId);
                if (sale == null || sale.PaymentStatus == "CANCELADO") return;

                var saleItems = await connection.Table<SaleItem>().Where(i => i.SaleId == _saleId).ToListAsync();

                await App.Database.RunInTransactionAsync((tx) =>
                {
                    // 1. Marca venda como cancelada
                    sale.PaymentStatus = "CANCELADO";
                    sale.IsSynced = false;
                    tx.Update(sale);

                    // 2. Devolve os produtos para o estoque e registra entradas
                    foreach (var item in saleItems)
                    {
                        // Lê o produto antes de devolver, para registrar saldo anterior/final
                        var product = tx.Find<Product>(item.ProductId);
                        double? saldoAntes = product?.LocalStockQuantity;
                        double? saldoDepois = saldoAntes.HasValue ? saldoAntes + item.Quantity : null;

                        var movement = new StockMovement
                        {
                            Id = Guid.NewGuid().ToString(),
                            ProductId = item.ProductId,
                            StoreId = sale.StoreId,
                            UserId = _currentUser.Id,
                            TenantId = sale.TenantId,
                            Type = "ENTRADA",
                            Quantity = item.Quantity,
                            Reason = "CANCELAMENTO_VENDA",
                            SaleId = sale.Id,
                            CreatedAt = DateTime.Now,
                            IsSynced = false,
                            BalanceBefore = saldoAntes,
                            BalanceAfter = saldoDepois
                        };
                        tx.Insert(movement);

                        if (product != null)
                        {
                            product.LocalStockQuantity = saldoDepois!.Value;
                            tx.Update(product);
                        }
                    }
                });

                _wasCanceled = true;
                MessageBox.Show("Venda cancelada com sucesso! Estoque devolvido.", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
                
                // Recarrega a visualização da tela para refletir o status cancelado e ocultar botão
                await LoadSaleData();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao cancelar venda: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // Classes de Visualização e Parser
        public class SaleDetailItemView
        {
            public string ProductName { get; set; } = string.Empty;
            public double Quantity { get; set; }
            public string UnitMeasure { get; set; } = "UN";
            public int PriceUnit { get; set; }
            public int Subtotal { get; set; }

            public string PriceUnitString => $"R$ {PriceUnit / 100.0:F2}";
            public string SubtotalString => $"R$ {Subtotal / 100.0:F2}";
            public string QuantityString => UnitMeasure == "KG" ? $"{Quantity:F3} KG" : $"{Quantity:F0} UN";
        }

        public class RemoteSale
        {
            public string Id { get; set; } = string.Empty;
            [JsonProperty("saleDate")]
            public DateTime SaleDate { get; set; }
            [JsonProperty("total")]
            public int Total { get; set; }
            [JsonProperty("paymentMethod")]
            public string PaymentMethod { get; set; } = string.Empty;
            [JsonProperty("paymentStatus")]
            public string PaymentStatus { get; set; } = string.Empty;
        }

        public class RemoteSaleItem
        {
            public string ProductId { get; set; } = string.Empty;
            public double Quantity { get; set; }
            public int PriceUnit { get; set; }
            public int Subtotal { get; set; }
            public RemoteProduct? Product { get; set; }
        }

        public class RemoteProduct
        {
            public string Name { get; set; } = string.Empty;
            public string UnitMeasure { get; set; } = "UN";
        }

        public class RemoteVendaDetalheResult
        {
            [JsonProperty("id")]
            public string Id { get; set; } = string.Empty;
            [JsonProperty("data")]
            public string Data { get; set; } = string.Empty;
            [JsonProperty("loja")]
            public string Loja { get; set; } = string.Empty;
            [JsonProperty("metodo")]
            public string Metodo { get; set; } = string.Empty;
            [JsonProperty("status")]
            public string Status { get; set; } = string.Empty;
            [JsonProperty("subtotal_centavos")]
            public int SubtotalCentavos { get; set; }
            [JsonProperty("desconto_centavos")]
            public int DescontoCentavos { get; set; }
            [JsonProperty("total_centavos")]
            public int TotalCentavos { get; set; }
            [JsonProperty("recebido_centavos")]
            public int? RecebidoCentavos { get; set; }
            [JsonProperty("troco_centavos")]
            public int? TrocoCentavos { get; set; }
            [JsonProperty("error")]
            public string? Error { get; set; }
            [JsonProperty("itens")]
            public List<RemoteVendaDetalheItem>? Itens { get; set; }
        }

        public class RemoteVendaDetalheItem
        {
            [JsonProperty("nome")]
            public string Nome { get; set; } = string.Empty;
            [JsonProperty("tipo")]
            public string Tipo { get; set; } = string.Empty;
            [JsonProperty("unidade")]
            public string Unidade { get; set; } = "UN";
            [JsonProperty("quantidade")]
            public double Quantidade { get; set; }
            [JsonProperty("preco_unit_centavos")]
            public int PrecoUnitCentavos { get; set; }
            [JsonProperty("subtotal_centavos")]
            public int SubtotalCentavos { get; set; }
        }
    }
}
