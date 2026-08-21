using SQLite;
using System;
using System.IO;
using System.Threading.Tasks;
using PdvPadaria.Models;

namespace PdvPadaria.Services
{
    public class DatabaseService
    {
        private readonly SQLiteAsyncConnection _database;
        public static string DbPath { get; private set; } = string.Empty;

        public DatabaseService()
        {
            // Inicializa as baterias do SQLitePCL para garantir o carregamento correto da DLL nativa no Windows
            SQLitePCL.Batteries_V2.Init();

            // Define o diretório AppData idêntico ao anterior para manter consistência dos dados do usuário
            var appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pdv-padaria");
            
            if (!Directory.Exists(appDataFolder))
            {
                Directory.CreateDirectory(appDataFolder);
            }

            DbPath = Path.Combine(appDataFolder, "dev.db");

            // Conexão assíncrona principal (vendas). SEM SharedCache de propósito: com WAL,
            // conexões separadas já leem/escrevem concorrentemente. SharedCache transforma o
            // lock em SQLITE_LOCKED (nível de tabela), que o BusyTimeout NÃO aguarda — fonte
            // de erro intermitente "database is locked" se uma venda coincidir com o sync.
            _database = new SQLiteAsyncConnection(DbPath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create);
        }

        // Executa a inicialização das tabelas de forma assíncrona
        public async Task InitializeAsync()
        {
            try
            {
                // WAL + busy_timeout: permite a conexão de vendas e a de sincronização lerem/escreverem
                // concorrentemente sem "database is locked" nem corromper o arquivo.
                // Ambos PRAGMAs retornam UMA linha → ler com ExecuteScalarAsync.
                // (ExecuteAsync lança "not an error" no sqlite-net porque recebe um SQLITE_ROW inesperado.)
                await _database.ExecuteScalarAsync<string>("PRAGMA journal_mode=WAL");
                await _database.ExecuteScalarAsync<int>("PRAGMA busy_timeout=5000");

                // Cria as tabelas se elas não existirem no dev.db local
                await _database.CreateTableAsync<User>();
                await _database.CreateTableAsync<Category>();
                await _database.CreateTableAsync<Product>();
                await _database.CreateTableAsync<Sale>();
                await _database.CreateTableAsync<SaleItem>();
                await _database.CreateTableAsync<BreadConfig>();
                await _database.CreateTableAsync<StockMovement>();
                await _database.CreateTableAsync<AppliedOwnerAdjustment>();

                // Versões antigas criavam dados de demonstração no banco real. Não
                // apagamos as linhas, pois vendas antigas podem referenciá-las; apenas
                // desativamos os IDs conhecidos e deixamos a nuvem fornecer dados reais.
                await _database.ExecuteAsync(
                    "UPDATE User SET Active = 0 WHERE Id = ? AND Email = ?",
                    "local-admin", "dono@padaria.com");
                await _database.ExecuteAsync(
                    "UPDATE Product SET Active = 0 WHERE Id IN (?, ?)",
                    "prod-pao-carioca", "prod-pao-massa-fina");
                await _database.ExecuteAsync(
                    "UPDATE BreadConfig SET Active = 0 WHERE Id = ?",
                    "config-pao-test");
                await _database.ExecuteAsync(
                    "DELETE FROM Category WHERE Id = ? AND TenantId = ? " +
                    "AND NOT EXISTS (SELECT 1 FROM Product WHERE CategoryId = ?)",
                    "cat-producao-propria", "tenant-test", "cat-producao-propria");
            }
            catch (Exception ex)
            {
                // Log ou tratamento de erro de inicialização
                System.Diagnostics.Debug.WriteLine($"[Prisma C# SQLite Error]: {ex.Message}");
                throw;
            }
        }

        // Getter para a conexão direta se necessário
        public SQLiteAsyncConnection GetConnection() => _database;

        // Getter para a conexão síncrona usada no serviço de sincronização
        public SQLiteConnection GetSyncConnection()
        {
            // Conexão de sincronização. Também SEM SharedCache (ver comentário no construtor):
            // WAL + BusyTimeout dá a concorrência segura entre esta conexão e a de vendas.
            var conn = new SQLiteConnection(DbPath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create);
            conn.BusyTimeout = TimeSpan.FromSeconds(5);
            return conn;
        }

        // Executa um bloco transacional atômico na conexão principal (usado na finalização
        // de venda, cancelamento e ajustes — tudo-ou-nada).
        public async Task RunInTransactionAsync(Action<SQLiteConnection> action)
        {
            await _database.RunInTransactionAsync(action);
        }
    }
}
