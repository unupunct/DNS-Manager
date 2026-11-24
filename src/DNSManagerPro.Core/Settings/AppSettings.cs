using DNSManagerPro.Core.Providers;

namespace DNSManagerPro.Core.Settings;

/// <summary>How the apply dialog treats IPv6 servers by default.</summary>
public enum Ipv6Preference
{
    /// <summary>Change IPv6 only when a selected adapter actually uses IPv6.</summary>
    Auto,

    /// <summary>Always include the provider's IPv6 servers when it has them.</summary>
    Always,

    /// <summary>Never change IPv6 unless the user opts in inside the dialog.</summary>
    Never,
}

/// <summary>User settings persisted as JSON. Every value is clamped by <see cref="Sanitized"/> because the file can be edited by hand.</summary>
public sealed record AppSettings
{
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 240;
    public const int MaxAdapterIdLength = 200;

    public static IReadOnlyList<string> DefaultDomains { get; } = ["google.com", "microsoft.com", "cloudflare.com", "wikipedia.org", "github.com"];

    // General
    public bool StartWithWindows { get; init; }

    public bool MinimizeToTray { get; init; }

    public bool LaunchMinimized { get; init; }

    /// <summary>When true the apply dialog needs a second, explicit confirmation click. The preview is always shown.</summary>
    public bool ConfirmDnsChanges { get; init; } = true;

    // Benchmark defaults
    public int BenchmarkRepetitions { get; init; } = 5;

    public int BenchmarkTimeoutMs { get; init; } = 2000;

    public int BenchmarkParallelism { get; init; } = 4;

    public List<string> BenchmarkDomains { get; init; } = [.. DefaultDomains];

    // Monitoring
    public bool MonitoringEnabled { get; init; }

    public int MonitoringIntervalMinutes { get; init; } = 5;

    public bool NotifyOnChange { get; init; } = true;

    public bool NotifyOnFailure { get; init; } = true;

    // Network
    public string? DefaultAdapterId { get; init; }

    public Ipv6Preference Ipv6Handling { get; init; } = Ipv6Preference.Auto;

    /// <summary>Pre-tick the "also enable DNS-over-HTTPS" option in the apply dialog when the provider supports it.</summary>
    public bool PreferEncryptedDns { get; init; }

    /// <summary>Returns a copy with out-of-range or invalid values replaced by safe ones.</summary>
    public AppSettings Sanitized()
    {
        var domains = (BenchmarkDomains ?? [])
            .Select(d => d?.Trim().ToLowerInvariant() ?? string.Empty)
            .Where(d => d.Contains('.', StringComparison.Ordinal) && DnsValidator.ValidateHostName(d, "Domain").IsValid)
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList();
        var adapter = DefaultAdapterId?.Trim();
        return this with
        {
            MonitoringIntervalMinutes = Math.Clamp(MonitoringIntervalMinutes, MinIntervalMinutes, MaxIntervalMinutes),
            BenchmarkRepetitions = Math.Clamp(BenchmarkRepetitions, 1, 100),
            BenchmarkTimeoutMs = Math.Clamp(BenchmarkTimeoutMs, 200, 10_000),
            BenchmarkParallelism = Math.Clamp(BenchmarkParallelism, 1, 16),
            BenchmarkDomains = domains.Count > 0 ? domains : [.. DefaultDomains],
            Ipv6Handling = Enum.IsDefined(Ipv6Handling) ? Ipv6Handling : Ipv6Preference.Auto,
            DefaultAdapterId = string.IsNullOrEmpty(adapter) || adapter.Length > MaxAdapterIdLength || adapter.Any(char.IsControl) ? null : adapter,
        };
    }
}
