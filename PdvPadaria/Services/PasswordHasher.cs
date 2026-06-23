namespace PdvPadaria.Services
{
    /// <summary>
    /// Hash e verificacao de senha com BCrypt.
    /// Suporta migracao transparente: senhas legadas em texto puro (base
    /// antiga / Supabase ainda nao migrado) sao aceitas uma vez e devem ser
    /// re-gravadas como hash pelo chamador apos login bem-sucedido.
    /// </summary>
    public static class PasswordHasher
    {
        // work factor 12 = ~250ms/hash em hardware atual; bom equilibrio p/ PDV
        private const int WorkFactor = 12;

        /// <summary>Gera hash BCrypt de uma senha em texto puro.</summary>
        public static string Hash(string plain)
        {
            return BCrypt.Net.BCrypt.HashPassword(plain, WorkFactor);
        }

        /// <summary>True se a string ja for um hash BCrypt valido ($2a$/$2b$/$2y$).</summary>
        public static bool IsHashed(string? stored)
        {
            if (string.IsNullOrEmpty(stored) || stored.Length < 7) return false;
            return stored.StartsWith("$2a$") || stored.StartsWith("$2b$") || stored.StartsWith("$2y$");
        }

        /// <summary>
        /// Verifica senha. Se 'stored' for hash BCrypt, usa BCrypt.Verify
        /// (tempo constante). Se for texto puro legado, compara direto.
        /// Use NeedsUpgrade para decidir re-gravar como hash.
        /// </summary>
        public static bool Verify(string entered, string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return false;

            if (IsHashed(stored))
            {
                try { return BCrypt.Net.BCrypt.Verify(entered, stored); }
                catch { return false; }
            }

            // Legado em texto puro — aceita para permitir migracao no proximo login
            return entered == stored;
        }

        /// <summary>True se a senha conferiu mas ainda esta em texto puro (precisa virar hash).</summary>
        public static bool NeedsUpgrade(string? stored) => !IsHashed(stored);
    }
}
