using System;

namespace PdvPadaria.Services
{
    /// <summary>
    /// Código de barras de PESO com preço embutido (mesmo truque que supermercado usa
    /// na balança etiquetadora, aqui feito a partir do peso digitado à mão).
    ///
    /// Formato (13 dígitos, EAN-13 válido):  "21" + PPPPP + VVVVV + C
    ///   21    -> prefixo que marca "etiqueta pesada". Não colide com o código interno
    ///            de unidade (gerar_codigo_interno gera "2" + sequência de 11 dígitos,
    ///            que na prática sempre sai como "20..."), nem com produto de fábrica
    ///            (no Brasil sempre 789/790).
    ///   PPPPP -> últimos 5 dígitos da sequência do código interno do produto. Serve
    ///            para o caixa saber QUAL produto é, sem precisar de coluna nova no banco.
    ///   VVVVV -> preço já calculado, em centavos (até 99999 = R$ 999,99).
    ///   C     -> dígito verificador EAN-13.
    ///
    /// O preço vai embutido (e não o peso) de propósito: a etiqueta impressa mostra
    /// R$ X ao cliente, e o caixa cobra exatamente esse valor mesmo que o preço por
    /// quilo mude entre a pesagem e o pagamento.
    /// </summary>
    public static class WeightBarcodeService
    {
        public const string Prefixo = "21";
        public const int PrecoMaximoCentavos = 99999; // R$ 999,99

        /// <summary>
        /// Extrai os 5 dígitos que identificam o produto a partir do código interno dele
        /// ("2" + sequência de 11 + verificador). Retorna null se o produto não tiver um
        /// código interno válido (ex.: código de fábrica ou código curto digitado à mão).
        /// </summary>
        public static string? ExtrairRefProduto(string? barcodeInterno)
        {
            if (string.IsNullOrWhiteSpace(barcodeInterno)) return null;
            // "!" porque no .NET Framework o IsNullOrWhiteSpace não carrega a anotação
            // [NotNullWhen(false)] que o compilador usa para saber que aqui não é nulo.
            var b = barcodeInterno!.Trim();
            if (b.Length != 13 || !SoDigitos(b)) return null;
            if (b[0] != '2') return null;
            if (b.StartsWith(Prefixo)) return null; // já é uma etiqueta de peso, não um produto

            // "2" + sequência(11) + verificador(1) -> pega os últimos 5 da sequência
            string sequencia = b.Substring(1, 11);
            return sequencia.Substring(sequencia.Length - 5);
        }

        /// <summary>True se este produto pode receber etiqueta de peso (tem código interno).</summary>
        public static bool SuportaEtiquetaPeso(string? barcodeInterno) => ExtrairRefProduto(barcodeInterno) != null;

        /// <summary>
        /// Monta o código de barras da etiqueta de peso. Retorna null se o produto não
        /// tiver código interno ou se o preço estiver fora da faixa representável.
        /// </summary>
        public static string? Gerar(string? barcodeInterno, int precoCentavos)
        {
            var refProduto = ExtrairRefProduto(barcodeInterno);
            if (refProduto == null) return null;
            if (precoCentavos <= 0 || precoCentavos > PrecoMaximoCentavos) return null;

            string doze = Prefixo + refProduto + precoCentavos.ToString("D5");
            return doze + CalcularDigitoEan13(doze).ToString();
        }

        /// <summary>
        /// Tenta interpretar um código lido no caixa como etiqueta de peso.
        /// Só aceita se for 13 dígitos, começar com "21" e o verificador bater —
        /// assim um código de fábrica nunca é confundido com etiqueta de peso.
        /// </summary>
        public static bool TentarLer(string? codigo, out string refProduto, out int precoCentavos)
        {
            refProduto = string.Empty;
            precoCentavos = 0;

            if (string.IsNullOrWhiteSpace(codigo)) return false;
            var c = codigo!.Trim();
            if (c.Length != 13 || !SoDigitos(c)) return false;
            if (!c.StartsWith(Prefixo)) return false;
            if (CalcularDigitoEan13(c.Substring(0, 12)) != (c[12] - '0')) return false;

            refProduto = c.Substring(2, 5);
            if (!int.TryParse(c.Substring(7, 5), out precoCentavos)) return false;
            return precoCentavos > 0;
        }

        /// <summary>Dígito verificador EAN-13 sobre os 12 primeiros dígitos (mesma regra do servidor).</summary>
        public static int CalcularDigitoEan13(string doze)
        {
            int soma = 0;
            for (int i = 0; i < 12; i++)
            {
                int d = doze[i] - '0';
                soma += (i % 2 == 0) ? d : d * 3;
            }
            return (10 - (soma % 10)) % 10;
        }

        private static bool SoDigitos(string s)
        {
            foreach (var ch in s) if (ch < '0' || ch > '9') return false;
            return true;
        }
    }
}
