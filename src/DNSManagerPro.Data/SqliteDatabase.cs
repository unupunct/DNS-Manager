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
        // v2: DNS change history / backups
        """
        CREATE TABLE DNSHistory (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            AdapterId TEXT NOT NULL,
            AdapterName TEXT NOT NULL,
            PreviousConfiguration TEXT NOT NULL,
            NewConfiguration TEXT NOT NULL,
            Operation TEXT NOT NULL,
            Result TEXT NOT NULL DEFAULT 'Pending',
            Message TEXT,
            Timestamp TEXT NOT NULL
        );
        CREATE INDEX IX_DNSHistory_Adapter ON DNSHistory (AdapterId, Id);
        """,
        // v3: benchmark runs and raw results
        """
        CREATE TABLE BenchmarkRuns (
            Id TEXT PRIMARY KEY NOT NULL,
            StartedAt TEXT NOT NULL,
            FinishedAt TEXT NOT NULL,
            OptionsJson TEXT NOT NULL,
            TargetsJson TEXT NOT NULL,
            Cancelled INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE BenchmarkResults (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId TEXT NOT NULL REFERENCES BenchmarkRuns(Id) ON DELETE CASCADE,
            ProviderId TEXT,
            TargetName TEXT NOT NULL,
            ServerAddress TEXT NOT NULL,
            Transport TEXT NOT NULL,
            Domain TEXT NOT NULL,
            QueryType TEXT NOT NULL,
            ResponseTime REAL NOT NULL,
            Success INTEGER NOT NULL,
            TimedOut INTEGER NOT NULL,
            ResponseCode TEXT NOT NULL,
            Attempts INTEGER NOT NULL,
            Timestamp TEXT NOT NULL
        );
        CREATE INDEX IX_BenchmarkResults_Run ON BenchmarkResults (RunId);
        CREATE INDEX IX_BenchmarkResults_Provider ON BenchmarkResults (ProviderId, Timestamp);
        """,
        // v4: DNS profiles
        """
        CREATE TABLE DNSProfiles (
            Id TEXT PRIMARY KEY NOT NULL,
            Name TEXT NOT NULL,
            Description TEXT NOT NULL DEFAULT '',
            Category TEXT NOT NULL DEFAULT 'Custom',
            ProviderId TEXT REFERENCES DNSProviders(Id) ON DELETE SET NULL,
            IsAutomatic INTEGER NOT NULL DEFAULT 0,
            IPv4Primary TEXT, IPv4Secondary TEXT, IPv6Primary TEXT, IPv6Secondary TEXT,
            DohUrl TEXT, DotHost TEXT, DoqHost TEXT,
            AdapterId TEXT, AdapterName TEXT,
            Tags TEXT NOT NULL DEFAULT '',
            Notes TEXT NOT NULL DEFAULT '',
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            LastAppliedAt TEXT
        );
        CREATE INDEX IX_DNSProfiles_Name ON DNSProfiles (Name);
        CREATE INDEX IX_DNSProfiles_Provider ON DNSProfiles (ProviderId);
        """,
        // v5: diagnostics and monitoring
        """
        CREATE TABLE DiagnosticRuns (
            Id TEXT PRIMARY KEY NOT NULL,
            StartedAt TEXT NOT NULL,
            FinishedAt TEXT NOT NULL,
            Overall TEXT NOT NULL,
            Summary TEXT NOT NULL,
            ReportText TEXT NOT NULL
        );
        CREATE INDEX IX_DiagnosticRuns_Started ON DiagnosticRuns (StartedAt);
        CREATE TABLE MonitorEvents (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Timestamp TEXT NOT NULL,
            Kind TEXT NOT NULL,
            Message TEXT NOT NULL,
            Details TEXT
        );
        CREATE INDEX IX_MonitorEvents_Time ON MonitorEvents (Timestamp);
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
