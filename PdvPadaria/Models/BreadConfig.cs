using SQLite;
using System;

namespace PdvPadaria.Models
{
    public class BreadConfig
    {
        [PrimaryKey]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string StoreId { get; set; } = string.Empty;
        public int PriceUnit { get; set; } // preço unitário em centavos (ex: R$0,50 = 50)
        public string Brackets { get; set; } = string.Empty; // Faixas salvas em JSON string
        public bool Active { get; set; } = true;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
