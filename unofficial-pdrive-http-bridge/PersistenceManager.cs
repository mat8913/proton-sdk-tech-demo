using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace unofficial_pdrive_http_bridge;

public sealed class PersistenceManager
{
    private readonly string _connectionString;

    public PersistenceManager(string dbFilePath)
    {
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

        return new ProgramDbContext(efDbFile);
    }
}
