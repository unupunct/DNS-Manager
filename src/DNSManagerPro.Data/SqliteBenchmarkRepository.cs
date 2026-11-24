using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Benchmark;
using Microsoft.Data.Sqlite;

namespace DNSManagerPro.Data;

public sealed class SqliteBenchmarkRepository(SqliteDatabase db) : IBenchmarkRepository
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private sealed record OptionsDto(string[] Domains, int Repetitions, double TimeoutMs, int Parallelism, CacheMode CacheMode, RecordKind RecordKind, int Retries);

    private sealed record TargetDto(string Name, string? ProviderId, string? Address, DnsTransport Transport, string? DohUrl);

    public async Task SaveAsync(BenchmarkRun run, CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO BenchmarkRuns (Id, StartedAt, FinishedAt, OptionsJson, TargetsJson, Cancelled) VALUES ($id,$s,$f,$o,$t,$c)";
            cmd.Parameters.AddWithValue("$id", run.Id.ToString());
            cmd.Parameters.AddWithValue("$s", Iso(run.StartedUtc));
            cmd.Parameters.AddWithValue("$f", Iso(run.FinishedUtc));
            var o = run.Options;
            cmd.Parameters.AddWithValue("$o", JsonSerializer.Serialize(new OptionsDto([.. o.Domains], o.Repetitions, o.Timeout.TotalMilliseconds, o.Parallelism, o.CacheMode, o.RecordKind, o.Retries), Json));
            cmd.Parameters.AddWithValue("$t", JsonSerializer.Serialize(run.Targets.Select(t => new TargetDto(t.Name, t.ProviderId, t.Address?.ToString(), t.Transport, t.DohUrl)), Json));
            cmd.Parameters.AddWithValue("$c", run.Cancelled ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var s in run.Samples)
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO BenchmarkResults (RunId, ProviderId, TargetName, ServerAddress, Transport, Domain, QueryType, ResponseTime, Success, TimedOut, ResponseCode, Attempts, Timestamp)
                VALUES ($r,$p,$n,$a,$tr,$d,$q,$ms,$ok,$to,$rc,$at,$ts)
                """;
            cmd.Parameters.AddWithValue("$r", run.Id.ToString());
            cmd.Parameters.AddWithValue("$p", (object?)s.ProviderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n", s.TargetName);
            cmd.Parameters.AddWithValue("$a", s.Server);
            cmd.Parameters.AddWithValue("$tr", s.Transport.ToString());
            cmd.Parameters.AddWithValue("$d", s.Domain);
            cmd.Parameters.AddWithValue("$q", s.Kind.ToString());
            cmd.Parameters.AddWithValue("$ms", s.Milliseconds);
            cmd.Parameters.AddWithValue("$ok", s.Success ? 1 : 0);
            cmd.Parameters.AddWithValue("$to", s.TimedOut ? 1 : 0);
            cmd.Parameters.AddWithValue("$rc", s.ResponseCode);
            cmd.Parameters.AddWithValue("$at", s.Attempts);
            cmd.Parameters.AddWithValue("$ts", Iso(s.TimestampUtc));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BenchmarkRunInfo>> GetRunsAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT r.Id, r.StartedAt, r.FinishedAt, r.TargetsJson, r.Cancelled, (SELECT COUNT(1) FROM BenchmarkResults x WHERE x.RunId = r.Id)
            FROM BenchmarkRuns r ORDER BY r.StartedAt DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<BenchmarkRunInfo>();
        await using var rd = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rd.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var targets = JsonSerializer.Deserialize<List<TargetDto>>(rd.GetString(3), Json) ?? [];
            list.Add(new BenchmarkRunInfo(Guid.Parse(rd.GetString(0)), Parse(rd.GetString(1)), Parse(rd.GetString(2)), targets.Count, rd.GetInt32(5), rd.GetInt32(4) != 0));
        }

        return list;
    }

    public async Task<BenchmarkRun?> GetRunAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        return await LoadAsync(c, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BenchmarkRun>> GetRecentRunsAsync(int count, CancellationToken cancellationToken = default)
    {
        var infos = await GetRunsAsync(count, cancellationToken).ConfigureAwait(false);
        await using var c = db.Open();
        var runs = new List<BenchmarkRun>();
        foreach (var i in infos)
        {
            if (await LoadAsync(c, i.Id, cancellationToken).ConfigureAwait(false) is { } r)
            {
                runs.Add(r);
            }
        }

        return runs;
    }

    public async Task DeleteRunAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM BenchmarkRuns WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<BenchmarkRun?> LoadAsync(SqliteConnection c, Guid id, CancellationToken ct)
    {
        BenchmarkRun? run = null;
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT StartedAt, FinishedAt, OptionsJson, TargetsJson, Cancelled FROM BenchmarkRuns WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await rd.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            var o = JsonSerializer.Deserialize<OptionsDto>(rd.GetString(2), Json)!;
            var targets = (JsonSerializer.Deserialize<List<TargetDto>>(rd.GetString(3), Json) ?? [])
                .Select(t => new BenchmarkTarget(t.Name, t.ProviderId, t.Address is null ? null : IPAddress.Parse(t.Address), t.Transport, t.DohUrl)).ToList();
            run = new BenchmarkRun(
                id, Parse(rd.GetString(0)), Parse(rd.GetString(1)),
                new BenchmarkOptions(o.Domains, o.Repetitions, TimeSpan.FromMilliseconds(o.TimeoutMs), o.Parallelism, o.CacheMode, o.RecordKind, o.Retries),
                targets, [], rd.GetInt32(4) != 0);
        }

        var samples = new List<QuerySample>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT ProviderId, TargetName, ServerAddress, Transport, Domain, QueryType, ResponseTime, Success, TimedOut, ResponseCode, Attempts, Timestamp FROM BenchmarkResults WHERE RunId=$id ORDER BY Id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rd.ReadAsync(ct).ConfigureAwait(false))
            {
                var transport = Enum.Parse<DnsTransport>(rd.GetString(3));
                var name = rd.GetString(1);
                var server = rd.GetString(2);
                samples.Add(new QuerySample(
                    $"{name}|{server}|{transport}", name, rd.IsDBNull(0) ? null : rd.GetString(0), server, transport, rd.GetString(4),
                    Enum.Parse<RecordKind>(rd.GetString(5)), rd.GetInt32(7) != 0, rd.GetDouble(6), rd.GetString(9), rd.GetInt32(8) != 0, rd.GetInt32(10), Parse(rd.GetString(11))));
            }
        }

        return run with { Samples = samples };
    }

    private static string Iso(DateTime d) => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime Parse(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
