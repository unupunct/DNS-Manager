using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DNSManagerPro.App.Controls;
using DNSManagerPro.App.Services;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Benchmark;
using DNSManagerPro.Core.Providers;
using DNSManagerPro.Core.Settings;
using DNSManagerPro.Infrastructure;
using CacheMode = DNSManagerPro.Core.Benchmark.CacheMode;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.App.ViewModels;

public sealed partial class BenchTargetItem(string name, string detail, string? providerId, IPAddress? address, string? ipv6, string? dohUrl, bool isCustom, bool isSystem) : ObservableObject
{
    public string Name { get; } = name;

    public string Detail { get; } = detail;

    public string? ProviderId { get; } = providerId;

    public IPAddress? Address { get; } = address;

    public string? IPv6Address { get; } = ipv6;

    public string? DohUrl { get; } = dohUrl;

    public bool IsCustom { get; } = isCustom;

    public bool IsSystem { get; } = isSystem;

    public string Key => $"{Name}|{Address}|{ProviderId}";

    [ObservableProperty]
    private bool _isSelected = true;
}

public sealed class ResultRow
{
    public ResultRow(TargetSummary s, Brush brush, double axisMax, double maxMean)
    {
        Summary = s;
        Brush = brush;
        AxisMax = axisMax;
        MaxMean = maxMean;
    }

    public TargetSummary Summary { get; }

    public Brush Brush { get; }

    public double AxisMax { get; }

    public double MaxMean { get; }

    public string RankText => Summary.Rank == 0 ? "-" : Summary.Rank.ToString(CultureInfo.InvariantCulture);

    public string Name => Summary.Name;

    public string Label => $"{Summary.Name} ({Summary.TransportText})";

    public string Detail => $"{Summary.TransportText}  {Summary.Server}";

    private static string F(double? v) => v is null ? "-" : v.Value.ToString("F1", CultureInfo.InvariantCulture);

    public double Median => Summary.Stats?.Median ?? 0;

    public double Mean => Summary.Stats?.Mean ?? 0;

    public double Min => Summary.Stats?.Min ?? 0;

    public double Max => Summary.Stats?.Max ?? 0;

    public bool HasStats => Summary.Stats is not null;

    public string MedianText => F(Summary.Stats?.Median);

    public string MeanText => F(Summary.Stats?.Mean);

    public string MinText => F(Summary.Stats?.Min);

    public string MaxText => F(Summary.Stats?.Max);

    public string P95Text => F(Summary.Stats?.P95);

    public string P99Text => F(Summary.Stats?.P99);

    public string StdDevText => F(Summary.Stats?.StdDev);

