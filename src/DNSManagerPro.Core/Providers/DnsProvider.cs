using System.Net;

namespace DNSManagerPro.Core.Providers;

/// <summary>Editable-in-future description of a public DNS provider. Configuration logic never depends on these fields.</summary>
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
    bool IsBuiltIn = true)
{
    public IEnumerable<string> AllAddresses =>
        new[] { IPv4Primary, IPv4Secondary, IPv6Primary, IPv6Secondary }.Where(a => !string.IsNullOrWhiteSpace(a))!;

    public bool Matches(IPAddress address) =>
        AllAddresses.Any(a => IPAddress.TryParse(a, out var p) && p.Equals(address));
}

/// <summary>Built-in provider seed data (Phase 2 makes this editable and persistent).</summary>
public static class BuiltInProviders
{
    public static IReadOnlyList<DnsProvider> All { get; } =
    [
        new("cloudflare", "Cloudflare", "Fast public resolver focused on privacy.", "https://1.1.1.1",
            "1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001",
            "Publishes a privacy policy stating limited, short-lived query logging.", "DNSSEC validation; supports DoH and DoT.", "None by default (1.1.1.2 / 1.1.1.3 offer malware / family filtering)."),
        new("google", "Google Public DNS", "Google's global public resolver.", "https://developers.google.com/speed/public-dns",
            "8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844",
            "Logs temporarily and permanently as described in Google's privacy policy.", "DNSSEC validation; supports DoH and DoT.", "None."),
        new("quad9", "Quad9", "Non-profit resolver that blocks known malicious domains.", "https://quad9.net",
            "9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9",
            "Does not log client IP addresses per its published policy.", "Threat-intelligence blocking; DNSSEC; supports DoH and DoT.", "Malware / phishing domain blocking."),
        new("opendns", "OpenDNS", "Cisco's public resolver with optional filtering accounts.", "https://www.opendns.com",
            "208.67.222.222", "208.67.220.220", null, null,
            "Operated by Cisco; see their privacy policy.", "Phishing protection; supports DoH.", "Basic phishing filtering; category filtering needs an account (FamilyShield uses different addresses)."),
        new("adguard", "AdGuard DNS", "Resolver that blocks ads and trackers.", "https://adguard-dns.io",
            "94.140.14.14", "94.140.15.15", "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff",
            "See AdGuard's privacy policy.", "Supports DoH, DoT and DoQ.", "Ad and tracker blocking (default mode)."),
        new("controld", "Control D", "Configurable resolver; public endpoints shown here.", "https://controld.com",
            "76.76.2.0", "76.76.10.0", "2606:1a40::", "2606:1a40:1::",
            "See Control D's privacy policy.", "Supports DoH and DoT.", "Unfiltered endpoint; other endpoints (malware, ads, social) differ - edit as needed."),
    ];
}
