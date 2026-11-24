using System.Globalization;
using DNSManagerPro.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.Core.Benchmark;

/// <summary>
/// Runs repeated direct DNS queries against each target. Work is interleaved (rotating order each round) so no
/// server is systematically measured at a better or worse moment, and concurrency is capped.
/// </summary>
public sealed class BenchmarkRunner(IDnsQueryClient client, ILogger<BenchmarkRunner> logger)
{
    public async Task<BenchmarkRun> RunAsync(
        IReadOnlyList<BenchmarkTarget> targets,
        BenchmarkOptions options,
        IProgress<BenchmarkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = options.Validate(targets.Count);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(options));
        }

        var started = DateTime.UtcNow;
        var samples = new List<QuerySample>();
        var gate = new object();
        var warmups = options.CacheMode == CacheMode.Warm ? targets.Count * options.Domains.Count : 0;
        var total = warmups + (targets.Count * options.Domains.Count * options.Repetitions);
        var done = 0;
        var cancelled = false;

        using var limiter = new SemaphoreSlim(options.Parallelism);

        async Task Execute(BenchmarkTarget t, string domain, bool warmup)
        {
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var qname = options.CacheMode == CacheMode.Cold ? $"dnsmp-{Guid.NewGuid().ToString("N")[..10]}.{domain}" : domain;
                var attempts = 0;
                QueryOutcome outcome;
                do
                {
                    attempts++;
                    outcome = await client.QueryAsync(t, qname, options.RecordKind, options.Timeout, cancellationToken).ConfigureAwait(false);
                }
                while (!outcome.Answered && attempts <= options.Retries);

                if (!warmup)
                {
                    var sample = new QuerySample(
                        t.Key, t.Name, t.ProviderId, t.ServerText, t.Transport, domain, options.RecordKind,
                        IsSuccess(outcome, options.CacheMode), outcome.Milliseconds, outcome.ResponseCode, outcome.TimedOut, attempts, DateTime.UtcNow);
                    lock (gate)
                    {
                        samples.Add(sample);
                    }
                }

                progress?.Report(new BenchmarkProgress(Interlocked.Increment(ref done), total, $"{t.Name} ({t.TransportText}) - {domain}"));
            }
            finally
            {
                limiter.Release();
            }
        }

        try
        {
            if (options.CacheMode == CacheMode.Warm)
            {
                await Task.WhenAll(targets.SelectMany(t => options.Domains.Select(d => Execute(t, d, true)))).ConfigureAwait(false);
            }

            var work = new List<Task>();
            for (var rep = 0; rep < options.Repetitions; rep++)
            {
                foreach (var d in options.Domains)
                {
                    for (var i = 0; i < targets.Count; i++)
                    {
                        var t = targets[(i + rep) % targets.Count]; // rotate starting server every round
                        work.Add(Execute(t, d, false));
                    }
                }
            }

            await Task.WhenAll(work).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            logger.LogInformation("Benchmark cancelled after {Done}/{Total} queries", done, total);
        }

        List<QuerySample> ordered;
        lock (gate)
        {
            ordered = [.. samples.OrderBy(s => s.TimestampUtc)];
        }

        return new BenchmarkRun(Guid.NewGuid(), started, DateTime.UtcNow, options, targets, ordered, cancelled);
    }

    /// <summary>In cold mode a random sub-domain legitimately returns NXDOMAIN; that still proves the server resolved recursively.</summary>
    public static bool IsSuccess(QueryOutcome o, CacheMode mode) =>
        o.Answered && (o.ResponseCode == "NoError" || (mode == CacheMode.Cold && o.ResponseCode == "NXDomain"));

    internal static string Fmt(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
}
