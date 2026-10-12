using System.Globalization;
using System.Text.RegularExpressions;

namespace PdvPadaria.Services
{
    public static class MoneyInput
    {
        public static bool TentarLerCentavos(string? texto, out int centavos)
        {
            centavos = 0;
            if (string.IsNullOrWhiteSpace(texto)) return false;
            string valor = texto!.Trim();
            if (valor.StartsWith("R$")) valor = valor.Substring(2).Trim();

            // Aceita reais em pt-BR, sem arredondar centavos nem interpretar sinais/expoentes.
            if (!Regex.IsMatch(valor, @"\A(?:[0-9]+|[0-9]{1,3}(?:\.[0-9]{3})+)(?:,[0-9]{1,2})?\z"))
                return false;
            if (!decimal.TryParse(valor, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands,
                    CultureInfo.GetCultureInfo("pt-BR"), out decimal reais)
                || reais > int.MaxValue / 100m)
                return false;

            centavos = (int)(reais * 100m);
            return true;
        }
    }
}
