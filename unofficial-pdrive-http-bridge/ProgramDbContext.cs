using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using unofficial_pdrive_http_bridge.DbModels;

namespace unofficial_pdrive_http_bridge;

public sealed class ProgramDbContext : DbContext
{
    public DbSet<Session> Sessions { get; set; }
    public DbSet<SessionScope> SessionScopes { get; set; }
    public DbSet<SecretsCacheSecret> SecretsCacheSecrets { get; set; }
    public DbSet<SecretsCacheGroup> SecretsCacheGroups { get; set; }

    public string DbPath { get; }

    // For design-time
    public ProgramDbContext()
    : this(":memory:")
    {
    }

    public ProgramDbContext(string dbPath)
    {
        DbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        var connectionString = new SqliteConnectionStringBuilder()
        {
            DataSource = DbPath,
            Pooling = true,
        }.ToString();
        options.UseSqlite(connectionString);
    }
}
