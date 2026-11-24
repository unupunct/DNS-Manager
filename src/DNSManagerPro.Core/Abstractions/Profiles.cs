using System.Net;
using DNSManagerPro.Core.Models;
using DNSManagerPro.Core.Profiles;

namespace DNSManagerPro.Core.Abstractions;

public interface IProfileRepository
{
    Task<IReadOnlyList<DnsProfile>> GetAllAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(DnsProfile p, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task MarkAppliedAsync(string id, DateTime utc, CancellationToken cancellationToken = default);
}

/// <summary>Registers DNS-over-HTTPS templates with Windows for given server IPs (Windows 11 and later only).</summary>
public interface IEncryptedDnsService
{
    bool IsSupported { get; }

    Task<OperationResult> EnableDohAsync(IReadOnlyList<IPAddress> servers, string dohTemplate, bool allowUdpFallback, CancellationToken cancellationToken = default);
}

