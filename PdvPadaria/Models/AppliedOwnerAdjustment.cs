using SQLite;

namespace PdvPadaria.Models
{
    /// <summary>
    /// Marca, na maquina da loja, os ajustes de estoque do DONO que ja foram aplicados.
    /// A release-ponte 1.1.7 ainda usa o protocolo legado de snapshot.
    /// </summary>
    public class AppliedOwnerAdjustment
    {
        [PrimaryKey]
        public string Id { get; set; } = string.Empty;
    }
}
