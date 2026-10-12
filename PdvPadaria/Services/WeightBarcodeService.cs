using System;
using System.Globalization;

namespace PdvPadaria.Services
{
    public static class WeightBarcodeService
    {
        // Code128: versão + produto(5) + centavos(5) + gramas(7), independente do preço atual.
        public const string Prefixo = "PDV1";
        public const int PrecoMaximoCentavos = 99999;
        public const int PesoMaximoGramas = 9999999;

        public static string? ExtrairRefProduto(string? barcodeInterno)
        {
            if (string.IsNullOrWhiteSpace(barcodeInterno)) return null;
            string codigo = barcodeInterno!.Trim();
            if (codigo.Length != 13 || !SoDigitos(codigo) || codigo[0] != '2'
                || codigo.StartsWith("21", StringComparison.Ordinal)
                || CalcularDigitoEan13(codigo.Substring(0, 12)) != codigo[12] - '0')
                return null;
            return codigo.Substring(7, 5);
        }

        public static bool SuportaEtiquetaPeso(string? barcodeInterno) => ExtrairRefProduto(barcodeInterno) != null;

        public static bool TentarCalcularPreco(int precoQuiloCentavos, int pesoGramas, out int precoCentavos)
        {
            precoCentavos = 0;
            if (precoQuiloCentavos <= 0 || pesoGramas <= 0 || pesoGramas > PesoMaximoGramas)
                return false;
            decimal preco = decimal.Round(precoQuiloCentavos * (decimal)pesoGramas / 1000m,
                0, MidpointRounding.AwayFromZero);
            if (preco <= 0 || preco > PrecoMaximoCentavos) return false;
            precoCentavos = (int)preco;
            return true;
        }

        public static string? Gerar(string? barcodeInterno, int precoCentavos, int pesoGramas)
        {
            string? referencia = ExtrairRefProduto(barcodeInterno);
            if (referencia == null || precoCentavos <= 0 || precoCentavos > PrecoMaximoCentavos
                || pesoGramas <= 0 || pesoGramas > PesoMaximoGramas)
                return null;
            return Prefixo + referencia + precoCentavos.ToString("D5", CultureInfo.InvariantCulture)
                + pesoGramas.ToString("D7", CultureInfo.InvariantCulture);
        }

        public static bool TentarLer(string? codigo, out string refProduto, out int precoCentavos, out int pesoGramas)
        {
            refProduto = string.Empty;
            precoCentavos = 0;
            pesoGramas = 0;
            if (string.IsNullOrWhiteSpace(codigo)) return false;
            string etiqueta = codigo!.Trim();

            if (etiqueta.Length == 21 && etiqueta.StartsWith(Prefixo, StringComparison.Ordinal)
                && SoDigitos(etiqueta.Substring(4)))
            {
                int preco = int.Parse(etiqueta.Substring(9, 5), CultureInfo.InvariantCulture);
                int peso = int.Parse(etiqueta.Substring(14, 7), CultureInfo.InvariantCulture);
                if (preco <= 0 || peso <= 0) return false;
                refProduto = etiqueta.Substring(4, 5);
                precoCentavos = preco;
                pesoGramas = peso;
                return true;
            }

            // EAN21 antigo não guarda peso: reconhecer permite pedir reemissão sem inventar quantidade.
            if (etiqueta.Length != 13 || !etiqueta.StartsWith("21", StringComparison.Ordinal)
                || !SoDigitos(etiqueta)
                || CalcularDigitoEan13(etiqueta.Substring(0, 12)) != etiqueta[12] - '0')
                return false;
            int precoLegado = int.Parse(etiqueta.Substring(7, 5), CultureInfo.InvariantCulture);
            if (precoLegado <= 0) return false;
            refProduto = etiqueta.Substring(2, 5);
            precoCentavos = precoLegado;
            return true;
        }

        public static int CalcularDigitoEan13(string doze)
        {
            int soma = 0;
            for (int i = 0; i < 12; i++)
                soma += (doze[i] - '0') * (i % 2 == 0 ? 1 : 3);
            return (10 - soma % 10) % 10;
        }

        private static bool SoDigitos(string texto)
        {
            foreach (char c in texto) if (c < '0' || c > '9') return false;
            return true;
        }
    }
}
