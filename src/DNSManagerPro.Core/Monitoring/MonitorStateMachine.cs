using System.Globalization;
using System.Text;

namespace DNSManagerPro.Core.Monitoring;

public enum MonitorEventKind
{
    ConfigChanged,
    AdapterChanged,
    ResolutionFailure,
    Recovered,
}

public sealed record MonitorEvent(DateTime TimestampUtc, MonitorEventKind Kind, string Message, string? Details = null);

/// <summary>What one monitoring tick observed.</summary>
public sealed record MonitorObservation(
    DateTime Utc,
    string? AdapterId,
    string AdapterName,
    string ConfigFingerprint,
    string ConfigDescription,
    bool? ProbeSucceeded,
    double? Milliseconds,
    string? ProbeDetail);

/// <summary>
/// Turns a stream of observations into events: configuration changes, resolution failures (only after
/// <c>failureThreshold</c> consecutive failures, to ignore one-off packet loss) and recovery. Raises each
/// condition once, on the transition, so notifications never repeat.
/// </summary>
public sealed class MonitorStateMachine(int failureThreshold = 2)
{
    private string? _adapterId;
    private string? _fingerprint;
    private string? _description;
    private int _consecutiveFailures;
    private bool _failing;

    public bool IsFailing => _failing;

    /// <param name="changeMadeByThisApp">True when this app changed DNS moments ago; the new state is adopted silently (the change is already in the history).</param>
    public IReadOnlyList<MonitorEvent> Observe(MonitorObservation o, bool changeMadeByThisApp = false)
    {
        var events = new List<MonitorEvent>();
        if (_adapterId is not null && o.AdapterId != _adapterId)
        {
            events.Add(new MonitorEvent(o.Utc, MonitorEventKind.AdapterChanged, $"Active network adapter changed to '{o.AdapterName}'.", o.ConfigDescription));
            _consecutiveFailures = 0;
            _failing = false;
        }
        else if (_fingerprint is not null && o.ConfigFingerprint != _fingerprint && !changeMadeByThisApp)
        {
            events.Add(new MonitorEvent(o.Utc, MonitorEventKind.ConfigChanged, $"DNS settings on '{o.AdapterName}' changed outside this app.", $"Before: {_description}{Environment.NewLine}After: {o.ConfigDescription}"));
        }

        _adapterId = o.AdapterId;
        _fingerprint = o.ConfigFingerprint;
        _description = o.ConfigDescription;

        switch (o.ProbeSucceeded)
        {
            case false:
                _consecutiveFailures++;
                if (_consecutiveFailures == failureThreshold && !_failing)
                {
                    _failing = true;
                    events.Add(new MonitorEvent(o.Utc, MonitorEventKind.ResolutionFailure,
                        $"DNS resolution failed {failureThreshold} times in a row.", o.ProbeDetail));
                }

                break;
            case true:
                if (_failing)
                {
                    events.Add(new MonitorEvent(o.Utc, MonitorEventKind.Recovered,
                        "DNS resolution works again" + (o.Milliseconds is { } ms ? $" ({ms.ToString("F0", CultureInfo.InvariantCulture)} ms)." : "."), null));
                }

                _failing = false;
                _consecutiveFailures = 0;
                break;
            default:
                break; // nothing to probe (no DNS server): neither success nor failure
        }

        return events;
    }

    public static string Fingerprint(bool v4Auto, IEnumerable<string> v4, bool v6Auto, IEnumerable<string> v6)
    {
        var sb = new StringBuilder();
        sb.Append(v4Auto ? "A:" : "M:").AppendJoin(',', v4).Append('|').Append(v6Auto ? "A:" : "M:").AppendJoin(',', v6);
        return sb.ToString();
    }
}
