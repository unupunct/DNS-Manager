using System.Globalization;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Configuration;
using DNSManagerPro.Core.Diagnostics;
using DNSManagerPro.Core.History;
using DNSManagerPro.Core.Monitoring;
using Microsoft.Data.Sqlite;

namespace DNSManagerPro.Data;

public sealed class SqliteDiagnosticsRepository(SqliteDatabase db) : IDiagnosticsRepository
{
    public async Task SaveAsync(DiagnosticRun run, CancellationToken cancellationToken = default)
    {
        var problems = run.Findings.Where(f => f.Severity != FindingSeverity.Info).Select(f => f.Detected).ToList();
        var summary = problems.Count == 0 ? "No problems detected." : string.Join(" ", problems);
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO DiagnosticRuns (Id, StartedAt, FinishedAt, Overall, Summary, ReportText) VALUES ($id,$s,$f,$o,$sum,$r)";
        cmd.Parameters.AddWithValue("$id", run.Id.ToString());
        cmd.Parameters.AddWithValue("$s", run.StartedUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$f", run.FinishedUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$o", run.Overall.ToString());
        cmd.Parameters.AddWithValue("$sum", summary);
        cmd.Parameters.AddWithValue("$r", run.ReportText);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Steps and findings are not stored separately; the report text contains them.</summary>
    public async Task<IReadOnlyList<DiagnosticRun>> GetRecentAsync(int count, CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, StartedAt, FinishedAt, Overall, ReportText FROM DiagnosticRuns ORDER BY StartedAt DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", count);
        var list = new List<DiagnosticRun>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new DiagnosticRun(Guid.Parse(r.GetString(0)), Parse(r.GetString(1)), Parse(r.GetString(2)), [], [], Enum.Parse<DiagnosticOverall>(r.GetString(3)), r.GetString(4)));
        }

        return list;
    }

    internal static DateTime Parse(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

public sealed class SqliteMonitorRepository(SqliteDatabase db) : IMonitorRepository
{
    public async Task AddAsync(MonitorEvent e, CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO MonitorEvents (Timestamp, Kind, Message, Details) VALUES ($t,$k,$m,$d)";
        cmd.Parameters.AddWithValue("$t", e.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$k", e.Kind.ToString());
        cmd.Parameters.AddWithValue("$m", e.Message);
        cmd.Parameters.AddWithValue("$d", (object?)e.Details ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MonitorEvent>> GetRecentAsync(int count, CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Timestamp, Kind, Message, Details FROM MonitorEvents ORDER BY Id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", count);
        var list = new List<MonitorEvent>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new MonitorEvent(SqliteDiagnosticsRepository.Parse(r.GetString(0)), Enum.Parse<MonitorEventKind>(r.GetString(1)), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        }

        return list;
    }
}

/// <summary>Merges configuration history, benchmarks, diagnostics and monitoring events into one timeline.</summary>
public sealed class SqliteTimelineRepository(SqliteDatabase db) : ITimelineRepository
{
    public async Task<IReadOnlyList<TimelineEntry>> GetAsync(int perSourceLimit, CancellationToken cancellationToken = default)
    {
        var list = new List<TimelineEntry>();
        await using var c = db.Open();

        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, AdapterName, PreviousConfiguration, NewConfiguration, Operation, Result, Message, Timestamp FROM DNSHistory ORDER BY Id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", perSourceLimit);
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var op = r.GetString(4);
                var result = r.GetString(5);
                var prev = SafeDescribe(r.GetString(2));
                var title = (op, result) switch
                {
                    (_, "RolledBack") => "DNS change rolled back",
                    (_, "Failed") => "DNS change failed",
                    (_, "Pending") => "DNS change did not complete",
                    ("Restore", _) => "DNS settings restored",
                    _ => "DNS settings changed",
                };
                var detail = $"Before: {prev}{Environment.NewLine}After: {r.GetString(3)}" + (r.IsDBNull(6) ? string.Empty : $"{Environment.NewLine}{r.GetString(6)}");
                var status = result switch { "Success" => TimelineStatus.Success, "Pending" => TimelineStatus.Warning, _ => TimelineStatus.Failed };
                list.Add(new TimelineEntry(new TimelineKey(TimelineKind.ConfigChange, r.GetInt64(0).ToString(CultureInfo.InvariantCulture)), SqliteDiagnosticsRepository.Parse(r.GetString(7)), title, detail, status, r.GetString(1)));
            }
        }

        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT r.Id, r.StartedAt, r.Cancelled, (SELECT COUNT(1) FROM BenchmarkResults x WHERE x.RunId = r.Id),
                       (SELECT COUNT(DISTINCT TargetName || ServerAddress || Transport) FROM BenchmarkResults x WHERE x.RunId = r.Id)
                FROM BenchmarkRuns r ORDER BY r.StartedAt DESC LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$n", perSourceLimit);
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var cancelled = r.GetInt32(2) != 0;
                list.Add(new TimelineEntry(new TimelineKey(TimelineKind.Benchmark, r.GetString(0)), SqliteDiagnosticsRepository.Parse(r.GetString(1)),
                    cancelled ? "DNS benchmark (stopped early)" : "DNS benchmark",
                    $"{r.GetInt32(3)} queries to {r.GetInt32(4)} server/transport combinations.", cancelled ? TimelineStatus.Warning : TimelineStatus.Success));
            }
        }

        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, StartedAt, Overall, Summary FROM DiagnosticRuns ORDER BY StartedAt DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", perSourceLimit);
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var overall = r.GetString(2);
                list.Add(new TimelineEntry(new TimelineKey(TimelineKind.Diagnostic, r.GetString(0)), SqliteDiagnosticsRepository.Parse(r.GetString(1)),
                    $"Diagnostics: {overall}", r.GetString(3), overall switch { "Healthy" => TimelineStatus.Success, "Warnings" => TimelineStatus.Warning, _ => TimelineStatus.Failed }));
            }
        }

        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Timestamp, Kind, Message, Details FROM MonitorEvents ORDER BY Id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", perSourceLimit);
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var kind = r.GetString(2);
                var status = kind switch { "ResolutionFailure" => TimelineStatus.Failed, "Recovered" => TimelineStatus.Success, "ConfigChanged" => TimelineStatus.Warning, _ => TimelineStatus.Info };
                list.Add(new TimelineEntry(new TimelineKey(TimelineKind.Monitoring, r.GetInt64(0).ToString(CultureInfo.InvariantCulture)), SqliteDiagnosticsRepository.Parse(r.GetString(1)),
                    r.GetString(3), r.IsDBNull(4) ? string.Empty : r.GetString(4), status));
            }
        }

        return [.. list.OrderByDescending(e => e.TimestampUtc)];
    }

    public async Task<int> DeleteAsync(IEnumerable<TimelineKey> keys, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        await using var c = db.Open();
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var k in keys)
        {
            var table = k.Kind switch
            {
                TimelineKind.ConfigChange => "DNSHistory",
                TimelineKind.Benchmark => "BenchmarkRuns",
                TimelineKind.Diagnostic => "DiagnosticRuns",
                _ => "MonitorEvents",
            };
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE Id=$id"; // table name comes from a fixed switch, never from input
            cmd.Parameters.AddWithValue("$id", k.Kind is TimelineKind.ConfigChange or TimelineKind.Monitoring ? long.Parse(k.Id, CultureInfo.InvariantCulture) : k.Id);
            removed += await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    private static string SafeDescribe(string json)
    {
        try
        {
            return DnsSnapshot.FromJson(json)?.Describe() ?? "unknown";
        }
        catch (System.Text.Json.JsonException)
        {
            return "unknown";
        }
    }
}
