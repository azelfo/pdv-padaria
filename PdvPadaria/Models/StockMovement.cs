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

        // Saldo do produto ANTES e DEPOIS deste movimento, na loja onde ele ocorreu.
        // Permite auditar sem recalcular: "tinha 275, saiu 30, ficou 245". É o que torna
        // possível conferir pães enviados x vendidos x dinheiro do caixa e detectar
        // desvio. Nulo em movimentos antigos, gravados antes destes campos existirem.
        public double? BalanceBefore { get; set; }
        public double? BalanceAfter { get; set; }
        
        // Controle de Sincronização
        public bool IsSynced { get; set; } = false;
        public DateTime? SyncedAt { get; set; }
    }
}
