using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DNSManagerPro.App.Services;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Diagnostics;
using DNSManagerPro.Core.Monitoring;
using DNSManagerPro.Core.Providers;
using DNSManagerPro.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.App.ViewModels;

public sealed class StepViewModel(DiagnosticStep step)
{
    public string Title => step.Title;

    public string Detail => step.Detail;

    public string Glyph => step.Status switch
    {
        DiagnosticStatus.Pass => "\uE73E",
        DiagnosticStatus.Warning => "\uE7BA",
        DiagnosticStatus.Fail => "\uE711",
        DiagnosticStatus.Info => "\uE946",
        _ => "\uE738",
    };

    public string Tone => step.Status switch { DiagnosticStatus.Pass => "good", DiagnosticStatus.Warning => "warn", DiagnosticStatus.Fail => "bad", _ => string.Empty };
}

public sealed class FindingViewModel(DiagnosticFinding f)
{
    public string Detected => f.Detected;

    public IReadOnlyList<string> Causes => f.PossibleCauses;

    public string? Suggestion => f.Suggestion is null ? null : "Suggested next step: " + f.Suggestion;

    public string Severity => f.Severity.ToString();

    public string Tone => f.Severity switch { FindingSeverity.Critical => "bad", FindingSeverity.Warning => "warn", _ => string.Empty };

    public bool IsProblem => f.Severity != FindingSeverity.Info;
}

public sealed class LookupCardViewModel(LookupResult r)
{
    public string Title => $"{r.Type}  {r.Domain}";

    public string Server => $"{r.ServerName} ({r.Server})";

    public string Time => r.TimedOut ? "timed out" : r.Milliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms";

    public string Code => r.ResponseCode;

    public string CodeTone => r.Success ? "good" : r.Answered ? "warn" : "bad";

    public IReadOnlyList<DnsRecordInfo> Records => r.Records;

    public bool HasRecords => r.Records.Count > 0;

    public string? Error => r.Error is null ? null : r.Error;

    public string Empty => r.Answered && r.Records.Count == 0 ? "The server answered, but returned no records of this type." : string.Empty;
}

public sealed class PropagationRowViewModel(LookupResult r, LookupType type)
{
    public string Resolver => r.ServerName;

    public string Server => r.Server.ToString();

    public string Code => r.ResponseCode;

    public string Tone => r.Success ? "good" : "bad";

    public string Time => r.TimedOut ? "timeout" : r.Milliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms";

    public string Answers => r.Records.Count == 0 ? (r.Answered ? "(none)" : r.Error ?? "-") : string.Join(Environment.NewLine, r.Records.Where(x => x.Type == type.ToString()).Select(x => x.Data));

    public string Ttl => r.Records.FirstOrDefault(x => x.Type == type.ToString())?.Ttl.ToString(CultureInfo.InvariantCulture) ?? "-";
}

public sealed class CacheRowViewModel(DnsCacheEntry e)
{
    public string Name => e.Name;

    public string Type => e.Type;

    public string Data => e.Data;

    public string Ttl => e.TimeToLive.ToString(CultureInfo.InvariantCulture) + " s";

    public string Status => e.Status;
}

public sealed class MonitorEventViewModel(MonitorEvent e)
{
    public string Time => e.TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string Message => e.Message;

    public string Tone => e.Kind switch { MonitorEventKind.ResolutionFailure => "bad", MonitorEventKind.Recovered => "good", _ => "warn" };
}

public sealed partial class DiagnosticsViewModel : ObservableObject, IDisposable
{
    private readonly DiagnosticsWorkflow _workflow;
    private readonly IDnsLookupClient _lookup;
    private readonly IDnsCacheInspector _cacheInspector;
    private readonly IDnsCacheService _cacheService;
    private readonly IDiagnosticsRepository _runs;
    private readonly IMonitorRepository _monitorRepo;
    private readonly IProviderRepository _providers;
    private readonly MonitorService _monitor;
    private readonly SettingsService _settings;
    private readonly SystemStateService _state;
    private readonly IDialogService _dialogs;
    private readonly IElevationService _elevation;
    private readonly ILogger<DiagnosticsViewModel> _log;
    private CancellationTokenSource? _cts;
    private DiagnosticRun? _lastRun;
    private List<DnsProvider> _providerList = [];
    private IReadOnlyList<DnsCacheEntry> _cacheAll = [];

