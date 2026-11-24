namespace DNSManagerPro.Core.Settings;

/// <summary>User settings persisted as JSON. Phase 6 uses the monitoring section; later phases add the rest.</summary>
public sealed record AppSettings
{
    public bool MonitoringEnabled { get; init; }

    public int MonitoringIntervalMinutes { get; init; } = 5;

    public bool NotifyOnChange { get; init; } = true;

    public bool NotifyOnFailure { get; init; } = true;

    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 240;

    /// <summary>Returns a copy with out-of-range values pulled back to safe limits (settings files can be edited by hand).</summary>
    public AppSettings Sanitized() => this with { MonitoringIntervalMinutes = Math.Clamp(MonitoringIntervalMinutes, MinIntervalMinutes, MaxIntervalMinutes) };
}
