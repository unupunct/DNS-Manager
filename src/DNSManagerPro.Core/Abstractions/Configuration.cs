using DNSManagerPro.Core.Configuration;
using DNSManagerPro.Core.Models;

namespace DNSManagerPro.Core.Abstractions;

/// <summary>Performs the actual OS change. Implementations must never accept free-form text.</summary>
public interface IDnsConfigurator
{
    Task<OperationResult> ApplyAsync(DnsChangePlan plan, CancellationToken cancellationToken = default);
}

public sealed record DnsHistoryEntry(
    long Id,
    string AdapterId,
    string AdapterName,
    string PreviousConfiguration,
    string NewConfiguration,
    string Operation,
    string Result,
    string? Message,
    DateTime TimestampUtc);

public interface IDnsHistoryRepository
{
    /// <summary>Persists the backup BEFORE the change is made. Returns the row id.</summary>
    Task<long> BeginAsync(string adapterId, string adapterName, DnsSnapshot previous, string newDescription, string operation, CancellationToken cancellationToken = default);

    Task CompleteAsync(long id, string result, string? message, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DnsHistoryEntry>> GetRecentAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>The most recent successful change for the adapter: restoring its PreviousConfiguration undoes it.</summary>
    Task<DnsHistoryEntry?> GetLastSuccessfulChangeAsync(string adapterId, CancellationToken cancellationToken = default);
}
