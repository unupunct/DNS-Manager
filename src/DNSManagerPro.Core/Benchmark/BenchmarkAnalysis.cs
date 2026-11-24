namespace DNSManagerPro.Core.Benchmark;

/// <summary>Aggregated result for one target.</summary>
public sealed record TargetSummary(
    string TargetKey,
    string Name,
    string? ProviderId,
    string Server,
    DnsTransport Transport,
    int Queries,
    int Successes,
    int Timeouts,
    int Failures,
    LatencyStats? Stats,
    int Rank = 0,
    bool TiedWithPrevious = false,
    IReadOnlyList<string>? Notes = null,
    string? TopFailure = null)
{
    public double SuccessRate => Queries == 0 ? 0 : (double)Successes / Queries;

    public double TimeoutRate => Queries == 0 ? 0 : (double)Timeouts / Queries;

    public string TransportText => Transport switch { DnsTransport.Udp => "UDP", DnsTransport.Tcp => "TCP", _ => "DoH" };
}

public static class BenchmarkAnalysis
{
    public const int MinSamplesForConfidence = 10;
    public const double MinRankableSuccessRate = 0.5;
    public const double ReliabilityWarningRate = 0.9;

    /// <summary>Raw samples -> one summary per target (unranked).</summary>
    public static IReadOnlyList<TargetSummary> Summarize(IEnumerable<QuerySample> samples) =>
        samples.GroupBy(s => s.TargetKey).Select(g =>
        {
            var first = g.First();
            var ok = g.Where(s => s.Success).ToList();
            var timeouts = g.Count(s => s.TimedOut);
            return new TargetSummary(
                g.Key, first.TargetName, first.ProviderId, first.Server, first.Transport,
                g.Count(), ok.Count, timeouts, g.Count() - ok.Count - timeouts,
                LatencyStats.Compute(ok.Select(s => s.Milliseconds)),
                TopFailure: g.Where(s => !s.Success).GroupBy(s => s.ResponseCode).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault());
        }).ToList();

    /// <summary>
    /// Orders by median latency (ties on P95), only ranking targets that answered at least half of their queries.
    /// Targets whose medians are within max(2 ms, 10%) of the previous one are flagged as tied: such differences are
    /// within normal measurement noise.
    /// </summary>
    public static IReadOnlyList<TargetSummary> Rank(IEnumerable<TargetSummary> summaries)
    {
        var all = summaries.ToList();
        var rankable = all
            .Where(s => s.Stats is not null && s.SuccessRate >= MinRankableSuccessRate)
            .OrderBy(s => s.Stats!.Median).ThenBy(s => s.Stats!.P95)
            .ToList();
        var result = new List<TargetSummary>();
        for (var i = 0; i < rankable.Count; i++)
        {
            var s = rankable[i];
            var tied = i > 0 && WithinNoise(rankable[i - 1].Stats!.Median, s.Stats!.Median);
            result.Add(s with { Rank = i + 1, TiedWithPrevious = tied, Notes = NotesFor(s, tied) });
        }

        foreach (var s in all.Except(rankable))
        {
            result.Add(s with { Rank = 0, Notes = NotesFor(s, false) });
        }

        return result;
    }

    /// <summary>Two medians closer than max(2 ms, 10%) cannot be told apart reliably.</summary>
    public static bool WithinNoise(double reference, double other) => Math.Abs(other - reference) <= Math.Max(2.0, reference * 0.10);

    private static List<string> NotesFor(TargetSummary s, bool tied)
    {
        var n = new List<string>();
        if (s.Stats is null || s.SuccessRate < MinRankableSuccessRate)
        {
            n.Add("not ranked: fewer than half of the queries succeeded");
        }
        else if (s.SuccessRate < ReliabilityWarningRate)
        {
            n.Add("unreliable: some queries failed or timed out");
        }

        if (s.TopFailure is not null && s.Successes < s.Queries)
        {
            n.Add($"most common failure: {s.TopFailure}");
        }

        if (s.Successes is > 0 and < MinSamplesForConfidence)
        {
            n.Add($"low confidence: only {s.Successes} successful samples");
        }

        if (tied)
        {
            n.Add("within noise of the server above");
        }

        return n;
    }

    /// <summary>Plain-language conclusion that never over-claims.</summary>
    public static string Conclusion(IReadOnlyList<TargetSummary> ranked)
    {
        var top = ranked.Where(r => r.Rank > 0).OrderBy(r => r.Rank).ToList();
        if (top.Count == 0)
        {
            return "No server answered enough queries to be ranked.";
        }

        var first = top[0];
        var ms = first.Stats!.Median.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        if (top.Count == 1)
        {
            return $"{first.Name} ({first.TransportText}) had a median of {ms} ms. Only one server could be ranked.";
        }

        var close = top.Skip(1).Where(t => WithinNoise(first.Stats!.Median, t.Stats!.Median)).ToList();
        var tail = close.Count > 0
            ? $" {string.Join(", ", close.Select(c => c.Name))} {(close.Count == 1 ? "is" : "are")} within normal measurement noise of it, so none of them can be called objectively fastest."
            : " The next server is clearly slower in this test.";
        var low = first.Successes < MinSamplesForConfidence ? " Few samples: repeat the benchmark before relying on this." : string.Empty;
        return $"{first.Name} ({first.TransportText}) had the lowest median latency ({ms} ms) in this run.{tail}{low} Results reflect your network and the moment of testing.";
    }
}


