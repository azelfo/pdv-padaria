using SQLite;
using System;

namespace PdvPadaria.Models
{
    public class Product
    {
        [PrimaryKey]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string? Barcode { get; set; }
        public int PriceSale { get; set; } // Em centavos
        public int PriceCost { get; set; } // Em centavos
        public string Type { get; set; } = "NORMAL"; // "NORMAL", "PAO_FRANCES", "SALGADO", "BOLO"
        public string UnitMeasure { get; set; } = "UN"; // "UN", "KG", "FATIA", "MEIA_DUZIA"
        public bool Active { get; set; } = true;
        public string? ImageUrl { get; set; }
        public string CategoryId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        
        // Quantidade em estoque local da loja atual
        public double LocalStockQuantity { get; set; } = 0.0;
        public double MinStock { get; set; } = 0.0;
        
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