    public DiagnosticsViewModel(
        DiagnosticsWorkflow workflow, IDnsLookupClient lookup, IDnsCacheInspector cacheInspector, IDnsCacheService cacheService,
        IDiagnosticsRepository runs, IMonitorRepository monitorRepo, IProviderRepository providers, MonitorService monitor,
        SettingsService settings, SystemStateService state, IDialogService dialogs, IElevationService elevation, ILogger<DiagnosticsViewModel> log)
    {
        _elevation = elevation;
        _workflow = workflow;
        _lookup = lookup;
        _cacheInspector = cacheInspector;
        _cacheService = cacheService;
        _runs = runs;
        _monitorRepo = monitorRepo;
        _providers = providers;
        _monitor = monitor;
        _settings = settings;
        _state = state;
        _dialogs = dialogs;
        _log = log;

        foreach (var (key, label) in new[] { ("auto", "Automated check"), ("lookup", "Lookup"), ("prop", "Propagation"), ("cache", "DNS cache"), ("mon", "Monitoring") })
        {
            Tabs.Add(new ChoiceOption(label, key, o => Tab = o.Value ?? "auto") { IsSelected = key == "auto" });
        }

        foreach (var t in Enum.GetValues<LookupType>())
        {
            TypeOptions.Add(new ChoiceOption(t == LookupType.PTR ? "PTR (reverse)" : t.ToString(), t.ToString(), o => SelectedType = Enum.Parse<LookupType>(o.Value ?? "A")) { IsSelected = t == LookupType.A });
        }

        foreach (var m in new[] { 1, 5, 15, 30, 60 })
        {
            IntervalOptions.Add(new ChoiceOption(m == 60 ? "1 h" : $"{m} min", m.ToString(CultureInfo.InvariantCulture), o => SetInterval(int.Parse(o.Value ?? "5", CultureInfo.InvariantCulture)))
            {
                IsSelected = m == _settings.Current.MonitoringIntervalMinutes,
            });
        }

        _monitorEnabled = _settings.Current.MonitoringEnabled;
        _notifyChange = _settings.Current.NotifyOnChange;
        _notifyFailure = _settings.Current.NotifyOnFailure;
        _settings.Changed += s => App_Dispatch(() => SyncFromSettings(s));
        _monitor.EventRaised += ev => App_Dispatch(() => _ = LoadMonitorEventsAsync());
        _monitor.StatusChanged += () => App_Dispatch(() => MonitorStatus = _monitor.LastStatus);
        MonitorStatus = _monitor.LastStatus;
    }

