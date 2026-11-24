using System.Net;

namespace DNSManagerPro.Core.Benchmark;

public enum DnsTransport
{
    Udp,
    Tcp,
    Doh,
}

public enum CacheMode
{
    /// <summary>One untimed warm-up query per server/domain, then measure (shows cached-answer latency ~ network round trip).</summary>
    Warm,

    /// <summary>Query a random, non-existent sub-domain each time so the resolver must recurse (NXDOMAIN counts as an answer).</summary>
    Cold,
}

public enum RecordKind
{
    A,
    AAAA,
}

/// <summary>One server (and transport) to measure.</summary>
public sealed record BenchmarkTarget(string Name, string? ProviderId, IPAddress? Address, DnsTransport Transport, string? DohUrl = null)
{
    public string Key => $"{Name}|{ServerText}|{Transport}";

    public string ServerText => Transport == DnsTransport.Doh ? DohUrl ?? "-" : Address?.ToString() ?? "-";

    public string TransportText => Transport switch
    {
        DnsTransport.Udp => "UDP",
        DnsTransport.Tcp => "TCP",
        _ => "DoH",
    };
}

public sealed record BenchmarkOptions(
    IReadOnlyList<string> Domains,
    int Repetitions,
    TimeSpan Timeout,
    int Parallelism,
    CacheMode CacheMode,
    RecordKind RecordKind,
    int Retries = 0)
{
    public static BenchmarkOptions Default { get; } = new(
        ["google.com", "microsoft.com", "cloudflare.com", "wikipedia.org", "github.com"],
        5, TimeSpan.FromSeconds(2), 4, CacheMode.Warm, RecordKind.A);

    public IReadOnlyList<string> Validate(int targetCount)
    {
        var e = new List<string>();
        if (targetCount < 1)
        {
            e.Add("Select at least one server to test.");
        }

        if (Domains.Count is < 1 or > 20)
        {
            e.Add("Provide between 1 and 20 test domains.");
        }

        foreach (var d in Domains)
        {
            var r = Providers.DnsValidator.ValidateHostName(d, $"Domain '{d}'");
            if (!r.IsValid || string.IsNullOrWhiteSpace(d) || !d.Contains('.', StringComparison.Ordinal))
            {
                e.Add($"'{d}' is not a valid domain name.");
            }
        }

        if (Repetitions is < 1 or > 100)
        {
            e.Add("Repetitions must be between 1 and 100.");
        }

        if (Timeout < TimeSpan.FromMilliseconds(200) || Timeout > TimeSpan.FromSeconds(10))
        {
            e.Add("Timeout must be between 200 ms and 10000 ms.");
        }

        if (Parallelism is < 1 or > 16)
        {
            e.Add("Parallel query limit must be between 1 and 16.");
        }

        if (Retries is < 0 or > 2)
        {
            e.Add("Retries must be 0, 1 or 2.");
        }

        return e;
    }
}

/// <summary>Result of one query attempt sequence. Answered = any DNS response arrived (even an error code).</summary>
public sealed record QueryOutcome(bool Answered, double Milliseconds, string ResponseCode, bool TimedOut, string? Error);

/// <summary>One measured query (raw data).</summary>
public sealed record QuerySample(
    string TargetKey,
    string TargetName,
    string? ProviderId,
    string Server,
    DnsTransport Transport,
    string Domain,
    RecordKind Kind,
    bool Success,
    double Milliseconds,
    string ResponseCode,
    bool TimedOut,
    int Attempts,
    DateTime TimestampUtc);

public sealed record BenchmarkRun(
    Guid Id,
    DateTime StartedUtc,
    DateTime FinishedUtc,
    BenchmarkOptions Options,
    IReadOnlyList<BenchmarkTarget> Targets,
    IReadOnlyList<QuerySample> Samples,
    bool Cancelled);

public sealed record BenchmarkProgress(int Completed, int Total, string Current);
