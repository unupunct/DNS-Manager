using CommunityToolkit.Mvvm.ComponentModel;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Benchmark;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.App.Services;

/// <summary>Holds the latest benchmark and recent history so the dashboard and the benchmark page stay in sync.</summary>
public sealed partial class BenchmarkSession(IBenchmarkRepository repo, ILogger<BenchmarkSession> logger) : ObservableObject
{
    public const int HistoryDepth = 10;

    [ObservableProperty]
    private BenchmarkRun? _lastRun;

    [ObservableProperty]
    private IReadOnlyList<BenchmarkRun> _history = [];

    public async Task LoadAsync()
    {
        try
        {
            var recent = await repo.GetRecentRunsAsync(HistoryDepth);
            History = recent;
            LastRun = recent.Count > 0 ? recent[0] : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not load benchmark history");
        }
    }

    public async Task RecordAsync(BenchmarkRun run)
    {
        if (run.Samples.Count == 0)
        {
            return;
        }

        try
        {
            await repo.SaveAsync(run);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not save benchmark run");
        }

        LastRun = run;
        History = [run, .. History.Where(r => r.Id != run.Id)];
        if (History.Count > HistoryDepth)
        {
            History = [.. History.Take(HistoryDepth)];
        }
    }
}

