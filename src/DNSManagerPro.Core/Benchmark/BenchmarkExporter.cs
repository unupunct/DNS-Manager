using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DNSManagerPro.Core.Benchmark;

/// <summary>CSV and JSON export of raw and aggregated benchmark data.</summary>
public static class BenchmarkExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string RawCsv(BenchmarkRun run)
    {
        var sb = new StringBuilder("RunId,TimestampUtc,Target,Server,Transport,Domain,QueryType,Success,ResponseTimeMs,ResponseCode,TimedOut,Attempts\r\n");
        foreach (var s in run.Samples)
        {
            sb.AppendJoin(',', Esc(run.Id.ToString()), Esc(s.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)), Esc(s.TargetName), Esc(s.Server),
                Esc(s.Transport.ToString()), Esc(s.Domain), Esc(s.Kind.ToString()), s.Success ? "true" : "false",
                s.Milliseconds.ToString("F3", CultureInfo.InvariantCulture), Esc(s.ResponseCode), s.TimedOut ? "true" : "false",
                s.Attempts.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        return sb.ToString();
    }

    public static string SummaryCsv(IEnumerable<TargetSummary> ranked)
    {
        var sb = new StringBuilder("Rank,Target,Server,Transport,Queries,Successes,Timeouts,Failures,SuccessPercent,MinMs,MeanMs,MedianMs,MaxMs,StdDevMs,P95Ms,P99Ms,Notes\r\n");
        string F(double? v) => v is null ? string.Empty : v.Value.ToString("F3", CultureInfo.InvariantCulture);
        foreach (var s in ranked)
        {
            var st = s.Stats;
            sb.AppendJoin(',', s.Rank == 0 ? string.Empty : s.Rank.ToString(CultureInfo.InvariantCulture), Esc(s.Name), Esc(s.Server), s.TransportText,
                s.Queries.ToString(CultureInfo.InvariantCulture), s.Successes.ToString(CultureInfo.InvariantCulture), s.Timeouts.ToString(CultureInfo.InvariantCulture),
                s.Failures.ToString(CultureInfo.InvariantCulture), (s.SuccessRate * 100).ToString("F1", CultureInfo.InvariantCulture),
                F(st?.Min), F(st?.Mean), F(st?.Median), F(st?.Max), F(st?.StdDev), F(st?.P95), F(st?.P99), Esc(string.Join("; ", s.Notes ?? []))).Append("\r\n");
        }

        return sb.ToString();
    }

    public static string Json(BenchmarkRun run)
    {
        var ranked = BenchmarkAnalysis.Rank(BenchmarkAnalysis.Summarize(run.Samples));
        return JsonSerializer.Serialize(new
        {
            run.Id,
            run.StartedUtc,
            run.FinishedUtc,
            run.Cancelled,
            Options = new
            {
                run.Options.Domains,
                run.Options.Repetitions,
                TimeoutMs = run.Options.Timeout.TotalMilliseconds,
                run.Options.Parallelism,
                run.Options.CacheMode,
                run.Options.RecordKind,
                run.Options.Retries,
            },
            Limitations = "Latency measured from this machine at this moment. Results vary with caching, network conditions and server load; differences within a few milliseconds are within noise.",
            Conclusion = BenchmarkAnalysis.Conclusion(ranked),
            Summary = ranked,
            Raw = run.Samples,
        }, JsonOptions);
    }

    /// <summary>RFC 4180 quoting plus protection against spreadsheet formula injection from text fields.</summary>
    public static string Esc(string? value)
    {
        var v = value ?? string.Empty;
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            v = "'" + v;
        }

        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }
}

