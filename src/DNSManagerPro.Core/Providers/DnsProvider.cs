namespace DNSManagerPro.Core.Providers;

/// <summary>Public DNS provider description. Editable by the user; configuration logic never depends on the descriptive fields.</summary>
public sealed record DnsProvider(
    string Id,
    string Name,
    string Description,
    string Website,
    string? IPv4Primary,
    string? IPv4Secondary,
    string? IPv6Primary,
    string? IPv6Secondary,
    string Privacy,
    string Security,
    string Filtering,
    string Notes = "",
    bool IsBuiltIn = true,
    string? DohUrl = null,
    string? DotHost = null,
    string? DoqHost = null)
{
    public IEnumerable<string> AllAddresses =>
        new[] { IPv4Primary, IPv4Secondary, IPv6Primary, IPv6Secondary }.Where(a => !string.IsNullOrWhiteSpace(a))!;

    public bool HasAddresses => AllAddresses.Any();

    public bool Matches(System.Net.IPAddress address) =>
        AllAddresses.Any(a => System.Net.IPAddress.TryParse(a, out var p) && p.Equals(address));

    /// <summary>Protocols this provider advertises. Plain IP configuration only ever enables unencrypted DNS.</summary>
    public IReadOnlyList<string> Protocols
    {
        get
        {
            var list = new List<string>();
            if (HasAddresses)
            {
                list.Add("UDP/TCP");
            }

            if (!string.IsNullOrWhiteSpace(DohUrl))
            {
                list.Add("DoH");
            }

            if (!string.IsNullOrWhiteSpace(DotHost))
            {
                list.Add("DoT");
            }

            if (!string.IsNullOrWhiteSpace(DoqHost))
            {
                list.Add("DoQ");
            }

            return list.Count == 0 ? ["Needs endpoint"] : list;
        }
    }
}
