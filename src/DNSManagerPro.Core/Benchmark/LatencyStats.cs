namespace DNSManagerPro.Core.Benchmark;

/// <summary>Descriptive statistics over successful query latencies (milliseconds).</summary>
public sealed record LatencyStats(int Count, double Min, double Max, double Mean, double Median, double StdDev, double P95, double P99)
{
    /// <summary>Returns null when there are no samples. Percentiles use linear interpolation between closest ranks; StdDev is the sample standard deviation (n-1).</summary>
    public static LatencyStats? Compute(IEnumerable<double> values)
    {
        var s = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).Order().ToList();
        if (s.Count == 0)
        {
            return null;
        }

        var mean = s.Average();
        var sd = s.Count > 1 ? Math.Sqrt(s.Sum(v => (v - mean) * (v - mean)) / (s.Count - 1)) : 0;
        return new LatencyStats(s.Count, s[0], s[^1], mean, Percentile(s, 0.5), sd, Percentile(s, 0.95), Percentile(s, 0.99));
    }

    public static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0)
        {
            throw new ArgumentException("No values.", nameof(sortedAscending));
        }

        if (sortedAscending.Count == 1)
        {
            return sortedAscending[0];
        }

        var rank = Math.Clamp(p, 0, 1) * (sortedAscending.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sortedAscending[lo] + ((sortedAscending[hi] - sortedAscending[lo]) * (rank - lo));
    }
}
