using System.Net;
using DNSManagerPro.Core.Diagnostics;
using DNSManagerPro.Core.History;
using DNSManagerPro.Core.Monitoring;

namespace DNSManagerPro.Core.Abstractions;

public interface IDnsLookupClient
{
    Task<LookupResult> LookupAsync(IPAddress server, string serverName, string domain, LookupType type, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public interface IPingService
{
    Task<PingResult> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>Resolves a host the way applications do: through the operating system resolver (and its cache).</summary>
public interface IOsResolver
{
    Task<OsResolution> ResolveAsync(string host, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public interface IDnsCacheInspector
{
    Task<IReadOnlyList<DnsCacheEntry>> GetEntriesAsync(CancellationToken cancellationToken = default);
}

public interface IDiagnosticsRepository
{
    Task SaveAsync(DiagnosticRun run, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiagnosticRun>> GetRecentAsync(int count, CancellationToken cancellationToken = default);
}

public interface IMonitorRepository
{
    Task AddAsync(MonitorEvent e, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MonitorEvent>> GetRecentAsync(int count, CancellationToken cancellationToken = default);
}

public interface ITimelineRepository
{
    Task<IReadOnlyList<TimelineEntry>> GetAsync(int perSourceLimit, CancellationToken cancellationToken = default);

    /// <summary>Deletes the underlying records. Returns how many were removed.</summary>
    Task<int> DeleteAsync(IEnumerable<TimelineKey> keys, CancellationToken cancellationToken = default);
}
