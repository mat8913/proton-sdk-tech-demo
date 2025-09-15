using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using unofficial_pdrive_http_bridge.DbModels;

namespace unofficial_pdrive_http_bridge;

public sealed class ProgramDbContext : DbContext
{
    private readonly ILoggerFactory? _loggerFactory;

    public DbSet<Session> Sessions { get; set; }
    public DbSet<SessionScope> SessionScopes { get; set; }
    public DbSet<SecretsCacheSecret> SecretsCacheSecrets { get; set; }
    public DbSet<SecretsCacheGroup> SecretsCacheGroups { get; set; }
    public DbSet<WebUiPassword> WebUiPasswords { get; set; }

    public string DbPath { get; }

    // For design-time
    public ProgramDbContext()
    : this(null, ":memory:")
    {
    }

    public ProgramDbContext(ILoggerFactory? loggerFactory, string dbPath)
    {
        _loggerFactory = loggerFactory;
        DbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        var connectionString = new SqliteConnectionStringBuilder()
        {
            DataSource = DbPath,
            Pooling = true,
        }.ToString();
        options
            .UseSqlite(connectionString)
            .UseLoggerFactory(_loggerFactory);
    }
}
