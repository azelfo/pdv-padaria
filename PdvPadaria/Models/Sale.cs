using SQLite;
using System;

namespace PdvPadaria.Models
{
    public class Sale
    {
        [PrimaryKey]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string StoreId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public DateTime SaleDate { get; set; } = DateTime.Now;
        public int Subtotal { get; set; } // Em centavos
        public int Discount { get; set; } // Em centavos
        public int Total { get; set; } // Em centavos
        public string PaymentMethod { get; set; } = "PIX"; // "PIX", "CARTAO_DEBITO", "CARTAO_CREDITO", "DINHEIRO"
        public string PaymentStatus { get; set; } = "PENDENTE"; // "PENDENTE", "APROVADO", "NEGADO", "CANCELADO"
        public int? ReceivedAmount { get; set; } // Em centavos (dinheiro)
        public int? ChangeAmount { get; set; } // Em centavos (dinheiro)
        public string? Notes { get; set; }
        public string? ExternalTxId { get; set; }
        
        // Controle de Sincronização
        public bool IsSynced { get; set; } = false;
        public DateTime? SyncedAt { get; set; }
    }
}
