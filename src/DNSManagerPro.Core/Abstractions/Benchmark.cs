using DNSManagerPro.Core.Benchmark;

namespace DNSManagerPro.Core.Abstractions;

/// <summary>Sends one DNS query to a target over its transport and times it.</summary>
public interface IDnsQueryClient
{
    Task<QueryOutcome> QueryAsync(BenchmarkTarget target, string queryName, RecordKind kind, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed record BenchmarkRunInfo(Guid Id, DateTime StartedUtc, DateTime FinishedUtc, int TargetCount, int SampleCount, bool Cancelled);

public interface IBenchmarkRepository
{
    Task SaveAsync(BenchmarkRun run, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BenchmarkRunInfo>> GetRunsAsync(int limit, CancellationToken cancellationToken = default);

    Task<BenchmarkRun?> GetRunAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Newest first, including raw samples.</summary>
    Task<IReadOnlyList<BenchmarkRun>> GetRecentRunsAsync(int count, CancellationToken cancellationToken = default);

    Task DeleteRunAsync(Guid id, CancellationToken cancellationToken = default);
}
