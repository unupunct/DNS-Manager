using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.Data;

public sealed record MaintenanceResult(bool Success, string Message);

/// <summary>Backup, validated restore and history clearing for the local SQLite database.</summary>
public sealed class DatabaseMaintenance(SqliteDatabase db, string databasePath, ILogger<DatabaseMaintenance>? logger = null)
{
    private static readonly string[] RequiredTables = ["DNSProviders", "DNSHistory", "BenchmarkRuns", "DNSProfiles"];

    /// <summary>Writes a consistent copy of the live database (VACUUM INTO) to <paramref name="destination"/>.</summary>
    public async Task<MaintenanceResult> BackupAsync(string destination, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(destination);
        if (string.Equals(full, Path.GetFullPath(databasePath), StringComparison.OrdinalIgnoreCase))
        {
            return new MaintenanceResult(false, "Choose a different file than the live database.");
        }

        // VACUUM INTO refuses to overwrite, so build in a fresh temp file beside the target and then replace atomically.
        var tmp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await using (var c = db.Open())
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "VACUUM INTO $p";
                cmd.Parameters.AddWithValue("$p", tmp);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tmp, full, overwrite: true);
            return new MaintenanceResult(true, "Backup saved.");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger?.LogError(ex, "Database backup failed");
            TryDelete(tmp);
            return new MaintenanceResult(false, "Backup failed: " + ex.Message);
        }
    }

    /// <summary>Checks that a file is an intact DNS Manager Pro database that this version can open. Never modifies the file.</summary>
    public static MaintenanceResult Validate(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return new MaintenanceResult(false, "The file does not exist.");
            }

            var cs = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            using var c = new SqliteConnection(cs);
            c.Open();
            var integrity = Scalar(c, "PRAGMA integrity_check;");
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new MaintenanceResult(false, "The file is damaged (integrity check failed).");
            }

            var version = int.Parse(Scalar(c, "PRAGMA user_version;") ?? "0", CultureInfo.InvariantCulture);
            if (version < 1)
            {
                return new MaintenanceResult(false, "This is not a DNS Manager Pro database.");
            }

            if (version > SqliteDatabase.LatestVersion)
            {
                return new MaintenanceResult(false, "This backup was made by a newer version of DNS Manager Pro.");
            }

            foreach (var t in RequiredTables)
            {
                if (Scalar(c, "SELECT name FROM sqlite_master WHERE type='table' AND name='" + t + "';") is null)
                {
                    return new MaintenanceResult(false, "This is not a DNS Manager Pro database (missing table " + t + ").");
                }
            }

            return new MaintenanceResult(true, "Valid backup.");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or FormatException)
        {
            return new MaintenanceResult(false, "The file is not a usable database: " + ex.Message);
        }
    }

    /// <summary>
    /// Replaces the live database with a validated backup. The current database is copied to
    /// <paramref name="safetyBackupDirectory"/> first, so a restore can itself be undone. The caller should restart the app afterwards.
    /// </summary>
    public MaintenanceResult Restore(string backupFile, string safetyBackupDirectory)
    {
        var check = Validate(backupFile);
        if (!check.Success)
        {
            return check;
        }

        if (string.Equals(Path.GetFullPath(backupFile), Path.GetFullPath(databasePath), StringComparison.OrdinalIgnoreCase))
        {
            return new MaintenanceResult(false, "That is already the live database.");
        }

        var staged = databasePath + ".restore.tmp";
        try
        {
            Directory.CreateDirectory(safetyBackupDirectory);
            var safety = Path.Combine(safetyBackupDirectory, $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            if (File.Exists(databasePath))
            {
                File.Copy(databasePath, safety, overwrite: false);
            }

            File.Copy(backupFile, staged, overwrite: true);
            File.Move(staged, databasePath, overwrite: true);
            db.Migrate(); // an older backup is upgraded in place
            return new MaintenanceResult(true, "Database restored. Your previous data was kept in " + safetyBackupDirectory + ".");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger?.LogError(ex, "Database restore failed");
            TryDelete(staged);
            return new MaintenanceResult(false, "Restore failed, current data left in place: " + ex.Message);
        }
    }

    /// <summary>Deletes DNS change history (and the backups it carries), benchmarks, diagnostic runs and monitoring events. Providers and profiles are kept.</summary>
    public async Task<MaintenanceResult> ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var c = db.Open();
            await using var tx = c.BeginTransaction();
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM DNSHistory; DELETE FROM BenchmarkResults; DELETE FROM BenchmarkRuns; DELETE FROM DiagnosticRuns; DELETE FROM MonitorEvents;";
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new MaintenanceResult(true, "History cleared.");
        }
        catch (SqliteException ex)
        {
            logger?.LogError(ex, "Clearing history failed");
            return new MaintenanceResult(false, "Could not clear history: " + ex.Message);
        }
    }

    private static string? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql; // constant strings only
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture); // Convert.ToString(null) would give "" and hide a missing row
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}
