using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PdvPadaria.Views
{
    public partial class PaymentWindow : Window
    {
        private readonly int _totalCentavos;
        private PaymentMode _currentMode = PaymentMode.Selection;
        private int _paymentDelayRemaining = 0;
        private CancellationTokenSource? _delayCts;

        public string SelectedPaymentMethod { get; private set; } = string.Empty;
        public int ReceivedAmountCentavos { get; private set; } = 0;
        public int ChangeAmountCentavos { get; private set; } = 0;

        private enum PaymentMode
        {
            Selection,
            Cash,
            Pix,
            Card
        }

        public PaymentWindow(int totalCentavos)
        {
            InitializeComponent();
            _totalCentavos = totalCentavos;
            
            TotalVendaText.Text = $"R$ {totalCentavos / 100.0:F2}";
            CashTotalText.Text = $"R$ {totalCentavos / 100.0:F2}";
            
            SetPaymentMode(PaymentMode.Selection);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            this.Focus();
        }

        private void SetPaymentMode(PaymentMode mode)
        {
            _delayCts?.Cancel();
            _delayCts?.Dispose();
            _delayCts = null;
            _paymentDelayRemaining = 0;

            _currentMode = mode;
            SelectionGrid.Visibility = mode == PaymentMode.Selection ? Visibility.Visible : Visibility.Collapsed;
            CashGrid.Visibility = mode == PaymentMode.Cash ? Visibility.Visible : Visibility.Collapsed;
            PixGrid.Visibility = mode == PaymentMode.Pix ? Visibility.Visible : Visibility.Collapsed;
            CardGrid.Visibility = mode == PaymentMode.Card ? Visibility.Visible : Visibility.Collapsed;

            if (mode == PaymentMode.Cash)
            {
                CashReceivedInput.Text = string.Empty;
                TrocoText.Text = "R$ 0,00";
                FinishCashBtn.IsEnabled = false;
                
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    CashReceivedInput.Focus();
                    Keyboard.Focus(CashReceivedInput);
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
            else if (mode == PaymentMode.Pix)
            {
                _ = StartPaymentDelay(FinishPixBtn, PixLoaderPanel, TxtPixCountdown);
            }
            else if (mode == PaymentMode.Card)
            {
                _ = StartPaymentDelay(FinishCardBtn, CardLoaderPanel, TxtCardCountdown);
            }
        }

        private async Task StartPaymentDelay(Button btn, StackPanel loaderPanel, TextBlock countdownText)
        {
            _delayCts = new CancellationTokenSource();
            var token = _delayCts.Token;

            btn.Visibility = Visibility.Collapsed;
            loaderPanel.Visibility = Visibility.Visible;
            _paymentDelayRemaining = 20;

            try
            {
                while (_paymentDelayRemaining > 0)
                {
                    countdownText.Text = $"Aguarde o processamento na maquininha... ({_paymentDelayRemaining}s)";
                    await Task.Delay(1000, token);
                    _paymentDelayRemaining--;
                }
                
                loaderPanel.Visibility = Visibility.Collapsed;
                btn.Visibility = Visibility.Visible;
                btn.IsEnabled = true;
            }
            catch (TaskCanceledException)
            {
                // Cancelado
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    _paymentDelayRemaining = 0;
                }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // Garante a liberação do CancellationTokenSource ao fechar a janela (evita leak de handle).
        protected override void OnClosed(EventArgs e)
        {
            _delayCts?.Cancel();
            _delayCts?.Dispose();
            _delayCts = null;
            base.OnClosed(e);
        }

        private void BtnSelectCash_Click(object sender, RoutedEventArgs e)
        {
            SelectedPaymentMethod = "DINHEIRO";
            SetPaymentMode(PaymentMode.Cash);
        }

        private void BtnSelectPix_Click(object sender, RoutedEventArgs e)
        {
            SelectedPaymentMethod = "PIX";
            SetPaymentMode(PaymentMode.Pix);
        }

        private void BtnSelectCard_Click(object sender, RoutedEventArgs e)
        {
            SelectedPaymentMethod = "CARTAO_CREDITO";
            SetPaymentMode(PaymentMode.Card);
        }

        private void CashReceivedInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            string raw = CashReceivedInput.Text;
            if (TryParseDouble(raw, out double receivedVal))
            {
                int receivedCentavos = (int)Math.Round(receivedVal * 100);
                int trocoCentavos = receivedCentavos - _totalCentavos;

                if (trocoCentavos >= 0)
                {
                    TrocoText.Text = $"R$ {trocoCentavos / 100.0:F2}";
                    FinishCashBtn.IsEnabled = true;
                    ReceivedAmountCentavos = receivedCentavos;
                    ChangeAmountCentavos = trocoCentavos;
                }
                else
                {
                    TrocoText.Text = "R$ 0,00";
                    FinishCashBtn.IsEnabled = false;
                }
            }
            else
            {
                TrocoText.Text = "R$ 0,00";
                FinishCashBtn.IsEnabled = false;
            }
        }

        private void FinishCashBtn_Click(object sender, RoutedEventArgs e)
        {
            if (FinishCashBtn.IsEnabled)
            {
                DialogResult = true;
                Close();
            }
        }

        private void ConfirmPayment_Click(object sender, RoutedEventArgs e)
        {
            if (_paymentDelayRemaining > 0) return;
            DialogResult = true;
            Close();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_currentMode == PaymentMode.Selection)
            {
                if (e.Key == Key.F1)
                {
                    e.Handled = true;
                    BtnSelectCash_Click(null!, null!);
                }
                else if (e.Key == Key.F2)
                {
                    e.Handled = true;
                    BtnSelectPix_Click(null!, null!);
                }
                else if (e.Key == Key.F3)
                {
                    e.Handled = true;
                    BtnSelectCard_Click(null!, null!);
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    DialogResult = false;
                    Close();
                }
            }
            else if (_currentMode == PaymentMode.Cash)
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    FinishCashBtn_Click(null!, null!);
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    SetPaymentMode(PaymentMode.Selection);
                }
            }
            else if (_currentMode == PaymentMode.Pix)
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    if (_paymentDelayRemaining == 0)
                    {
                        ConfirmPayment_Click(null!, null!);
                    }
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    SetPaymentMode(PaymentMode.Selection);
                }
            }
            else if (_currentMode == PaymentMode.Card)
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    if (_paymentDelayRemaining == 0)
                    {
                        ConfirmPayment_Click(null!, null!);
                    }
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    SetPaymentMode(PaymentMode.Selection);
                }
            }
        }

        private bool TryParseDouble(string input, out double val)
        {
            val = 0;
            if (string.IsNullOrWhiteSpace(input)) return false;
            
            input = input.Trim().Replace("R$", "").Trim();
            
            // O .NET Framework 4.8 não tem a sobrecarga TryParse(string, IFormatProvider, out)
            // — só a que recebe NumberStyles junto. NumberStyles.Any aceita separador de
            // milhar, sinal e decimal, que é o comportamento desejado aqui.
            if (double.TryParse(input.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out val)) return true;
            if (double.TryParse(input.Replace('.', ','), NumberStyles.Any, new CultureInfo("pt-BR"), out val)) return true;
            
            return false;
        }
    }
}
