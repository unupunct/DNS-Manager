using System.Net;
using System.Text.Json;
using DNSManagerPro.Core.Models;

namespace DNSManagerPro.Core.Configuration;

public enum DnsMode
{
    Unchanged,
    Automatic,
    Manual,
}

/// <summary>What to do with one IP family's DNS settings on an adapter.</summary>
public sealed record FamilyChange(DnsMode Mode, IReadOnlyList<IPAddress> Servers)
{
    public static FamilyChange Unchanged { get; } = new(DnsMode.Unchanged, []);

    public static FamilyChange Automatic { get; } = new(DnsMode.Automatic, []);

    public static FamilyChange Manual(IEnumerable<IPAddress> servers) => new(DnsMode.Manual, servers.ToList());
}

/// <summary>An intended change to one adapter. IPv4 and IPv6 are handled independently.</summary>
public sealed record DnsChangePlan(NetworkAdapterInfo Adapter, FamilyChange IPv4, FamilyChange IPv6, string Source);

public sealed record PreviewRow(string Label, string Before, string After, bool Changes);

/// <summary>Complete, restorable copy of an adapter's DNS settings taken before any change.</summary>
public sealed record DnsSnapshot(
    string AdapterId,
    int InterfaceIndex,
    string AdapterName,
    bool IPv4Automatic,
    IReadOnlyList<string> IPv4Servers,
    bool IPv6Automatic,
    IReadOnlyList<string> IPv6Servers,
    DateTime CapturedAtUtc)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static DnsSnapshot From(NetworkAdapterInfo a) => new(
        a.Id, a.InterfaceIndex, a.Name,
        a.Dns.IPv4Automatic, a.Dns.IPv4Servers.Select(s => s.ToString()).ToList(),
        a.Dns.IPv6Automatic, a.Dns.IPv6Servers.Select(s => s.ToString()).ToList(),
        DateTime.UtcNow);

    /// <summary>A plan that puts the adapter back to exactly this snapshot (both families are always set explicitly).</summary>
    public DnsChangePlan ToPlan(NetworkAdapterInfo adapter, string source) => new(
        adapter,
        IPv4Automatic ? FamilyChange.Automatic : FamilyChange.Manual(IPv4Servers.Select(IPAddress.Parse)),
        IPv6Automatic ? FamilyChange.Automatic : FamilyChange.Manual(IPv6Servers.Select(IPAddress.Parse)),
        source);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static DnsSnapshot? FromJson(string json) => JsonSerializer.Deserialize<DnsSnapshot>(json, Json);

    public string Describe() =>
        $"IPv4: {(IPv4Automatic ? "automatic" : string.Join(", ", IPv4Servers))}; IPv6: {(IPv6Automatic ? "automatic" : string.Join(", ", IPv6Servers))}";
}

public sealed record ApplyOutcome(
    bool Success,
    string Message,
    IReadOnlyList<string> Steps,
    bool RolledBack = false,
    bool RequiresElevation = false);
