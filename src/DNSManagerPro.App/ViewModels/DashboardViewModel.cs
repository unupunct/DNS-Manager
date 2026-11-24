using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DNSManagerPro.App.Services;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Models;

namespace DNSManagerPro.App.ViewModels;

/// <summary>One label/value line in a dashboard card. Tone: "", "good", "warn", "bad".</summary>
public sealed record InfoItem(string Label, string Value, string Tone = "");

public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly SystemStateService _state;
    private readonly INavigationService _nav;
    private readonly IDnsCacheService _cache;
    private readonly IApplyFlow _flow;
    private readonly DispatcherTimer _timer;

    private readonly BenchmarkSession _session;
    private readonly ProfileService _profiles;

    public DashboardViewModel(SystemStateService state, INavigationService nav, IDnsCacheService cache, IApplyFlow flow, BenchmarkSession session, ProfileService profiles)
    {
        _state = state;
        _session = session;
        _profiles = profiles;
        _session.PropertyChanged += OnStateChanged;
        _nav = nav;
        _cache = cache;
        _flow = flow;
        _state.PropertyChanged += OnStateChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += async (_, _) => await _state.RefreshAsync(true);
        _timer.Start();
        Rebuild();
        _ = _state.RefreshAsync(true);
    }

    public ObservableCollection<InfoItem> DnsInfo { get; } = [];

    public ObservableCollection<InfoItem> NetworkInfo { get; } = [];

    public ObservableCollection<double> LatencyHistory => _state.LatencyHistory;

    [ObservableProperty]
    private string _latencyText = "-";

    [ObservableProperty]
    private string _latencyCaption = "Measuring...";

    [ObservableProperty]
    private string _lastBenchmark = "No benchmark run yet";

    [ObservableProperty]
    private string _fastestProvider = "-";

    [ObservableProperty]
    private string _availability = "-";

    [ObservableProperty]
    private string _statsSummary = "Waiting for samples...";

    [ObservableProperty]
    private string _updatedText = "Not yet updated";

    [ObservableProperty]
    private string? _actionMessage;

    [ObservableProperty]
    private bool _actionIsError;

    [RelayCommand]
    private Task RefreshAsync() => _state.RefreshAsync(true);

    [RelayCommand]
    private void ChangeDns() => _nav.NavigateTo(NavSection.Providers);

    [RelayCommand]
    private Task RestoreAutomaticAsync() => _flow.RestoreAutomaticAsync(_state.ActiveAdapter?.Id);

    [RelayCommand]
    private void RunBenchmark() => _nav.NavigateTo(NavSection.Benchmark);

    [RelayCommand]
    private void RunDiagnostics() => _nav.NavigateTo(NavSection.Diagnostics);

    [RelayCommand]
    private void CreateProfile()
    {
        _profiles.StartNew = true;
        _nav.NavigateTo(NavSection.CustomDns);
    }

    [RelayCommand]
    private async Task FlushDnsAsync()
    {
        _state.BackgroundOperation = "Flushing DNS cache...";
        var r = await _cache.FlushAsync();
        _state.BackgroundOperation = "Idle";
        ActionIsError = !r.Success;
        ActionMessage = r.Message;
    }

    public void Dispose()
    {
        _timer.Stop();
        _state.PropertyChanged -= OnStateChanged;
        _session.PropertyChanged -= OnStateChanged;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => Rebuild();

    private static string Join(IEnumerable<System.Net.IPAddress> a) => string.Join(", ", a);

    private static string Or(object? v) => v?.ToString() is { Length: > 0 } s ? s : "-";

    private void Rebuild()
    {
        var a = _state.ActiveAdapter;
        DnsInfo.Clear();
        NetworkInfo.Clear();

        if (a is null)
        {
            DnsInfo.Add(new InfoItem("Status", "No active network adapter", "bad"));
            NetworkInfo.Add(new InfoItem("Connection", "Disconnected", "bad"));
        }
        else
        {
            var d = a.Dns;
            var provider = _state.ActiveProvider;
            DnsInfo.Add(new InfoItem("Provider", provider?.Name ?? (d.IsAutomatic ? "Automatic (from DHCP)" : "Unrecognized / custom"), provider is null ? "" : "good"));
            DnsInfo.Add(new InfoItem("Mode (IPv4)", d.IPv4Automatic ? "Automatic" : "Manual", d.IPv4Automatic ? "" : "warn"));
            DnsInfo.Add(new InfoItem("Mode (IPv6)", d.IPv6Automatic ? "Automatic" : "Manual", d.IPv6Automatic ? "" : "warn"));
            DnsInfo.Add(new InfoItem("Primary IPv4", Or(d.Primary(false))));
            DnsInfo.Add(new InfoItem("Secondary IPv4", Or(d.Secondary(false))));
            DnsInfo.Add(new InfoItem("Primary IPv6", Or(d.Primary(true))));
            DnsInfo.Add(new InfoItem("Secondary IPv6", Or(d.Secondary(true))));
            DnsInfo.Add(new InfoItem("Adapter", a.Name));

            NetworkInfo.Add(new InfoItem("Connection", _state.IsConnected ? "Connected" : "Local only / no gateway", _state.IsConnected ? "good" : "warn"));
            NetworkInfo.Add(new InfoItem("Type", $"{a.Kind}" + (a.SpeedBitsPerSecond > 0 ? $" - {FormatSpeed(a.SpeedBitsPerSecond)}" : string.Empty)));
            NetworkInfo.Add(new InfoItem("Adapter", a.Description));
            NetworkInfo.Add(new InfoItem("Local IPv4", a.IPv4Addresses.Count > 0 ? Join(a.IPv4Addresses) : "-"));
            NetworkInfo.Add(new InfoItem("Local IPv6", a.IPv6Addresses.Count > 0 ? Join(a.IPv6Addresses.Take(1)) : "-"));
            NetworkInfo.Add(new InfoItem("Default gateway", a.Gateways.Count > 0 ? Join(a.Gateways) : "-"));
            NetworkInfo.Add(new InfoItem("DHCP", a.IsDhcpEnabled ? "Enabled" : "Disabled"));
        }

        if (_state.CurrentLatencyMs is { } ms)
        {
            LatencyText = ms.ToString("F0", CultureInfo.InvariantCulture) + " ms";
            LatencyCaption = $"Single direct UDP query to {_state.ProbeTarget} for {SystemStateService.ProbeDomain} (resolver cache may be warm)";
        }
        else
        {
            LatencyText = "-";
            LatencyCaption = _state.LatencyError ?? "Measuring...";
        }

        var valid = _state.LatencyHistory.Where(v => !double.IsNaN(v)).ToList();
        if (_state.LatencyHistory.Count > 0)
        {
            var ok = valid.Count * 100.0 / _state.LatencyHistory.Count;
            if (_session.LastRun is null)
            {
                Availability = ok.ToString("F0", CultureInfo.InvariantCulture) + "% (this session)";
            }

            StatsSummary = valid.Count > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{_state.LatencyHistory.Count} sample(s) - min {valid.Min():F0} ms - avg {valid.Average():F0} ms - max {valid.Max():F0} ms")
                : $"{_state.LatencyHistory.Count} sample(s) - all failed";
        }

        ShowBenchmark(_session.LastRun);
        UpdatedText = _state.LastUpdated is { } t ? $"Updated {t:HH:mm:ss}" : "Not yet updated";
    }

    private void ShowBenchmark(DNSManagerPro.Core.Benchmark.BenchmarkRun? run)
    {
        if (run is null || run.Samples.Count == 0)
        {
            LastBenchmark = "No benchmark run yet";
            FastestProvider = "-";
            return;
        }

        var ranked = DNSManagerPro.Core.Benchmark.BenchmarkAnalysis.Rank(DNSManagerPro.Core.Benchmark.BenchmarkAnalysis.Summarize(run.Samples));
        LastBenchmark = $"{run.StartedUtc.ToLocalTime():g} - {run.Samples.Count} queries";
        var top = ranked.Where(r => r.Rank > 0).OrderBy(r => r.Rank).ToList();
        if (top.Count == 0)
        {
            FastestProvider = "No server could be ranked";
        }
        else
        {
            var tied = top.Count > 1 && top[1].TiedWithPrevious;
            FastestProvider = string.Create(CultureInfo.InvariantCulture, $"{top[0].Name} ({top[0].Stats!.Median:F0} ms median){(tied ? " - within noise of " + top[1].Name : string.Empty)}");
        }

        var current = ranked.FirstOrDefault(r => r.Name == "Current DNS");
        var basis = current ?? ranked.OrderByDescending(r => r.Queries).FirstOrDefault();
        if (basis is not null)
        {
            Availability = string.Create(CultureInfo.InvariantCulture, $"{basis.SuccessRate * 100:F1}% ({basis.Name}, last benchmark)");
        }
    }

    private static string FormatSpeed(long bps) =>
        bps >= 1_000_000_000 ? $"{bps / 1_000_000_000.0:0.#} Gbps" : $"{bps / 1_000_000.0:0} Mbps";
}



