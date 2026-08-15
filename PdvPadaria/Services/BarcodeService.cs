using System;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace PdvPadaria.Services
{
    // Gera a imagem do código de barras para a aba Etiquetas.
    // Escolhe o formato sozinho:
    //   - 13 dígitos numéricos com checksum EAN-13 válido -> EAN_13 (padrão de varejo)
    //   - qualquer outra coisa (código curto tipo "1001", "123", alfanumérico) -> CODE_128,
    //     que aceita qualquer conteúdo e todo leitor lê.
    // Retorna um BitmapSource pronto para exibir/imprimir no WPF (sem System.Drawing).
    public static class BarcodeService
    {
        public static BitmapSource? Gerar(string? codigo, int largura = 300, int altura = 90)
        {
            if (string.IsNullOrWhiteSpace(codigo)) return null;
            codigo = codigo!.Trim();

            var formato = EscolherFormato(codigo);

            var writer = new BarcodeWriterPixelData
            {
                Format = formato,
                Options = new EncodingOptions
                {
                    Width = largura,
                    Height = altura,
                    Margin = 6,       // "quiet zone" — margem branca que o leitor precisa
                    PureBarcode = false // deixa o ZXing escrever os dígitos embaixo (EAN-13)
                }
            };

            try
            {
                var pixelData = writer.Write(codigo);
                var bmp = new WriteableBitmap(pixelData.Width, pixelData.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                bmp.Lock();
                System.Runtime.InteropServices.Marshal.Copy(pixelData.Pixels, 0, bmp.BackBuffer, pixelData.Pixels.Length);
                bmp.AddDirtyRect(new System.Windows.Int32Rect(0, 0, pixelData.Width, pixelData.Height));
                bmp.Unlock();
                bmp.Freeze(); // thread-safe + pode reusar na impressão
                return bmp;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BarcodeService.Gerar Error]: {ex.Message}");
                return null;
            }
        }

        private static BarcodeFormat EscolherFormato(string codigo)
        {
            if (codigo.Length == 13 && SoDigitos(codigo) && Ean13ChecksumValido(codigo))
                return BarcodeFormat.EAN_13;
            return BarcodeFormat.CODE_128;
        }

        private static bool SoDigitos(string s)
        {
            foreach (var c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        // Valida o dígito verificador de um EAN-13 (mesma regra do servidor).
        private static bool Ean13ChecksumValido(string codigo13)
        {
            int soma = 0;
            for (int i = 0; i < 12; i++)
            {
                int d = codigo13[i] - '0';
                soma += (i % 2 == 0) ? d : d * 3;
            }
            int verificador = (10 - (soma % 10)) % 10;
            return verificador == (codigo13[12] - '0');
        }
    }
}
