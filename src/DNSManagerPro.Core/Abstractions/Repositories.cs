using DNSManagerPro.Core.Providers;

namespace DNSManagerPro.Core.Abstractions;

public interface IProviderRepository
{
    Task<IReadOnlyList<DnsProvider>> GetAllAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(DnsProvider provider, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Re-inserts any missing built-ins and resets edited built-ins to their original values. Custom providers are untouched.</summary>
    Task RestoreBuiltInsAsync(CancellationToken cancellationToken = default);
}

public interface IAdapterStore
{
    Task RecordAsync(IEnumerable<Models.NetworkAdapterInfo> adapters, CancellationToken cancellationToken = default);
}
