using DNSManagerPro.Core.Models;

namespace DNSManagerPro.Core.Abstractions;

/// <summary>Reads network adapters and their real DNS configuration from the host OS.</summary>
public interface INetworkAdapterService
{
    Task<IReadOnlyList<NetworkAdapterInfo>> GetAdaptersAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reports whether the process runs with administrator rights.</summary>
public interface IPrivilegeService
{
    bool IsElevated { get; }
}

/// <summary>Flushes the OS DNS resolver cache.</summary>
public interface IDnsCacheService
{
    Task<OperationResult> FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>Sends one direct DNS query (not via the OS resolver) and measures it.</summary>
public interface IDnsProbe
{
    Task<DnsProbeResult> ProbeAsync(
        System.Net.IPAddress server,
        string domain,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>Per-user automatic start with Windows.</summary>
public interface IStartupRegistration
{
    bool IsEnabled();

    void Configure(bool enabled, bool minimized);
}
