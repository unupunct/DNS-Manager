using System.Net;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Benchmark;
using DNSManagerPro.Data;
using DNSManagerPro.Networking;
using Microsoft.Extensions.Logging.Abstractions;

namespace DNSManagerPro.Tests.Benchmark;

public class LatencyStatsTests
{
    [Fact]
    public void Empty_returns_null() => Assert.Null(LatencyStats.Compute([]));

    [Fact]
    public void Ignores_nan_and_infinity() =>
        Assert.Equal(2, LatencyStats.Compute([1, double.NaN, double.PositiveInfinity, 3])!.Count);

    [Fact]
    public void Basic_values_odd_count()
    {
        var s = LatencyStats.Compute([10, 20, 30, 40, 50])!;
        Assert.Equal(5, s.Count);
        Assert.Equal(10, s.Min);
        Assert.Equal(50, s.Max);
        Assert.Equal(30, s.Mean);
        Assert.Equal(30, s.Median);
        Assert.Equal(Math.Sqrt(250), s.StdDev, 6); // sample stddev: sum sq = 1000 / 4
    }

    [Fact]
    public void Median_even_count_is_midpoint() => Assert.Equal(25, LatencyStats.Compute([40, 10, 30, 20])!.Median);

    [Fact]
    public void Percentiles_interpolate_linearly()
    {
        var s = LatencyStats.Compute(Enumerable.Range(1, 100).Select(i => (double)i))!;
        Assert.Equal(95.05, s.P95, 6);   // rank = 0.95*99 = 94.05 -> 95 + 0.05
        Assert.Equal(99.01, s.P99, 6);
    }

    [Fact]
    public void Single_sample_has_zero_stddev_and_equal_percentiles()
    {
        var s = LatencyStats.Compute([42])!;
        Assert.Equal(0, s.StdDev);
        Assert.Equal(42, s.P99);
    }
}

public class AnalysisTests
{
    private static QuerySample S(string name, double ms, bool ok = true, bool timeout = false) =>
        new($"{name}|1.1.1.1|Udp", name, null, "1.1.1.1", DnsTransport.Udp, "x.com", RecordKind.A, ok, ms, ok ? "NoError" : "Timeout", timeout, 1, DateTime.UtcNow);

    private static IEnumerable<QuerySample> Many(string n, double ms, int count) => Enumerable.Range(0, count).Select(_ => S(n, ms));

    [Fact]
    public void Summarize_counts_success_timeout_failure()
    {
        var sum = Assert.Single(BenchmarkAnalysis.Summarize([S("A", 10), S("A", 20), S("A", 2000, false, true), S("A", 5, false)]));
        Assert.Equal(4, sum.Queries);
        Assert.Equal(2, sum.Successes);
        Assert.Equal(1, sum.Timeouts);
        Assert.Equal(1, sum.Failures);
        Assert.Equal(0.5, sum.SuccessRate);
        Assert.Equal(15, sum.Stats!.Median);   // failed queries excluded from latency stats
    }

    [Fact]
    public void Rank_orders_by_median_and_flags_ties()
    {
        var ranked = BenchmarkAnalysis.Rank(BenchmarkAnalysis.Summarize(Many("Slow", 50, 12).Concat(Many("Fast", 10, 12)).Concat(Many("Close", 11, 12))));
        Assert.Equal(["Fast", "Close", "Slow"], ranked.Select(r => r.Name));
        Assert.False(ranked[0].TiedWithPrevious);
        Assert.True(ranked[1].TiedWithPrevious);   // 1 ms apart: within 2 ms noise floor
        Assert.False(ranked[2].TiedWithPrevious);
        Assert.Equal([1, 2, 3], ranked.Select(r => r.Rank));
    }

    [Fact]
    public void Mostly_failing_server_is_not_ranked()
    {
        var samples = Many("Good", 20, 12).Concat(Many("Bad", 5, 2)).Concat(Enumerable.Range(0, 10).Select(_ => S("Bad", 2000, false, true)));
        var ranked = BenchmarkAnalysis.Rank(BenchmarkAnalysis.Summarize(samples));
        Assert.Equal("Good", ranked[0].Name);
        Assert.Equal(0, ranked[1].Rank);
        Assert.Contains(ranked[1].Notes!, n => n.Contains("not ranked", StringComparison.Ordinal));
    }

