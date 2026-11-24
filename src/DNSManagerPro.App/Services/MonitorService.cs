using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Monitoring;
using DNSManagerPro.Core.Providers;
using DNSManagerPro.Core.Settings;
using DNSManagerPro.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.App.Services;

/// <summary>
/// Optional background monitoring: every N minutes it reads the adapter configuration and sends one DNS query.
/// Cost per check is one adapter enumeration and one UDP packet. Events are stored, raised, and (if enabled) notified once per transition.
/// </summary>
public sealed class MonitorService(
    SettingsService settings,
    INetworkAdapterService adapters,
    IDnsProbe probe,
    IMonitorRepository repo,
    INotifier notifier,
    SystemStateService state,
    ILogger<MonitorService> logger) : IDisposable
{
    private static readonly string[] Domains = ["cloudflare.com", "google.com", "microsoft.com"];

    private readonly MonitorStateMachine _machine = new(2);
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private int _domainIndex;

    public event Action<MonitorEvent>? EventRaised;

    public event Action? StatusChanged;

    public DateTime? LastCheckUtc { get; private set; }

    public string LastStatus { get; private set; } = "Monitoring is off.";

    public bool IsRunning => _cts is not null;

    public void Start()
    {
        settings.Changed += _ => Apply();
        Apply();
    }

    private void Apply()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        var s = settings.Current;
        if (!s.MonitoringEnabled)
        {
            LastStatus = "Monitoring is off.";
            StatusChanged?.Invoke();
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        LastStatus = "Monitoring started...";
        StatusChanged?.Invoke();
        _ = Task.Run(() => LoopAsync(TimeSpan.FromMinutes(s.MonitoringIntervalMinutes), token), token);
    }

    private async Task LoopAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            await CheckNowAsync(ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await CheckNowAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped or reconfigured
        }
    }

    public async Task CheckNowAsync(CancellationToken ct = default)
    {
        if (!await _tickGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var all = await adapters.GetAdaptersAsync(ct).ConfigureAwait(false);
            var active = ActiveAdapterSelector.Select(all);
            MonitorObservation obs;
            if (active is null)
            {
                obs = new MonitorObservation(DateTime.UtcNow, "none", "no adapter", "none", "No active network adapter.", null, null, null);
            }
            else
            {
                var d = active.Dns;
                var fp = MonitorStateMachine.Fingerprint(d.IPv4Automatic, d.IPv4Servers.Select(x => x.ToString()), d.IPv6Automatic, d.IPv6Servers.Select(x => x.ToString()));
                var desc = $"IPv4 {(d.IPv4Automatic ? "automatic" : "manual")}: {string.Join(", ", d.IPv4Servers)}; IPv6 {(d.IPv6Automatic ? "automatic" : "manual")}: {string.Join(", ", d.IPv6Servers)}";
                var target = d.Primary(false) ?? d.Primary(true);
                bool? ok = null;
                double? ms = null;
                string? detail = null;
                if (target is not null)
                {
                    var domain = Domains[_domainIndex++ % Domains.Length];
                    var r = await probe.ProbeAsync(target, domain, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    ok = r.Success;
                    ms = r.Success ? r.ResponseTimeMs : null;
                    detail = r.Success ? null : $"{target} did not answer for {domain} ({r.ResponseCode}).";
                }

                obs = new MonitorObservation(DateTime.UtcNow, active.Id, active.Name, fp, desc, ok, ms, detail);
            }

            var changedByApp = state.LastAppChangeUtc is { } t && (LastCheckUtc is null || t > LastCheckUtc);
            LastCheckUtc = DateTime.UtcNow;
            LastStatus = obs.ProbeSucceeded switch
            {
                true => $"Last check {DateTime.Now:HH:mm:ss}: DNS OK ({obs.Milliseconds:F0} ms).",
                false => $"Last check {DateTime.Now:HH:mm:ss}: DNS not answering.",
                _ => $"Last check {DateTime.Now:HH:mm:ss}: nothing to test.",
            };
            foreach (var e in _machine.Observe(obs, changedByApp))
            {
                await repo.AddAsync(e, ct).ConfigureAwait(false);
                EventRaised?.Invoke(e);
                Notify(e);
            }

            StatusChanged?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Monitoring check failed");
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private void Notify(MonitorEvent e)
    {
        var s = settings.Current;
        var isFailureKind = e.Kind is MonitorEventKind.ResolutionFailure or MonitorEventKind.Recovered;
        if ((isFailureKind && s.NotifyOnFailure) || (!isFailureKind && s.NotifyOnChange))
        {
            notifier.Notify("DNS Manager Pro", e.Message, e.Kind is MonitorEventKind.ResolutionFailure or MonitorEventKind.ConfigChanged);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _tickGate.Dispose();
    }
}
