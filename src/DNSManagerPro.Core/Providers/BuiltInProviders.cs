namespace DNSManagerPro.Core.Providers;

/// <summary>Built-in provider seed data. Seeded once; user edits are kept.</summary>
public static class BuiltInProviders
{
    public static IReadOnlyList<DnsProvider> All { get; } =
    [
        new("cloudflare", "Cloudflare", "Fast public resolver focused on privacy.", "https://1.1.1.1",
            "1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001",
            "Publishes a privacy policy stating limited, short-lived query logging.", "DNSSEC validation; supports DoH and DoT.",
            "None on 1.1.1.1 (1.1.1.2 / 1.1.1.3 add malware / family filtering).",
            DohUrl: "https://cloudflare-dns.com/dns-query", DotHost: "one.one.one.one"),
        new("google", "Google Public DNS", "Google's global public resolver.", "https://developers.google.com/speed/public-dns",
            "8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844",
            "Logs temporarily and permanently as described in Google's privacy policy.", "DNSSEC validation; supports DoH and DoT.", "None.",
            DohUrl: "https://dns.google/dns-query", DotHost: "dns.google"),
        new("quad9", "Quad9", "Non-profit resolver that blocks known malicious domains.", "https://quad9.net",
            "9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9",
            "States it does not log client IP addresses.", "Threat-intelligence blocking; DNSSEC; DoH and DoT.", "Malware / phishing domain blocking.",
            DohUrl: "https://dns.quad9.net/dns-query", DotHost: "dns.quad9.net"),
        new("opendns", "OpenDNS", "Cisco's public resolver with optional filtering accounts.", "https://www.opendns.com",
            "208.67.222.222", "208.67.220.220", null, null,
            "Operated by Cisco; see their privacy policy.", "Phishing protection; supports DoH.",
            "Basic phishing filtering; category filtering needs an account (FamilyShield uses different addresses).",
            DohUrl: "https://doh.opendns.com/dns-query"),
        new("adguard", "AdGuard DNS", "Resolver that blocks ads and trackers.", "https://adguard-dns.io",
            "94.140.14.14", "94.140.15.15", "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff",
            "See AdGuard's privacy policy.", "Supports DoH, DoT and DoQ.", "Ad and tracker blocking (default mode).",
            DohUrl: "https://dns.adguard-dns.com/dns-query", DotHost: "dns.adguard-dns.com", DoqHost: "dns.adguard-dns.com"),
        new("controld", "Control D", "Configurable resolver; free unfiltered endpoint shown here.", "https://controld.com",
            "76.76.2.0", "76.76.10.0", "2606:1a40::", "2606:1a40:1::",
            "See Control D's privacy policy.", "Supports DoH and DoT.",
            "Unfiltered endpoint; other endpoints (malware, ads, social) use different addresses - edit as needed.",
            DohUrl: "https://freedns.controld.com/p0", DotHost: "p0.freedns.controld.com"),
        new("nextdns", "NextDNS", "Personalized resolver: your own configuration, your own endpoint.", "https://nextdns.io",
            null, null, null, null,
            "Depends on the logging settings you choose in your NextDNS configuration.", "Configurable threat protection, DNSSEC; DoH, DoT, DoQ.",
            "Fully configurable per your NextDNS account.",
            Notes: "Edit this provider and enter your personal endpoint, e.g. DoH URL https://dns.nextdns.io/<your-config-id>. Linked-IP addresses are tied to your account.",
            DohUrl: null, DotHost: null),
    ];
}