    private static void App_Dispatch(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess())
        {
            a();
        }
        else
        {
            d.BeginInvoke(a);
        }
    }

    public ObservableCollection<ChoiceOption> Tabs { get; } = [];

    public ObservableCollection<ChoiceOption> TypeOptions { get; } = [];

    public ObservableCollection<ChoiceOption> ServerOptions { get; } = [];

    public ObservableCollection<ChoiceOption> IntervalOptions { get; } = [];

    public ObservableCollection<StepViewModel> Steps { get; } = [];

    public ObservableCollection<FindingViewModel> Problems { get; } = [];

    public ObservableCollection<FindingViewModel> Notes { get; } = [];

    public ObservableCollection<LookupCardViewModel> Lookups { get; } = [];

    public ObservableCollection<PropagationRowViewModel> PropagationRows { get; } = [];

    public ObservableCollection<CacheRowViewModel> CacheRows { get; } = [];

    public ObservableCollection<MonitorEventViewModel> MonitorEvents { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuto), nameof(IsLookup), nameof(IsProp), nameof(IsCache), nameof(IsMon), nameof(ShowQueryBar))]
    private string _tab = "auto";

    public bool IsAuto => Tab == "auto";

    public bool IsLookup => Tab == "lookup";

    public bool IsProp => Tab == "prop";

    public bool IsCache => Tab == "cache";

    public bool IsMon => Tab == "mon";

    public bool ShowQueryBar => IsLookup || IsProp;

    [ObservableProperty]
    private LookupType _selectedType = LookupType.A;

    [ObservableProperty]
    private string _domain = "example.com";

    [ObservableProperty]
    private string _customServer = string.Empty;

    [ObservableProperty]
    private string? _queryError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCheckCommand), nameof(StopCheckCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _overallText = "Run the automated check to test your connection and DNS step by step.";

    [ObservableProperty]
    private string _overallTone = string.Empty;

    [ObservableProperty]
    private bool _hasReport;

    [ObservableProperty]
    private string _propagationSummary = string.Empty;

    [ObservableProperty]
    private string _cacheFilter = string.Empty;

    [ObservableProperty]
    private string _cacheStatus = string.Empty;

    [ObservableProperty]
    private bool _cacheNeedsAdmin;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    [ObservableProperty]
    private bool _monitorEnabled;

    [ObservableProperty]
    private bool _notifyChange;

    [ObservableProperty]
    private bool _notifyFailure;

    [ObservableProperty]
    private string _monitorStatus = string.Empty;

    partial void OnCacheFilterChanged(string value) => ApplyCacheFilter();

    partial void OnMonitorEnabledChanged(bool value) => _settings.Update(s => s with { MonitoringEnabled = value });

    partial void OnNotifyChangeChanged(bool value) => _settings.Update(s => s with { NotifyOnChange = value });

    partial void OnNotifyFailureChanged(bool value) => _settings.Update(s => s with { NotifyOnFailure = value });

    /// <summary>Keeps this tab in step when the same options are changed on the Settings page.</summary>
    private void SyncFromSettings(DNSManagerPro.Core.Settings.AppSettings s)
    {
        MonitorEnabled = s.MonitoringEnabled;
        NotifyChange = s.NotifyOnChange;
        NotifyFailure = s.NotifyOnFailure;
        foreach (var o in IntervalOptions)
        {
            if (o.Value == s.MonitoringIntervalMinutes.ToString(CultureInfo.InvariantCulture))
            {
                o.IsSelected = true;
            }
        }
    }

    private void SetInterval(int minutes)
    {
        if (_settings.Current.MonitoringIntervalMinutes != minutes)
        {
            _settings.Update(s => s with { MonitoringIntervalMinutes = minutes });
        }
    }

    public void Dispose() => _cts?.Dispose();

    /// <summary>Called when the page is shown.</summary>
    public async Task OnShownAsync()
    {
        try
        {
            _providerList = [.. (await _providers.GetAllAsync()).Where(p => !string.IsNullOrWhiteSpace(p.IPv4Primary))];
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not load providers");
        }

        var selected = ServerOptions.FirstOrDefault(o => o.IsSelected)?.Value;
        ServerOptions.Clear();
        if (_state.ActiveAdapter?.Dns.Primary(false) is { } sys)
        {
            ServerOptions.Add(new ChoiceOption($"Current DNS ({sys})", sys.ToString(), _ => { }));
        }

        foreach (var p in _providerList)
        {
            ServerOptions.Add(new ChoiceOption($"{p.Name} ({p.IPv4Primary})", p.IPv4Primary, _ => { }));
        }

        ServerOptions.Add(new ChoiceOption("Custom server...", "custom", _ => { }));
        (ServerOptions.FirstOrDefault(o => o.Value == selected) ?? ServerOptions[0]).IsSelected = true;
        await LoadMonitorEventsAsync();
    }

    // ---------- automated check ----------

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunCheckAsync()
    {
        IsRunning = true;
        _cts = new CancellationTokenSource();
        Steps.Clear();
        Problems.Clear();
        Notes.Clear();
        HasReport = false;
        Message = null;
        OverallText = "Running checks...";
        OverallTone = string.Empty;
        _state.BackgroundOperation = "Running diagnostics...";
        try
        {
            var progress = new Progress<DiagnosticStep>(s => Steps.Add(new StepViewModel(s)));
            var run = await _workflow.RunAsync(progress, _cts.Token);
            _lastRun = run;
            foreach (var f in run.Findings)
            {
                (f.Severity == FindingSeverity.Info ? Notes : Problems).Add(new FindingViewModel(f));
            }

            (OverallText, OverallTone) = run.Overall switch
            {
                DiagnosticOverall.Healthy => ("No problems detected.", "good"),
                DiagnosticOverall.Warnings => ("Some warnings were found.", "warn"),
                _ => ("Problems were detected.", "bad"),
            };
            HasReport = true;
            try
            {
                await _runs.SaveAsync(run);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Could not save diagnostic run");
            }
        }
        catch (OperationCanceledException)
        {
            OverallText = "Stopped.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Diagnostics failed");
            OverallText = "The check failed: " + ex.Message;
            OverallTone = "bad";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            _state.BackgroundOperation = "Idle";
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void StopCheck() => _cts?.Cancel();

    [RelayCommand]
    private void ExportReport()
    {
        if (_lastRun is null)
        {
            return;
        }

        var path = _dialogs.PickSaveFile("Save diagnostic report", "Text file (*.txt)|*.txt", $"dns-diagnostics-{_lastRun.StartedUtc.ToLocalTime():yyyyMMdd-HHmm}.txt");
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, _lastRun.ReportText);
            Info($"Report saved to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail("Could not save the report: " + ex.Message);
        }
    }

    // ---------- lookup / propagation ----------

    private bool TryQuery(out string domain, out LookupType type)
    {
        domain = Domain.Trim().TrimEnd('.');
        type = SelectedType;
        QueryError = null;
        if (IPAddress.TryParse(domain, out _))
        {
            if (type != LookupType.PTR)
            {
                type = LookupType.PTR;
                (TypeOptions.First(o => o.Value == "PTR")).IsSelected = true;
            }

            return true;
        }

        var v = DnsValidator.ValidateHostName(domain, "Domain");
        if (domain.Length == 0 || !v.IsValid || (!domain.Contains('.', StringComparison.Ordinal) && !IsIdn(domain)))
        {
            QueryError = v.IsValid ? "Enter a domain name (for example example.com) or an IP address." : v.Error;
            return false;
        }

        if (type == LookupType.PTR)
        {
            QueryError = "Reverse (PTR) lookups need an IP address.";
            return false;
        }

        return true;
    }

    private static bool IsIdn(string s) => s.Any(c => c > 127);

    private (IPAddress Ip, string Name)? ChosenServer()
    {
        var o = ServerOptions.FirstOrDefault(x => x.IsSelected);
        if (o is null)
        {
            QueryError = "Choose a server.";
            return null;
        }

        var text = o.Value == "custom" ? CustomServer.Trim() : o.Value;
        var v4 = DnsValidator.ValidateIPv4(text);
        var v6 = DnsValidator.ValidateIPv6(text);
        if (!v4.IsValid && !v6.IsValid)
        {
            QueryError = o.Value == "custom" ? "Enter a valid IPv4 or IPv6 address for the custom server." : v4.Error;
            return null;
        }

        return (IPAddress.Parse(text!), o.Value == "custom" ? $"Custom {text}" : o.Label.Split(" (")[0]);
    }

    [RelayCommand]
    private async Task LookupAsync()
    {
        if (!TryQuery(out var domain, out var type) || ChosenServer() is not { } s)
        {
            return;
        }

        await RunLookupsAsync(domain, [type], s);
    }

    [RelayCommand]
    private async Task LookupAllAsync()
    {
        if (!TryQuery(out var domain, out _) || ChosenServer() is not { } s)
        {
            return;
        }

        if (IPAddress.TryParse(domain, out _))
        {
            await RunLookupsAsync(domain, [LookupType.PTR], s);
        }
        else
        {
            await RunLookupsAsync(domain, [LookupType.A, LookupType.AAAA, LookupType.MX, LookupType.TXT, LookupType.NS, LookupType.CNAME, LookupType.SOA], s);
        }
    }

    private async Task RunLookupsAsync(string domain, LookupType[] types, (IPAddress Ip, string Name) s)
    {
        _state.BackgroundOperation = "DNS lookup...";
        try
        {
            foreach (var t in types)
            {
                var r = await _lookup.LookupAsync(s.Ip, s.Name, domain, t, TimeSpan.FromSeconds(4));
                Lookups.Insert(0, new LookupCardViewModel(r));
                while (Lookups.Count > 40)
                {
                    Lookups.RemoveAt(Lookups.Count - 1);
                }
            }
        }
        finally
        {
            _state.BackgroundOperation = "Idle";
        }
    }

    [RelayCommand]
    private void ClearLookups() => Lookups.Clear();

    [RelayCommand]
    private async Task CheckPropagationAsync()
    {
        if (!TryQuery(out var domain, out var type))
        {
            return;
        }

        if (type == LookupType.PTR)
        {
            QueryError = "Propagation checks apply to forward records (A, AAAA, MX, TXT, NS, CNAME, SOA).";
            return;
        }

        PropagationRows.Clear();
        PropagationSummary = "Querying resolvers...";
        _state.BackgroundOperation = "Checking propagation...";
        try
        {
            var resolvers = new List<(IPAddress Ip, string Name)>();
            if (_state.ActiveAdapter?.Dns.Primary(false) is { } sys)
            {
                resolvers.Add((sys, "Current DNS"));
            }

            resolvers.AddRange(_providerList.Select(p => (IPAddress.Parse(p.IPv4Primary!), p.Name)));
            var tasks = resolvers.Select(r => _lookup.LookupAsync(r.Ip, r.Name, domain, type, TimeSpan.FromSeconds(4))).ToList();
            var results = await Task.WhenAll(tasks);
            foreach (var r in results)
            {
                PropagationRows.Add(new PropagationRowViewModel(r, type));
            }

            PropagationSummary = PropagationAnalysis.Summarize(results, type).Text;
        }
        finally
        {
            _state.BackgroundOperation = "Idle";
        }
    }

    // ---------- cache ----------

    [RelayCommand]
    private async Task RefreshCacheAsync()
    {
        CacheStatus = "Reading the Windows DNS cache...";
        try
        {
            _cacheAll = await _cacheInspector.GetEntriesAsync();
            CacheNeedsAdmin = false;
            ApplyCacheFilter();
        }
        catch (UnauthorizedAccessException ex)
        {
            CacheRows.Clear();
            CacheNeedsAdmin = true;
            CacheStatus = ex.Message + " Windows does not expose the cache to standard users.";
        }
        catch (Exception ex)
        {
            CacheRows.Clear();
            CacheStatus = "Could not read the cache: " + ex.Message;
        }
    }

    private void ApplyCacheFilter()
    {
        var q = CacheFilter.Trim();
        CacheRows.Clear();
        foreach (var e in _cacheAll.Where(e => q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Data.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(1000))
        {
            CacheRows.Add(new CacheRowViewModel(e));
        }

        CacheStatus = $"{CacheRows.Count} shown of {_cacheAll.Count} cached entries.";
    }

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (!_elevation.RestartAsAdministrator(null))
        {
            Fail("Administrator permission was not granted.");
        }
    }

    [RelayCommand]
    private async Task FlushCacheAsync()
    {
        var r = await _cacheService.FlushAsync();
        if (r.Success)
        {
            Info(r.Message);
            await RefreshCacheAsync();
        }
        else
        {
            Fail(r.Message);
        }
    }

    // ---------- monitoring ----------

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        await _monitor.CheckNowAsync();
        await LoadMonitorEventsAsync();
        MonitorStatus = _monitor.LastStatus;
    }

    private async Task LoadMonitorEventsAsync()
    {
        try
        {
            var events = await _monitorRepo.GetRecentAsync(15);
            MonitorEvents.Clear();
            foreach (var e in events)
            {
                MonitorEvents.Add(new MonitorEventViewModel(e));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not load monitor events");
        }
    }

    private void Info(string text)
    {
        MessageIsError = false;
        Message = text;
    }

    private void Fail(string text)
    {
        MessageIsError = true;
        Message = text;
    }
}


