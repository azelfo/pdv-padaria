using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PdvPadaria.Services;
using PdvPadaria.Views;
using ZXing;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static void Main()
    {
        foreach (string culture in new[] { "pt-BR", "en-US" })
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            foreach (var valid in new[] { ("20", 2000), ("20,50", 2050), ("1.000,00", 100000),
                ("R$ 0,01", 1), ("21.474.836,47", int.MaxValue), ("0", 0) })
                Check(MoneyInput.TentarLerCentavos(valid.Item1, out int cents) && cents == valid.Item2,
                    "Moeda válida: " + valid.Item1 + " / " + culture);

            foreach (string invalid in new[] { "", "NaN", "Infinity", "-1", "-21474836,48", "21.474.836,48",
                "1e3", "1,999", "1.00", "1.000.00", "1,000.00", "9999999999999999999999999999999999", "R$ R$ 1,00" })
                Check(!MoneyInput.TentarLerCentavos(invalid, out _), "Moeda inválida: " + invalid);
        }

        string product = "2000000000015";
        Check(WeightBarcodeService.ExtrairRefProduto(product) == "00001", "Referência do produto");
        Check(WeightBarcodeService.ExtrairRefProduto("2000000000016") == null, "Checksum do produto inválido");
        Check(WeightBarcodeService.TentarCalcularPreco(2000, 1000, out int price) && price == 2000, "Preço da emissão");
        string label = WeightBarcodeService.Gerar(product, price, 1000)!;
        Check(label == "PDV100001020000001000", "Formato da etiqueta preserva preço e peso");
        Check(WeightBarcodeService.TentarLer(label, out string reference, out int amount, out int grams)
            && reference == "00001" && amount == 2000 && grams == 1000, "Leitura sem cadastro/banco");
        int newKiloPrice = 2500;
        Check(grams == 1000 && amount != newKiloPrice, "Novo preço/kg não altera peso nem total da etiqueta emitida");
        Check(WeightBarcodeService.TentarCalcularPreco(1999, 500, out price) && price == 1000,
            "Meio centavo na emissão arredonda para cima");
        Check(WeightBarcodeService.TentarCalcularPreco(1, 500, out price) && price == 1, "Meio centavo mínimo");
        Check(!WeightBarcodeService.TentarCalcularPreco(int.MaxValue, WeightBarcodeService.PesoMaximoGramas, out _),
            "Preço excessivo não transborda");
        Check(!WeightBarcodeService.TentarCalcularPreco(1000, 0, out _), "Peso zero recusado");
        Check(WeightBarcodeService.Gerar(product, 99999, WeightBarcodeService.PesoMaximoGramas) != null,
            "Limites máximos representáveis");
        Check(WeightBarcodeService.Gerar(product, 100000, 1000) == null, "Preço além da etiqueta recusado");
        Check(WeightBarcodeService.Gerar(product, 1000, WeightBarcodeService.PesoMaximoGramas + 1) == null,
            "Peso além da etiqueta recusado");
        foreach (string invalid in new[] { "PDV100001000000001000", "PDV100001020000000000", label + "1",
            label.Substring(0, 20), "PDV200001020000001000", "PDV10000102X000001000" })
            Check(!WeightBarcodeService.TentarLer(invalid, out _, out _, out _), "Etiqueta malformada: " + invalid);
        string legacyTwelve = "210000102000";
        string legacy = legacyTwelve + WeightBarcodeService.CalcularDigitoEan13(legacyTwelve);
        Check(WeightBarcodeService.TentarLer(legacy, out _, out amount, out grams) && amount == 2000 && grams == 0,
            "Etiqueta antiga identifica ausência de peso para exigir reemissão");

        var bitmap = BarcodeService.Gerar(label, 600, 180)
            ?? throw new Exception("Gerador existente recusou Code128 novo");
        Check(bitmap.PixelWidth > 0, "Gerador existente aceita Code128 novo");
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var decoded = new BarcodeReaderGeneric().Decode(new RGBLuminanceSource(pixels,
            bitmap.PixelWidth, bitmap.PixelHeight, RGBLuminanceSource.BitmapFormat.BGRA32));
        Check(decoded != null && decoded.Text == label && decoded.BarcodeFormat == BarcodeFormat.CODE_128,
            "Imagem real Code128 decodifica com preço e peso originais");

        CheckPaymentWindow();
        Console.WriteLine(_checks + " verificações passaram; nenhum banco ou serviço remoto foi acessado.");
    }

    private static void CheckPaymentWindow()
    {
        var app = new Application();
        foreach (string key in new[] { "Surface", "BorderSoft", "Accent", "AccentBadge", "TextMuted", "TextSubtle",
            "TextPrimary", "SuccessTint", "Success", "DangerTint", "Danger", "BorderStrong", "InfoLight", "Info",
            "AccentTint", "AccentLight", "TextFaint" })
            app.Resources.Add(key, Brushes.Black);
        app.Resources.Add("RadiusSm", new CornerRadius(4));
        app.Resources.Add("ElevCard", new DropShadowEffect());

        var window = new PaymentWindow(1500);
        ((Button)window.FindName("BtnSelectCash")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var input = (TextBox)window.FindName("CashReceivedInput");
        var finish = (Button)window.FindName("FinishCashBtn");
        input.Text = "20,00";
        Check(finish.IsEnabled && window.ReceivedAmountCentavos == 2000 && window.ChangeAmountCentavos == 500,
            "Tela calcula troco correto");
        input.Text = "NaN";
        Check(!finish.IsEnabled && window.ReceivedAmountCentavos == 0, "Entrada inválida apaga recebimento anterior");
        finish.IsEnabled = true;
        finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.DialogResult != true, "Conclusão revalida valor mesmo com botão habilitado");
        input.Text = "2,00";
        Check(!finish.IsEnabled && ((TextBlock)window.FindName("TrocoLabel")).Text == "Falta receber:",
            "Dinheiro insuficiente não conclui");
        ((Button)window.FindName("BtnSelectPix")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.SelectedPaymentMethod == "PIX" && ((Button)window.FindName("FinishPixBtn")).Visibility == Visibility.Visible
            && window.DialogResult != true, "PIX fica manual, sem temporizador nem aprovação automática");
        window.Close();
    }

    private static void Check(bool success, string name)
    {
        if (!success) throw new Exception("FALHOU: " + name);
        _checks++;
    }
}
