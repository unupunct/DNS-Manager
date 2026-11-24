using System.Net;

namespace DNSManagerPro.Core.Models;

/// <summary>Kind of network interface, simplified for display and filtering.</summary>
public enum AdapterKind
{
    Ethernet,
    WiFi,
    Virtual,
    Other,
}

/// <summary>DNS servers configured on one adapter, kept separate per IP family.</summary>
/// <param name="IPv4Servers">Effective IPv4 DNS servers.</param>
/// <param name="IPv6Servers">Effective IPv6 DNS servers.</param>
/// <param name="IPv4Automatic">True when IPv4 DNS comes from DHCP (no static server list).</param>
/// <param name="IPv6Automatic">True when IPv6 DNS comes from DHCPv6/RA (no static server list).</param>
public sealed record AdapterDnsConfiguration(
    IReadOnlyList<IPAddress> IPv4Servers,
    IReadOnlyList<IPAddress> IPv6Servers,
    bool IPv4Automatic,
    bool IPv6Automatic)
{
    public static AdapterDnsConfiguration Empty { get; } = new([], [], true, true);

    public bool IsAutomatic => IPv4Automatic && IPv6Automatic;

    public IPAddress? Primary(bool ipv6) => Nth(ipv6 ? IPv6Servers : IPv4Servers, 0);

    public IPAddress? Secondary(bool ipv6) => Nth(ipv6 ? IPv6Servers : IPv4Servers, 1);

    private static IPAddress? Nth(IReadOnlyList<IPAddress> list, int i) => i < list.Count ? list[i] : null;
}

/// <summary>A snapshot of one Windows network adapter as read from the system.</summary>
public sealed record NetworkAdapterInfo(
    string Id,
    int InterfaceIndex,
    string Name,
    string Description,
    string MacAddress,
    AdapterKind Kind,
    bool IsUp,
    bool IsDhcpEnabled,
    long SpeedBitsPerSecond,
    IReadOnlyList<IPAddress> IPv4Addresses,
    IReadOnlyList<IPAddress> IPv6Addresses,
    IReadOnlyList<IPAddress> Gateways,
    AdapterDnsConfiguration Dns)
{
    public bool HasGateway => Gateways.Count > 0;
}

