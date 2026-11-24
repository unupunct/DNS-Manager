using DNSManagerPro.Core.Providers;

namespace DNSManagerPro.Core.Profiles;

public static class ProfileCategories
{
    public static IReadOnlyList<string> All { get; } = ["Default", "Gaming", "Privacy", "Security", "Family", "Custom"];
}

/// <summary>A saved, named DNS configuration. IsAutomatic profiles restore DHCP-provided DNS and carry no addresses.</summary>
public sealed record DnsProfile(
    string Id,
    string Name,
    string Description,
    string Category,
    string? ProviderId,
    bool IsAutomatic,
    string? IPv4Primary,
    string? IPv4Secondary,
    string? IPv6Primary,
    string? IPv6Secondary,
    string? DohUrl,
    string? DotHost,
    string? DoqHost,
    string? AdapterId,
    string? AdapterName,
    string Tags,
    string Notes,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? LastAppliedUtc)
{
    public static DnsProfile New(string name, string category = "Custom") => new(
        $"p-{Guid.NewGuid():N}"[..14], name, string.Empty, category, null, false, null, null, null, null, null, null, null, null, null,
        string.Empty, string.Empty, DateTime.UtcNow, DateTime.UtcNow, null);

    /// <summary>Adapts the profile to the provider shape used by the apply dialog and benchmark. Not meaningful for automatic profiles.</summary>
    public DnsProvider ToProvider() => new(
        $"profile-{Id}", Name, Description, string.Empty,
        IsAutomatic ? null : IPv4Primary, IsAutomatic ? null : IPv4Secondary, IsAutomatic ? null : IPv6Primary, IsAutomatic ? null : IPv6Secondary,
        string.Empty, string.Empty, string.Empty, Notes, IsBuiltIn: false, DohUrl: DohUrl, DotHost: DotHost, DoqHost: DoqHost);

    public string Summary => IsAutomatic
        ? "Automatic (DHCP)"
        : string.Join("  /  ", new[] { IPv4Primary, IPv4Secondary, IPv6Primary, IPv6Secondary }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public IReadOnlyList<string> TagList => [.. Tags.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>All problems that prevent saving or applying the profile (empty = valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("Name is required.");
        }
        else if (Name.Trim().Length > 60)
        {
            errors.Add("Name must be 60 characters or fewer.");
        }

        if (!ProfileCategories.All.Contains(Category, StringComparer.Ordinal))
        {
            errors.Add("Unknown category.");
        }

        if (Description.Length > 500 || Notes.Length > 2000 || Tags.Length > 200)
        {
            errors.Add("Description, notes or tags are too long.");
        }

        if (IsAutomatic)
        {
            return errors;
        }

        // Reuse the provider rules (address families, secondary-needs-primary, duplicates, URL/host checks).
        errors.AddRange(DnsValidator.ValidateProvider(ToProvider()).Where(e => e != "Name is required."));
        if (!ToProvider().HasAddresses)
        {
            errors.Add("Enter at least one DNS server address, or choose automatic (DHCP).");
        }

        return errors;
    }
}

/// <summary>Starter templates. They are suggestions built from public provider data, not guarantees.</summary>
public static class ProfileTemplates
{
    public static IReadOnlyList<DnsProfile> All { get; } =
    [
        Make("Default (automatic)", "Default", "Use the DNS servers handed out by your router / ISP (DHCP).", auto: true),
        Make("Gaming", "Gaming", "Starts from Cloudflare. Latency depends on where you live: run the benchmark and edit this profile with the fastest server for you.",
            v4: ("1.1.1.1", "1.0.0.1"), v6: ("2606:4700:4700::1111", "2606:4700:4700::1001"), provider: "cloudflare"),
        Make("Privacy", "Privacy", "Quad9 does not log client IP addresses per its published policy and offers DoH/DoT. Verify the current policy on quad9.net.",
            v4: ("9.9.9.9", "149.112.112.112"), v6: ("2620:fe::fe", "2620:fe::9"), provider: "quad9"),
        Make("Security", "Security", "Quad9 blocks domains on threat-intelligence lists (malware, phishing).",
            v4: ("9.9.9.9", "149.112.112.112"), v6: ("2620:fe::fe", "2620:fe::9"), provider: "quad9"),
        Make("Family", "Family", "Cloudflare for Families (1.1.1.3) blocks malware and adult content. Filtering is never perfect.",
            v4: ("1.1.1.3", "1.0.0.3"), v6: ("2606:4700:4700::1113", "2606:4700:4700::1003"), provider: "cloudflare"),
    ];

    private static DnsProfile Make(string name, string category, string description, bool auto = false, (string, string)? v4 = null, (string, string)? v6 = null, string? provider = null) =>
        DnsProfile.New(name, category) with
        {
            Description = description,
            IsAutomatic = auto,
            ProviderId = provider,
            IPv4Primary = v4?.Item1,
            IPv4Secondary = v4?.Item2,
            IPv6Primary = v6?.Item1,
            IPv6Secondary = v6?.Item2,
        };
}
