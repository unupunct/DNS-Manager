using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.Data;

/// <summary>Opens SQLite connections and applies ordered schema migrations tracked in PRAGMA user_version.</summary>
public sealed class SqliteDatabase(string path, ILogger<SqliteDatabase>? logger = null)
{
    private static readonly string[] Migrations =
    [
        // v1: providers + detected adapters
        """
        CREATE TABLE DNSProviders (
            Id TEXT PRIMARY KEY NOT NULL,
            Name TEXT NOT NULL,
            Description TEXT NOT NULL DEFAULT '',
            Website TEXT NOT NULL DEFAULT '',
            IPv4Primary TEXT, IPv4Secondary TEXT, IPv6Primary TEXT, IPv6Secondary TEXT,
            AdditionalInformation TEXT NOT NULL DEFAULT '',
            Privacy TEXT NOT NULL DEFAULT '',
            Security TEXT NOT NULL DEFAULT '',
            Filtering TEXT NOT NULL DEFAULT '',
            DohUrl TEXT, DotHost TEXT, DoqHost TEXT,
            IsBuiltIn INTEGER NOT NULL DEFAULT 0,
            SortOrder INTEGER NOT NULL DEFAULT 1000,
            UpdatedAt TEXT NOT NULL
        );
        CREATE INDEX IX_DNSProviders_Name ON DNSProviders (Name);
        CREATE TABLE NetworkAdapters (
            Id TEXT PRIMARY KEY NOT NULL,
            AdapterName TEXT NOT NULL,
            InterfaceDescription TEXT NOT NULL,
            MacAddress TEXT NOT NULL,
            LastDetectedAt TEXT NOT NULL
        );
        """,
    ];

    public static int LatestVersion => Migrations.Length;

    public string ConnectionString { get; } = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        ForeignKeys = true,
        Pooling = false,
    }.ToString();

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(ConnectionString);
        c.Open();
        return c;
    }

    public void Migrate()
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var c = Open();
        int current;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            current = Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        for (var v = current; v < Migrations.Length; v++)
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Migrations[v];
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"PRAGMA user_version = {v + 1};"; // integer literal, not user input
            cmd.ExecuteNonQuery();
            tx.Commit();
            logger?.LogInformation("Database migrated to version {Version}", v + 1);
        }
    }
}