    [Fact]
    public void Low_sample_count_is_flagged_and_conclusion_does_not_overclaim()
    {
        var ranked = BenchmarkAnalysis.Rank(BenchmarkAnalysis.Summarize(Many("A", 10, 3).Concat(Many("B", 10.5, 3))));
        Assert.Contains(ranked[0].Notes!, n => n.Contains("low confidence", StringComparison.Ordinal));
        var text = BenchmarkAnalysis.Conclusion(ranked);
        Assert.Contains("noise", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("is the fastest", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Conclusion_compares_against_the_top_server_not_a_chain_of_near_ties()
    {
        // B is within noise of A, C is within noise of B but NOT of A: only B may be called indistinguishable from A.
        var ranked = BenchmarkAnalysis.Rank(BenchmarkAnalysis.Summarize(Many("A", 10, 12).Concat(Many("B", 11.5, 12)).Concat(Many("C", 13, 12))));
        Assert.True(ranked[2].TiedWithPrevious);
        var text = BenchmarkAnalysis.Conclusion(ranked);
        Assert.Contains("B is within", text, StringComparison.Ordinal);
        Assert.DoesNotContain("C", text.Split("lowest median", 2)[1].Split("Results reflect")[0].Replace("Cloudflare", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_reason_is_surfaced_in_notes()
    {
        var blocked = Enumerable.Range(0, 5).Select(_ => S("T", 11, false) with { ResponseCode = "Socket:AccessDenied" });
        var summary = Assert.Single(BenchmarkAnalysis.Rank(BenchmarkAnalysis.Summarize(blocked)));
        Assert.Equal("Socket:AccessDenied", summary.TopFailure);
        Assert.Contains(summary.Notes!, n => n.Contains("Socket:AccessDenied", StringComparison.Ordinal));
    }

    [Fact]
    public void Conclusion_handles_nothing_ranked() =>
        Assert.Contains("No server", BenchmarkAnalysis.Conclusion([]), StringComparison.Ordinal);
}

internal sealed class FakeQueryClient(Func<BenchmarkTarget, string, int, QueryOutcome>? behaviour = null) : IDnsQueryClient
{
    private readonly object _gate = new();
    public List<(string Target, string Name)> Calls { get; } = [];

    private readonly Dictionary<string, int> _perKey = [];

    public Task<QueryOutcome> QueryAsync(BenchmarkTarget target, string queryName, RecordKind kind, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int n;
        lock (_gate)
        {
            Calls.Add((target.Name, queryName));
            var k = target.Name + queryName;
            _perKey[k] = n = _perKey.GetValueOrDefault(k) + 1;
        }

        return Task.FromResult(behaviour?.Invoke(target, queryName, n) ?? new QueryOutcome(true, 10, "NoError", false, null));
    }
}

public class RunnerTests
{
    private static BenchmarkTarget T(string n) => new(n, n.ToLowerInvariant(), IPAddress.Parse("1.1.1.1"), DnsTransport.Udp);

    private static BenchmarkOptions Opt(CacheMode m = CacheMode.Warm, int reps = 3, int retries = 0, string[]? domains = null) =>
        new(domains ?? ["a.com", "b.com"], reps, TimeSpan.FromSeconds(1), 4, m, RecordKind.A, retries);

    [Fact]
    public async Task Warm_mode_does_untimed_warmups_then_measures_exact_sample_count()
    {
        var client = new FakeQueryClient();
        var run = await new BenchmarkRunner(client, NullLogger<BenchmarkRunner>.Instance).RunAsync([T("X"), T("Y")], Opt(), null, default);
        Assert.Equal(2 * 2 * 3, run.Samples.Count);            // targets x domains x reps
        Assert.Equal((2 * 2) + (2 * 2 * 3), client.Calls.Count); // + one warm-up per target/domain
        Assert.False(run.Cancelled);
    }

    [Fact]
    public async Task Cold_mode_uses_unique_random_subdomains_and_accepts_nxdomain()
    {
        var client = new FakeQueryClient((_, _, _) => new QueryOutcome(true, 30, "NXDomain", false, null));
        var run = await new BenchmarkRunner(client, NullLogger<BenchmarkRunner>.Instance).RunAsync([T("X")], Opt(CacheMode.Cold, 4, domains: ["a.com"]), null, default);
        Assert.Equal(4, client.Calls.Count);                    // no warm-up in cold mode
        Assert.Equal(4, client.Calls.Select(c => c.Name).Distinct().Count());
        Assert.All(client.Calls, c => Assert.EndsWith(".a.com", c.Name, StringComparison.Ordinal));
        Assert.All(run.Samples, s => Assert.True(s.Success));
    }

    [Fact]
    public async Task Warm_mode_treats_nxdomain_as_failure()
    {
        var client = new FakeQueryClient((_, _, _) => new QueryOutcome(true, 30, "NXDomain", false, null));
        var run = await new BenchmarkRunner(client, NullLogger<BenchmarkRunner>.Instance).RunAsync([T("X")], Opt(reps: 2, domains: ["a.com"]), null, default);
        Assert.All(run.Samples, s => Assert.False(s.Success));
    }

    [Fact]
    public async Task Timeouts_are_recorded_not_thrown_and_retries_can_recover()
    {
        // first attempt of every measured query times out, the retry succeeds
        var client = new FakeQueryClient((_, _, n) => n % 2 == 1 ? new QueryOutcome(false, 1000, "Timeout", true, "t") : new QueryOutcome(true, 12, "NoError", false, null));
        var run = await new BenchmarkRunner(client, NullLogger<BenchmarkRunner>.Instance).RunAsync([T("X")], Opt(CacheMode.Cold, 3, retries: 1, domains: ["a.com"]), null, default);
        // cold mode => unique names, so attempt counter per name: 1 (timeout) then 2 (ok)
        Assert.All(run.Samples, s =>
        {
            Assert.True(s.Success);
            Assert.Equal(2, s.Attempts);
            Assert.Equal(12, s.Milliseconds);
        });

        var down = new FakeQueryClient((_, _, _) => new QueryOutcome(false, 1000, "Timeout", true, "t"));
        var r2 = await new BenchmarkRunner(down, NullLogger<BenchmarkRunner>.Instance).RunAsync([T("X")], Opt(reps: 2, domains: ["a.com"]), null, default);
        Assert.All(r2.Samples, s =>
        {
            Assert.False(s.Success);
            Assert.True(s.TimedOut);
        });
        var sum = Assert.Single(BenchmarkAnalysis.Summarize(r2.Samples));
        Assert.Equal(1.0, sum.TimeoutRate);
    }

    [Fact]
    public async Task Cancellation_returns_partial_run()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var client = new FakeQueryClient((_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 5)
            {
                cts.Cancel();
            }

            return new QueryOutcome(true, 5, "NoError", false, null);
        });
        var run = await new BenchmarkRunner(client, NullLogger<BenchmarkRunner>.Instance).RunAsync([T("X")], Opt(CacheMode.Cold, 50, domains: ["a.com"]), null, cts.Token);
        Assert.True(run.Cancelled);
        Assert.InRange(run.Samples.Count, 1, 49);
    }

    [Fact]
    public async Task Progress_reaches_total()
    {
        var last = new BenchmarkProgress(0, 0, "");
        var p = new SyncProgress<BenchmarkProgress>(x => last = x.Completed >= last.Completed ? x : last);
        await new BenchmarkRunner(new FakeQueryClient(), NullLogger<BenchmarkRunner>.Instance).RunAsync([T("X")], Opt(reps: 2, domains: ["a.com"]), p, default);
        Assert.Equal(last.Total, last.Completed);
    }

    private sealed class SyncProgress<TValue>(Action<TValue> a) : IProgress<TValue>
    {
        public void Report(TValue value) => a(value);
    }

    [Fact]
    public void Options_validation()
    {
        Assert.Empty(BenchmarkOptions.Default.Validate(1));
        Assert.NotEmpty(BenchmarkOptions.Default.Validate(0));
        Assert.NotEmpty((BenchmarkOptions.Default with { Domains = ["not a domain"] }).Validate(1));
        Assert.NotEmpty((BenchmarkOptions.Default with { Domains = ["localhost"] }).Validate(1));
        Assert.NotEmpty((BenchmarkOptions.Default with { Repetitions = 0 }).Validate(1));
        Assert.NotEmpty((BenchmarkOptions.Default with { Timeout = TimeSpan.FromMilliseconds(10) }).Validate(1));
        Assert.NotEmpty((BenchmarkOptions.Default with { Parallelism = 99 }).Validate(1));
    }

    [Fact]
    public async Task Invalid_options_throw_before_any_query()
    {
        var client = new FakeQueryClient();
        await Assert.ThrowsAsync<ArgumentException>(() => new BenchmarkRunner(client, NullLogger<BenchmarkRunner>.Instance).RunAsync([T("X")], Opt(reps: 0), null, default));
        Assert.Empty(client.Calls);
    }
}

public class ExportTests
{
    private static BenchmarkRun Run() => new(
        Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow, BenchmarkOptions.Default,
        [new BenchmarkTarget("Evil,\"Name\"", null, IPAddress.Parse("1.1.1.1"), DnsTransport.Udp)],
        [new QuerySample("k", "=cmd|' /C calc'!A0", null, "1.1.1.1", DnsTransport.Udp, "a.com", RecordKind.A, true, 12.3456, "NoError", false, 1, DateTime.UtcNow)],
        false);

    [Fact]
    public void Csv_is_escaped_and_formula_safe()
    {
        var csv = BenchmarkExporter.RawCsv(Run());
        Assert.StartsWith("RunId,TimestampUtc,Target", csv, StringComparison.Ordinal);
        Assert.Contains("'=cmd", csv, StringComparison.Ordinal);       // formula neutralised
        Assert.Contains("12.346", csv, StringComparison.Ordinal);
        Assert.Equal("\"a,\"\"b\"\"\"", BenchmarkExporter.Esc("a,\"b\""));
    }

    [Fact]
    public void Json_contains_summary_raw_and_limitations()
    {
        var json = BenchmarkExporter.Json(Run());
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("Summary", out _));
        Assert.True(doc.RootElement.TryGetProperty("Raw", out var raw));
        Assert.Equal(1, raw.GetArrayLength());
        Assert.Contains("noise", doc.RootElement.GetProperty("Limitations").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_csv_has_one_row_per_target()
    {
        var csv = BenchmarkExporter.SummaryCsv(BenchmarkAnalysis.Rank(BenchmarkAnalysis.Summarize(Run().Samples)));
        Assert.Equal(2, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }
}

public class DnsWireTests
{
    [Fact]
    public void Builds_a_valid_query_packet()
    {
        var p = DnsWire.BuildQuery("example.com", DnsWire.TypeA, 0x1234);
        Assert.Equal(0x12, p[0]);
        Assert.Equal(0x34, p[1]);
        Assert.Equal(0x01, p[2]); // RD flag
        Assert.Equal(1, p[5]);    // QDCOUNT
        Assert.Equal(7, p[12]);
        Assert.Equal((byte)'e', p[13]);
        Assert.Equal(0, p[^5]);   // root label
        Assert.Equal(1, p[^1]);   // class IN
        Assert.Equal(DnsWire.TypeA, p[^3]);
    }

    [Fact]
    public void Rejects_bad_labels()
    {
        Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery("a..com", 1, 1));
        Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery(new string('a', 64) + ".com", 1, 1));
    }

    [Fact]
    public void Parses_rcode_only_from_responses()
    {
        byte[] resp = [0, 1, 0x81, 0x83, 0, 1, 0, 0, 0, 0, 0, 0]; // QR=1, RCODE=3
        Assert.Equal(3, DnsWire.ParseRcode(resp));
        Assert.Equal("NXDomain", DnsWire.RcodeName(3));
        byte[] query = [0, 1, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0];
        Assert.Null(DnsWire.ParseRcode(query));
        Assert.Null(DnsWire.ParseRcode(new byte[5]));
    }
}

public sealed class BenchmarkRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dnsmp-b-{Guid.NewGuid():N}.db");
    private readonly SqliteBenchmarkRepository _repo;

    public BenchmarkRepositoryTests()
    {
        var db = new SqliteDatabase(_path);
        db.Migrate();
        _repo = new SqliteBenchmarkRepository(db);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // temp
        }
    }

    private static BenchmarkRun Run(double ms)
    {
        var t = new BenchmarkTarget("Cloudflare", "cloudflare", IPAddress.Parse("1.1.1.1"), DnsTransport.Udp);
        var doh = new BenchmarkTarget("Cloudflare", "cloudflare", null, DnsTransport.Doh, "https://cloudflare-dns.com/dns-query");
        return new BenchmarkRun(Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow, BenchmarkOptions.Default, [t, doh],
            [new QuerySample(t.Key, "Cloudflare", "cloudflare", "1.1.1.1", DnsTransport.Udp, "a.com", RecordKind.A, true, ms, "NoError", false, 1, DateTime.UtcNow),
             new QuerySample(doh.Key, "Cloudflare", "cloudflare", doh.ServerText, DnsTransport.Doh, "a.com", RecordKind.A, false, 2000, "Timeout", true, 2, DateTime.UtcNow)],
            false);
    }

    [Fact]
    public async Task Save_and_load_roundtrip_preserves_everything()
    {
        var run = Run(12.5);
        await _repo.SaveAsync(run);
        var back = await _repo.GetRunAsync(run.Id);
        Assert.NotNull(back);
        Assert.Equal(2, back.Samples.Count);
        Assert.Equal(run.Options.Domains, back.Options.Domains);
        Assert.Equal(CacheMode.Warm, back.Options.CacheMode);
        Assert.Equal(2, back.Targets.Count);
        Assert.Equal(12.5, back.Samples[0].Milliseconds);
        Assert.True(back.Samples[1].TimedOut);
        Assert.Equal(run.Samples[0].TargetKey, back.Samples[0].TargetKey);   // keys stay stable so history charts can group
    }

    [Fact]
    public async Task Recent_runs_are_newest_first_and_delete_cascades()
    {
        var a = Run(10);
        await _repo.SaveAsync(a);
        await Task.Delay(20);
        var b = Run(20);
        b = b with { StartedUtc = DateTime.UtcNow };
        await _repo.SaveAsync(b);
        var recent = await _repo.GetRecentRunsAsync(5);
        Assert.Equal([b.Id, a.Id], recent.Select(r => r.Id));
        await _repo.DeleteRunAsync(a.Id);
        Assert.Null(await _repo.GetRunAsync(a.Id));
        Assert.Single(await _repo.GetRunsAsync(10));
    }
}

