using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Models;
using DNSManagerPro.Core.Providers;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.App.Services;

/// <summary>
/// Single source of truth for live system state (adapters, connectivity, DNS latency).
/// Everything here is read from the actual Windows system; nothing is hardcoded.
/// </summary>
public sealed partial class SystemStateService(
    INetworkAdapterService adapterService,
    IDnsProbe probe,
    IPrivilegeService privilege,
    IAdapterStore adapterStore,
    ILogger<SystemStateService> logger) : ObservableObject, IDisposable
{
    public const int MaxHistory = 60;
    public const string ProbeDomain = "cloudflare.com";

    private readonly SemaphoreSlim _gate = new(1, 1);

    [ObservableProperty]
    private IReadOnlyList<NetworkAdapterInfo> _adapters = [];

    [ObservableProperty]
    private NetworkAdapterInfo? _activeAdapter;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private double? _currentLatencyMs;

    [ObservableProperty]
    private string? _latencyError;

    [ObservableProperty]
    private DateTime? _lastUpdated;

    [ObservableProperty]
    private string _backgroundOperation = "Idle";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Latency samples of the current session (NaN = failed query).</summary>
    public void Dispose() => _gate.Dispose();

    public ObservableCollection<double> LatencyHistory { get; } = [];

    public bool IsElevated => privilege.IsElevated;

    /// <summary>When this app last attempted a DNS change; lets monitoring tell its own changes from outside ones.</summary>
    public DateTime? LastAppChangeUtc { get; private set; }

    public void MarkAppChange() => LastAppChangeUtc = DateTime.UtcNow;

    public DnsProvider? ActiveProvider
    {
        get
        {
            var dns = ActiveAdapter?.Dns;
            if (dns is null)
            {
                return null;
            }

            return BuiltInProviders.All.FirstOrDefault(p =>
                dns.IPv4Servers.Concat(dns.IPv6Servers).Any(p.Matches));
        }
    }

    public IPAddress? ProbeTarget =>
        ActiveAdapter?.Dns.Primary(false) ?? ActiveAdapter?.Dns.Primary(true);

    public async Task RefreshAsync(bool measureLatency, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            return; // A refresh is already running.
        }

        try
        {
            IsBusy = true;
            BackgroundOperation = "Reading network configuration...";
            var list = await adapterService.GetAdaptersAsync(ct);
            Adapters = list;
            try
            {
                await adapterStore.RecordAsync(list, ct);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException)
            {
                logger.LogWarning(ex, "Could not record adapters");
            }

            ActiveAdapter = ActiveAdapterSelector.Select(list);
            IsConnected = ActiveAdapter is { HasGateway: true } && System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
            OnPropertyChanged(nameof(ActiveProvider));
            OnPropertyChanged(nameof(ProbeTarget));

            if (measureLatency)
            {
                await MeasureAsync(ct);
            }

            LastUpdated = DateTime.Now;
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "System refresh failed");
        }
        finally
        {
            BackgroundOperation = "Idle";
            IsBusy = false;
            _gate.Release();
        }
    }

    private async Task MeasureAsync(CancellationToken ct)
    {
        var target = ProbeTarget;
        if (target is null)
        {
            CurrentLatencyMs = null;
            LatencyError = "No DNS server configured on the active adapter.";
            return;
        }

        BackgroundOperation = $"Measuring DNS latency ({target})...";
        var r = await probe.ProbeAsync(target, ProbeDomain, TimeSpan.FromSeconds(2), ct);
        if (r.Success)
        {
            CurrentLatencyMs = r.ResponseTimeMs;
            LatencyError = null;
            Push(r.ResponseTimeMs);
        }
        else
        {
            CurrentLatencyMs = null;
            LatencyError = r.Error ?? r.ResponseCode;
            Push(double.NaN);
        }
    }

    private void Push(double v)
    {
        LatencyHistory.Add(v);
        while (LatencyHistory.Count > MaxHistory)
        {
            LatencyHistory.RemoveAt(0);
        }
    }
}




