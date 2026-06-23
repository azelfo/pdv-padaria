using SQLite;
using System;
using System.IO;
using System.Collections.Generic;
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
            
            // Inicializa a conexão assíncrona do SQLite com suporte a transações multi-thread
            _database = new SQLiteAsyncConnection(DbPath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);
        }

        // Executa a inicialização das tabelas de forma assíncrona
        public async Task InitializeAsync()
        {
            try
            {
                // Cria as tabelas se elas não existirem no dev.db local
                await _database.CreateTableAsync<User>();
                await _database.CreateTableAsync<Category>();
                await _database.CreateTableAsync<Product>();
                await _database.CreateTableAsync<Sale>();
                await _database.CreateTableAsync<SaleItem>();
                await _database.CreateTableAsync<BreadConfig>();
                await _database.CreateTableAsync<StockMovement>();

                // Obtém valores reais do .env com fallbacks de teste
                string tenantId = EnvService.Get("TENANT_ID", "tenant-test");
                string storeId = EnvService.Get("STORE_ID", "store-test");

                // Seed inicial para garantir que o usuário dono consiga logar offline na primeira vez
                var userCount = await _database.Table<User>().CountAsync();
                if (userCount == 0)
                {
                    await _database.InsertAsync(new User
                    {
                        Id = "local-admin",
                        Name = "Marcelo Dono",
                        Email = "dono@padaria.com.br",
                        Password = PasswordHasher.Hash("123"), // hash BCrypt; troque a senha no 1o login
                        Role = "DONO",
                        TenantId = tenantId,
                        StoreId = storeId,
                        Active = true
                    });
                }

                // Seed de Categoria
                var catCount = await _database.Table<Category>().CountAsync();
                if (catCount == 0)
                {
                    await _database.InsertAsync(new Category
                    {
                        Id = "cat-producao-propria",
                        Name = "Produção Própria",
                        TenantId = tenantId
                    });
                }

                // Seed do Produto Pão Francês Quente (Legado)
                var prodCount = await _database.Table<Product>().CountAsync();
                if (prodCount == 0)
                {
                    await _database.InsertAsync(new Product
                    {
                        Id = "prod-pao-frances",
                        Name = "Pão Francês Quente",
                        Barcode = "100000000001",
                        PriceSale = 50,
                        PriceCost = 15,
                        Type = "PAO_FRANCES",
                        UnitMeasure = "UN",
                        CategoryId = "cat-producao-propria",
                        TenantId = tenantId,
                        LocalStockQuantity = 0,
                        Active = false // Desativado
                    });
                }

                // Migração / Seed garantido para Pão Carioca e Pão Massa Fina
                var oldFrench = await _database.Table<Product>().Where(p => p.Id == "prod-pao-frances").FirstOrDefaultAsync();
                if (oldFrench != null && oldFrench.Active)
                {
                    oldFrench.Active = false;
                    await _database.UpdateAsync(oldFrench);
                }

                var paoCarioca = await _database.Table<Product>().Where(p => p.Id == "prod-pao-carioca").FirstOrDefaultAsync();
                if (paoCarioca == null)
                {
                    await _database.InsertAsync(new Product
                    {
                        Id = "prod-pao-carioca",
                        Name = "Pão Carioca",
                        Barcode = "100000000002",
                        PriceSale = 50,
                        PriceCost = 15,
                        Type = "PAO_FRANCES",
                        UnitMeasure = "UN",
                        CategoryId = "cat-producao-propria",
                        TenantId = tenantId,
                        LocalStockQuantity = 300,
                        Active = true
                    });
                }

                var paoMassaFina = await _database.Table<Product>().Where(p => p.Id == "prod-pao-massa-fina").FirstOrDefaultAsync();
                if (paoMassaFina == null)
                {
                    await _database.InsertAsync(new Product
                    {
                        Id = "prod-pao-massa-fina",
                        Name = "Pão Massa Fina",
                        Barcode = "100000000003",
                        PriceSale = 50,
                        PriceCost = 15,
                        Type = "PAO_FRANCES",
                        UnitMeasure = "UN",
                        CategoryId = "cat-producao-propria",
                        TenantId = tenantId,
                        LocalStockQuantity = 300,
                        Active = true
                    });
                }

                // Seed do BreadConfig
                var configCount = await _database.Table<BreadConfig>().CountAsync();
                if (configCount == 0)
                {
                    await _database.InsertAsync(new BreadConfig
                    {
                        Id = "config-pao-test",
                        StoreId = storeId,
                        PriceUnit = 50,
                        Brackets = "[{\"ate\": 50, \"qtd\": 1}, {\"ate\": 100, \"qtd\": 3}, {\"ate\": 150, \"qtd\": 4}, {\"ate\": 200, \"qtd\": 6}, {\"ate\": 250, \"qtd\": 7}, {\"ate\": 300, \"qtd\": 9}, {\"ate\": 500, \"qtd\": 15}, {\"ate\": 1000, \"qtd\": 30}]",
                        Active = true
                    });
                }
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
            return new SQLiteConnection(DbPath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);
        }

        #region Métodos de Conveniência (Operações Básicas)

        public async Task<List<T>> GetAllAsync<T>() where T : new()
        {
            return await _database.Table<T>().ToListAsync();
        }

        public async Task<T> GetByIdAsync<T>(string id) where T : new()
        {
            return await _database.FindAsync<T>(id);
        }

        public async Task<int> InsertAsync<T>(T entity)
        {
            return await _database.InsertAsync(entity);
        }

        public async Task<int> UpdateAsync<T>(T entity)
        {
            return await _database.UpdateAsync(entity);
        }

        public async Task<int> DeleteAsync<T>(T entity)
        {
            return await _database.DeleteAsync(entity);
        }

        public async Task RunInTransactionAsync(Action<SQLiteConnection> action)
        {
            await _database.RunInTransactionAsync(action);
        }

        #endregion
    }
}
