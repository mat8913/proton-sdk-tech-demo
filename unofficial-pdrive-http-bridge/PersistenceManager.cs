using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace unofficial_pdrive_http_bridge;

public sealed class PersistenceManager
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _connectionString;

    public PersistenceManager(ILoggerFactory loggerFactory, string dbFilePath)
    {
        _loggerFactory = loggerFactory;
        _connectionString = new SqliteConnectionStringBuilder()
        {
            DataSource = dbFilePath,
            Pooling = true,
        }.ToString();
    }

    public SqliteConnection GetSqlConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    public ProgramDbContext GetProgramDbContext()
    {
        // TODO: Use dbFilePath
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        var dataDir = Path.Join(appData, "unofficial-pdrive-http-bridge");
        var efDbFile = Path.Join(dataDir, "data_ef.db");

        return new ProgramDbContext(_loggerFactory, efDbFile);
    }
}
