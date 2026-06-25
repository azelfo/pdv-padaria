using SQLite;

namespace PdvPadaria.Models
{
    /// <summary>
    /// Marca, na máquina da loja, os ajustes de estoque do DONO que já foram aplicados
    /// localmente. Garante que cada ajuste vindo da nuvem seja aplicado uma única vez
    /// (idempotência), sem reverter vendas offline a cada pull.
    /// </summary>
    public class AppliedOwnerAdjustment
    {
        [PrimaryKey]
        public string Id { get; set; } = string.Empty;
    }
}
