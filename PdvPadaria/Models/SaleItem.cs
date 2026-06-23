using SQLite;
using System;

namespace PdvPadaria.Models
{
    public class SaleItem
    {
        [PrimaryKey]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string SaleId { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
        public double Quantity { get; set; }
        public int PriceUnit { get; set; } // Em centavos
        public int Subtotal { get; set; } // Em centavos
        public string Type { get; set; } = "NORMAL";
        public string? Details { get; set; } // JSON string para detalhes adicionais (como peso, faixas de preço de pão)
    }
}
