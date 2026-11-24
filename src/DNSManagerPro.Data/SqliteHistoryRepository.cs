using System.Globalization;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Configuration;

namespace DNSManagerPro.Data;

public sealed class SqliteHistoryRepository(SqliteDatabase db) : IDnsHistoryRepository
{
    private const string Cols = "Id, AdapterId, AdapterName, PreviousConfiguration, NewConfiguration, Operation, Result, Message, Timestamp";

    public async Task<long> BeginAsync(string adapterId, string adapterName, DnsSnapshot previous, string newDescription, string operation, CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DNSHistory (AdapterId, AdapterName, PreviousConfiguration, NewConfiguration, Operation, Result, Timestamp)
            VALUES ($a, $n, $prev, $new, $op, 'Pending', $ts);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$a", adapterId);
        cmd.Parameters.AddWithValue("$n", adapterName);
        cmd.Parameters.AddWithValue("$prev", previous.ToJson());
        cmd.Parameters.AddWithValue("$new", newDescription);
        cmd.Parameters.AddWithValue("$op", operation);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task CompleteAsync(long id, string result, string? message, CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE DNSHistory SET Result=$r, Message=$m WHERE Id=$id";
        cmd.Parameters.AddWithValue("$r", result);
        cmd.Parameters.AddWithValue("$m", (object?)message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DnsHistoryEntry>> GetRecentAsync(int count, CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM DNSHistory ORDER BY Id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", count);
        return await ReadAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DnsHistoryEntry?> GetLastSuccessfulChangeAsync(string adapterId, CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM DNSHistory WHERE AdapterId=$a AND Result='Success' ORDER BY Id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$a", adapterId);
        var rows = await ReadAsync(cmd, cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    private static async Task<IReadOnlyList<DnsHistoryEntry>> ReadAsync(Microsoft.Data.Sqlite.SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<DnsHistoryEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new DnsHistoryEntry(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                DateTime.Parse(r.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return list;
    }
}

