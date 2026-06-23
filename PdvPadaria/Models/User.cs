using SQLite;
using System;

namespace PdvPadaria.Models
{
    public class User
    {
        [PrimaryKey]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        
        [Unique]
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty; // Hash de senha para login offline-first
        public string Role { get; set; } = "ATENDENTE"; // "ATENDENTE", "GERENTE", "DONO"
        public bool Active { get; set; } = true;
        public string TenantId { get; set; } = string.Empty;
        public string StoreId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
