using SQLite;
using System;

namespace PdvPadaria.Models
{
    public class StockMovement
    {
        [PrimaryKey]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ProductId { get; set; } = string.Empty;
        public string StoreId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string Type { get; set; } = "SAIDA"; // "ENTRADA", "SAIDA", "AJUSTE"
        public double Quantity { get; set; }
        public string Reason { get; set; } = "VENDA"; // "VENDA", "REPOSICAO", "PERDA", "AJUSTE_MANUAL"
        public string? SaleId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        
        // Controle de Sincronização
        public bool IsSynced { get; set; } = false;
        public DateTime? SyncedAt { get; set; }
    }
}