    public string SuccessText => (Summary.SuccessRate * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    public double SuccessFraction => Summary.SuccessRate;

    public string TimeoutText => (Summary.TimeoutRate * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    public int Failed => Summary.Failures + Summary.Timeouts;

    public string Notes => string.Join("; ", Summary.Notes ?? []);

    public double MeanFraction => MaxMean <= 0 ? 0 : Mean / MaxMean;

    public string RangeText => Summary.Stats is { } s ? $"{F(s.Min)} / {F(s.Median)} / {F(s.Max)} ms" : "-";

    public bool CanApply => Summary.Rank > 0 && !Summary.Name.StartsWith("Current DNS", StringComparison.Ordinal);
}

public sealed partial class BenchmarkViewModel : ObservableObject, IDisposable
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x38, 0xBD, 0xF8), Color.FromRgb(0x22, 0xC5, 0x5E), Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0xA7, 0x8B, 0xFA),
        Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0x2D, 0xD4, 0xBF), Color.FromRgb(0xFB, 0x92, 0x3C), Color.FromRgb(0x94, 0xA3, 0xB8),
    ];

    private readonly BenchmarkRunner _runner;
    private readonly BenchmarkSession _session;
    private readonly IProviderRepository _providers;
    private readonly SystemStateService _state;
    private readonly IApplyFlow _flow;
    private readonly IDialogService _dialogs;
    private readonly ILogger<BenchmarkViewModel> _log;
    private readonly SettingsService _settings;
    private AppSettings _appliedDefaults;
    private CancellationTokenSource? _cts;
    private List<DnsProvider> _providerCache = [];
    private string _sortKey = "Rank";
    private bool _sortDescending;

    public BenchmarkViewModel(
        BenchmarkRunner runner, BenchmarkSession session, IProviderRepository providers, SystemStateService state,
        IApplyFlow flow, IDialogService dialogs, SettingsService settings, ILogger<BenchmarkViewModel> log)
    {
        _runner = runner;
        _session = session;
        _providers = providers;
        _state = state;
        _flow = flow;
        _dialogs = dialogs;
        _log = log;
        _settings = settings;
        _appliedDefaults = settings.Current;
        _domainsText = string.Join(", ", settings.Current.BenchmarkDomains);
        _repetitionsText = settings.Current.BenchmarkRepetitions.ToString(CultureInfo.InvariantCulture);
        _timeoutText = settings.Current.BenchmarkTimeoutMs.ToString(CultureInfo.InvariantCulture);
        _parallelText = settings.Current.BenchmarkParallelism.ToString(CultureInfo.InvariantCulture);
        _settings.Changed += OnSettingsChanged;
        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BenchmarkSession.LastRun) or nameof(BenchmarkSession.History) && !IsRunning)
            {
                ShowRun(_session.LastRun);
            }
        };
        _ = InitAsync();
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _cts?.Dispose();
    }

    /// <summary>When the user changes the benchmark defaults in Settings, the form follows (only fields whose default changed).</summary>
    private void OnSettingsChanged(AppSettings s)
    {
        var old = _appliedDefaults;
        _appliedDefaults = s;
        if (IsRunning)
        {
            return;
        }

        if (!old.BenchmarkDomains.SequenceEqual(s.BenchmarkDomains))
        {
            DomainsText = string.Join(", ", s.BenchmarkDomains);
        }

        if (old.BenchmarkRepetitions != s.BenchmarkRepetitions)
        {
            RepetitionsText = s.BenchmarkRepetitions.ToString(CultureInfo.InvariantCulture);
        }

        if (old.BenchmarkTimeoutMs != s.BenchmarkTimeoutMs)
        {
            TimeoutText = s.BenchmarkTimeoutMs.ToString(CultureInfo.InvariantCulture);
        }

        if (old.BenchmarkParallelism != s.BenchmarkParallelism)
        {
            ParallelText = s.BenchmarkParallelism.ToString(CultureInfo.InvariantCulture);
        }
    }

    public ObservableCollection<BenchTargetItem> Targets { get; } = [];

    public ObservableCollection<ResultRow> Rows { get; } = [];

    public ObservableCollection<string> Errors { get; } = [];

    public ObservableCollection<ChartSeries> HistorySeries { get; } = [];

    [ObservableProperty]
    private string _customServerText = string.Empty;

    [ObservableProperty]
    private string? _customServerError;

    [ObservableProperty]
    private string _domainsText;

    [ObservableProperty]
    private string _repetitionsText = "5";

    [ObservableProperty]
    private string _timeoutText = "2000";

    [ObservableProperty]
    private string _parallelText = "4";

    [ObservableProperty]
    private string _retriesText = "0";

    [ObservableProperty]
    private CacheMode _cacheModeChoice = CacheMode.Warm;

    [ObservableProperty]
    private RecordKind _recordKindChoice = RecordKind.A;

    [ObservableProperty]
    private bool _includeTcp;

    [ObservableProperty]
    private bool _includeDoh;

    [ObservableProperty]
    private bool _includeIPv6;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMax = 1;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _conclusion = string.Empty;

    [ObservableProperty]
    private string _resultSubtitle = "No benchmark yet. Choose servers and press Start.";

    [ObservableProperty]
    private bool _hasResults;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    [ObservableProperty]
    private string _sortLabel = "Sorted by rank";

    public async Task InitAsync()
    {
        await RefreshTargetsAsync();
        await _session.LoadAsync();
        ShowRun(_session.LastRun);
    }

    /// <summary>Rebuilds the server list from the provider library + current DNS, keeping selection and custom servers.</summary>
    public async Task RefreshTargetsAsync()
    {
        try
        {
            _providerCache = [.. await _providers.GetAllAsync()];
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not load providers for benchmark");
            return;
        }

        var previous = Targets.ToDictionary(t => t.Key, t => t.IsSelected);
        var custom = Targets.Where(t => t.IsCustom).ToList();
        Targets.Clear();
        if (_state.ActiveAdapter?.Dns.Primary(false) is { } sys)
        {
            Targets.Add(new BenchTargetItem("Current DNS", $"{sys} (active adapter)", null, sys, _state.ActiveAdapter.Dns.Primary(true)?.ToString(), null, false, true));
        }

        foreach (var p in _providerCache.Where(p => !string.IsNullOrWhiteSpace(p.IPv4Primary) || !string.IsNullOrWhiteSpace(p.DohUrl)))
        {
            var ip = IPAddress.TryParse(p.IPv4Primary, out var parsed) ? parsed : null;
            Targets.Add(new BenchTargetItem(p.Name, string.Join(" / ", new[] { p.IPv4Primary, p.DohUrl is null ? null : "DoH" }.Where(s => s is not null)), p.Id, ip, p.IPv6Primary, p.DohUrl, false, false));
        }

        foreach (var c in custom)
        {
            Targets.Add(c);
        }

        foreach (var t in Targets)
        {
            if (previous.TryGetValue(t.Key, out var sel))
            {
                t.IsSelected = sel;
            }
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var t in Targets)
        {
            t.IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var t in Targets)
        {
            t.IsSelected = false;
        }
    }

    [RelayCommand]
    private void AddCustom()
    {
        var text = CustomServerText.Trim();
        var v4 = DnsValidator.ValidateIPv4(text);
        var v6 = DnsValidator.ValidateIPv6(text);
        if (!v4.IsValid && !v6.IsValid)
        {
            CustomServerError = v4.Error;
            return;
        }

        var ip = IPAddress.Parse(text);
        if (Targets.Any(t => t.Address?.Equals(ip) == true))
        {
            CustomServerError = "That server is already in the list.";
            return;
        }

        Targets.Add(new BenchTargetItem($"Custom {text}", text, null, ip, null, null, true, false));
        CustomServerText = string.Empty;
        CustomServerError = null;
    }

    [RelayCommand]
    private void RemoveCustom(BenchTargetItem item) => Targets.Remove(item);

    private (BenchmarkOptions? Options, List<string> Errors) ReadOptions()
    {
        var errors = new List<string>();
        var domains = DomainsText.Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(d => d.ToLowerInvariant()).Distinct().ToList();
        bool Int(string s, string what, out int v)
        {
            if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                return true;
            }

            errors.Add($"{what} must be a whole number.");
            return false;
        }

        var ok = Int(RepetitionsText, "Repetitions", out var reps) & Int(TimeoutText, "Timeout (ms)", out var timeout)
            & Int(ParallelText, "Parallel queries", out var par) & Int(RetriesText, "Retries", out var retries);
        if (!ok)
        {
            return (null, errors);
        }

        return (new BenchmarkOptions(domains, reps, TimeSpan.FromMilliseconds(timeout), par, CacheModeChoice, RecordKindChoice, retries), errors);
    }

    private List<BenchmarkTarget> BuildTargets()
    {
        var list = new List<BenchmarkTarget>();
        foreach (var t in Targets.Where(t => t.IsSelected))
        {
            if (t.Address is not null)
            {
                list.Add(new BenchmarkTarget(t.Name, t.ProviderId, t.Address, DnsTransport.Udp));
                if (IncludeTcp)
                {
                    list.Add(new BenchmarkTarget(t.Name, t.ProviderId, t.Address, DnsTransport.Tcp));
                }
            }

            if (IncludeIPv6 && IPAddress.TryParse(t.IPv6Address, out var v6))
            {
                list.Add(new BenchmarkTarget($"{t.Name} (IPv6)", t.ProviderId, v6, DnsTransport.Udp));
            }

            if (IncludeDoh && !string.IsNullOrWhiteSpace(t.DohUrl))
            {
                list.Add(new BenchmarkTarget(t.Name, t.ProviderId, null, DnsTransport.Doh, t.DohUrl));
            }
        }

        return list;
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        Errors.Clear();
        Message = null;
        var (options, parseErrors) = ReadOptions();
        var targets = BuildTargets();
        foreach (var e in parseErrors)
        {
            Errors.Add(e);
        }

        if (options is not null)
        {
            foreach (var e in options.Validate(targets.Count))
            {
                Errors.Add(e);
            }
        }

        if (Errors.Count > 0 || options is null)
        {
            return;
        }

        IsRunning = true;
        _cts = new CancellationTokenSource();
        ProgressValue = 0;
        ProgressMax = 1;
        ProgressText = "Starting...";
        _state.BackgroundOperation = "DNS benchmark running...";
        try
        {
            var progress = new Progress<BenchmarkProgress>(p =>
            {
                ProgressMax = Math.Max(1, p.Total);
                ProgressValue = p.Completed;
                ProgressText = $"{p.Completed} / {p.Total} queries - {p.Current}";
            });
            var run = await _runner.RunAsync(targets, options, progress, _cts.Token);
            await _session.RecordAsync(run);
            ShowRun(run);
            MessageIsError = run.Cancelled;
            Message = run.Cancelled ? $"Stopped early: showing the {run.Samples.Count} queries completed so far." : $"Done: {run.Samples.Count} queries measured.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Benchmark failed");
            MessageIsError = true;
            Message = "Benchmark failed: " + ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            _state.BackgroundOperation = "Idle";
            ProgressText = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();

    private static SolidColorBrush BrushFor(string name)
    {
        var h = name.Aggregate(7, (a, c) => unchecked((a * 31) + c));
        var b = new SolidColorBrush(Palette[Math.Abs(h % Palette.Length)]);
        b.Freeze();
        return b;
    }

    private void ShowRun(BenchmarkRun? run)
    {
        Rows.Clear();
        HistorySeries.Clear();
        HasResults = run is not null && run.Samples.Count > 0;
        if (run is null || run.Samples.Count == 0)
        {
            Conclusion = string.Empty;
            return;
        }

        var ranked = BenchmarkAnalysis.Rank(BenchmarkAnalysis.Summarize(run.Samples));
        _ranked = ranked;
        Conclusion = BenchmarkAnalysis.Conclusion(ranked);
        ResultSubtitle = string.Create(
            CultureInfo.InvariantCulture,
            $"Run {run.StartedUtc.ToLocalTime():g} - {run.Samples.Count} queries - {run.Options.Repetitions}x {run.Options.Domains.Count} domains - {run.Options.CacheMode} cache, {run.Options.RecordKind} records{(run.Cancelled ? " - stopped early" : string.Empty)}");
        ApplySort();
        BuildHistory();
    }

    private IReadOnlyList<TargetSummary> _ranked = [];

    private void ApplySort()
    {
        var axisMax = _ranked.Where(r => r.Stats is not null).Select(r => r.Stats!.Max).DefaultIfEmpty(1).Max();
        var maxMean = _ranked.Where(r => r.Stats is not null).Select(r => r.Stats!.Mean).DefaultIfEmpty(1).Max();
        IEnumerable<ResultRow> rows = _ranked.Select(r => new ResultRow(r, BrushFor(r.Name), axisMax, maxMean));
        Func<ResultRow, object> key = _sortKey switch
        {
            "Name" => r => r.Name,
            "Median" => r => r.HasStats ? r.Median : double.MaxValue,
            "Mean" => r => r.HasStats ? r.Mean : double.MaxValue,
            "Min" => r => r.HasStats ? r.Min : double.MaxValue,
            "Max" => r => r.HasStats ? r.Max : double.MaxValue,
            "P95" => r => r.Summary.Stats?.P95 ?? double.MaxValue,
            "StdDev" => r => r.Summary.Stats?.StdDev ?? double.MaxValue,
            "Success" => r => r.SuccessFraction,
            _ => r => r.Summary.Rank == 0 ? int.MaxValue : r.Summary.Rank,
        };
        var ordered = _sortDescending ? rows.OrderByDescending(key) : rows.OrderBy(key);
        Rows.Clear();
        foreach (var r in ordered)
        {
            Rows.Add(r);
        }

        SortLabel = $"Sorted by {_sortKey.ToLowerInvariant()} ({(_sortDescending ? "descending" : "ascending")})";
    }

    [RelayCommand]
    private void Sort(string key)
    {
        _sortDescending = _sortKey == key && !_sortDescending;
        _sortKey = key;
        ApplySort();
    }

    private void BuildHistory()
    {
        var runs = _session.History.Where(r => !r.Cancelled).OrderBy(r => r.StartedUtc).ToList();
        var keys = runs.SelectMany(r => r.Samples.Select(s => s.TargetKey)).Distinct().ToList();
        foreach (var k in keys)
        {
            var values = runs.Select(r =>
            {
                var ok = r.Samples.Where(s => s.TargetKey == k && s.Success).Select(s => s.Milliseconds).ToList();
                return LatencyStats.Compute(ok)?.Median ?? double.NaN;
            }).ToList();
            var name = runs.SelectMany(r => r.Samples).First(s => s.TargetKey == k) is var first ? $"{first.TargetName} ({first.Transport})" : k;
            HistorySeries.Add(new ChartSeries(name, BrushFor(name), values));
        }
    }

    // ----- apply / export -----

    [RelayCommand]
    private Task ApplyFastestAsync()
    {
        var top = Rows.Where(r => r.Summary.Rank > 0 && r.Summary.Transport != DnsTransport.Doh).OrderBy(r => r.Summary.Rank).FirstOrDefault();
        if (top is null)
        {
            _dialogs.Notify("Nothing to apply", "No ranked server with a plain DNS address is available in these results.");
            return Task.CompletedTask;
        }

        return ApplyRowAsync(top);
    }

    [RelayCommand]
    private async Task ApplyRowAsync(ResultRow row)
    {
        if (row.Summary.Name.StartsWith("Current DNS", StringComparison.Ordinal))
        {
            _dialogs.Notify("Already in use", "This is the DNS configuration your adapter uses now.");
            return;
        }

        var provider = _providerCache.FirstOrDefault(p => p.Id == row.Summary.ProviderId);
        if (provider is null && IPAddress.TryParse(row.Summary.Server, out var ip))
        {
            var text = ip.ToString();
            provider = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? new DnsProvider($"bench-{text}", row.Summary.Name, "Server from benchmark", string.Empty, text, null, null, null, string.Empty, string.Empty, string.Empty, IsBuiltIn: false)
                : new DnsProvider($"bench-{text}", row.Summary.Name, "Server from benchmark", string.Empty, null, null, text, null, string.Empty, string.Empty, string.Empty, IsBuiltIn: false);
        }

        if (provider is null)
        {
            _dialogs.Notify("Cannot apply", "No server address is known for this result.");
            return;
        }

        await _flow.ApplyProviderAsync(provider);
    }

    private BenchmarkRun? CurrentRun => _session.LastRun;

    private void Save(string title, string filter, string name, Func<BenchmarkRun, string> content)
    {
        if (CurrentRun is not { } run)
        {
            return;
        }

        var path = _dialogs.PickSaveFile(title, filter, name);
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, content(run), new UTF8Encoding(true));
            MessageIsError = false;
            Message = $"Exported to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageIsError = true;
            Message = "Export failed: " + ex.Message;
        }
    }

    private string Stamp => (CurrentRun?.StartedUtc ?? DateTime.UtcNow).ToLocalTime().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

    [RelayCommand]
    private void ExportRawCsv() => Save("Export raw results", "CSV file (*.csv)|*.csv", $"dns-benchmark-raw-{Stamp}.csv", BenchmarkExporter.RawCsv);

    [RelayCommand]
    private void ExportSummaryCsv() => Save("Export summary", "CSV file (*.csv)|*.csv", $"dns-benchmark-summary-{Stamp}.csv",
        r => BenchmarkExporter.SummaryCsv(BenchmarkAnalysis.Rank(BenchmarkAnalysis.Summarize(r.Samples))));

    [RelayCommand]
    private void ExportJson() => Save("Export benchmark (JSON)", "JSON file (*.json)|*.json", $"dns-benchmark-{Stamp}.json", BenchmarkExporter.Json);
}



