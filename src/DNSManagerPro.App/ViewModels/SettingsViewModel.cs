using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DNSManagerPro.App.Services;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Providers;
using DNSManagerPro.Core.Settings;
using DNSManagerPro.Data;
using DNSManagerPro.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IStartupRegistration _startup;
    private readonly SystemStateService _state;
    private readonly DatabaseMaintenance _maintenance;
    private readonly IDialogService _dialogs;
    private readonly BenchmarkSession _session;
    private readonly ILogger<SettingsViewModel> _log;
    private bool _syncing;

    public SettingsViewModel(
        SettingsService settings, IStartupRegistration startup, SystemStateService state, DatabaseMaintenance maintenance,
        IDialogService dialogs, BenchmarkSession session, ILogger<SettingsViewModel> log)
    {
        _settings = settings;
        _startup = startup;
        _state = state;
        _maintenance = maintenance;
        _dialogs = dialogs;
        _session = session;
        _log = log;

        foreach (var m in new[] { 1, 5, 15, 30, 60 })
        {
            IntervalOptions.Add(new ChoiceOption(m == 60 ? "1 h" : $"{m} min", m.ToString(CultureInfo.InvariantCulture), o => SetInterval(o)));
        }

        foreach (var (p, label) in new[] { (Ipv6Preference.Auto, "Automatic"), (Ipv6Preference.Always, "Always include"), (Ipv6Preference.Never, "Never change") })
        {
            Ipv6Options.Add(new ChoiceOption(label, p.ToString(), o => SetIpv6(o)));
        }

        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemStateService.Adapters))
            {
                BuildAdapterOptions();
            }
        };
        _settings.Changed += s => Dispatch(() => Sync(s));
        BuildAdapterOptions();
        Sync(_settings.Current);
        _ = _state.RefreshAsync(false);
    }

    public ObservableCollection<ChoiceOption> IntervalOptions { get; } = [];

    public ObservableCollection<ChoiceOption> Ipv6Options { get; } = [];

    public ObservableCollection<ChoiceOption> AdapterOptions { get; } = [];

    public string DatabaseFile { get; } = AppPaths.Database;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _launchMinimized;

    [ObservableProperty]
    private bool _confirmDnsChanges;

    [ObservableProperty]
    private bool _monitorEnabled;

    [ObservableProperty]
    private bool _notifyChange;

    [ObservableProperty]
    private bool _notifyFailure;

    [ObservableProperty]
    private bool _preferEncryptedDns;

    [ObservableProperty]
    private string _repetitionsText = "5";

    [ObservableProperty]
    private string _timeoutText = "2000";

    [ObservableProperty]
    private string _parallelText = "4";

    [ObservableProperty]
    private string _domainsText = string.Empty;

    [ObservableProperty]
    private string? _benchmarkMessage;

    [ObservableProperty]
    private bool _benchmarkMessageIsError;

    [ObservableProperty]
    private string? _generalMessage;

    [ObservableProperty]
    private string? _dataMessage;

    [ObservableProperty]
    private bool _dataMessageIsError;

    // ---- General / monitoring / network toggles persist immediately --------------------------------------------

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        if (ApplyStartup(value, LaunchMinimized))
        {
            _settings.Update(s => s with { StartWithWindows = value });
        }
        else
        {
            Revert(() => StartWithWindows = !value);
        }
    }

    partial void OnLaunchMinimizedChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        if (StartWithWindows && !ApplyStartup(true, value))
        {
            Revert(() => LaunchMinimized = !value);
            return;
        }

        _settings.Update(s => s with { LaunchMinimized = value });
    }

    partial void OnMinimizeToTrayChanged(bool value) => Save(s => s with { MinimizeToTray = value });

    partial void OnConfirmDnsChangesChanged(bool value) => Save(s => s with { ConfirmDnsChanges = value });

    partial void OnMonitorEnabledChanged(bool value) => Save(s => s with { MonitoringEnabled = value });

    partial void OnNotifyChangeChanged(bool value) => Save(s => s with { NotifyOnChange = value });

    partial void OnNotifyFailureChanged(bool value) => Save(s => s with { NotifyOnFailure = value });

    partial void OnPreferEncryptedDnsChanged(bool value) => Save(s => s with { PreferEncryptedDns = value });

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (!_syncing)
        {
            _settings.Update(change);
        }
    }

    private void Revert(Action undo)
    {
        _syncing = true;
        try
        {
            undo();
        }
        finally
        {
            _syncing = false;
        }
    }

    private bool ApplyStartup(bool enabled, bool minimized)
    {
        try
        {
            _startup.Configure(enabled, minimized);
            GeneralMessage = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or ArgumentException or IOException)
        {
            _log.LogWarning(ex, "Could not update the startup entry");
            GeneralMessage = "Could not change the Windows startup entry: " + ex.Message;
            return false;
        }
    }

    private void SetInterval(ChoiceOption o)
    {
        if (!_syncing && int.TryParse(o.Value, CultureInfo.InvariantCulture, out var m) && m != _settings.Current.MonitoringIntervalMinutes)
        {
            _settings.Update(s => s with { MonitoringIntervalMinutes = m });
        }
    }

    private void SetIpv6(ChoiceOption o)
    {
        if (!_syncing && Enum.TryParse<Ipv6Preference>(o.Value, out var p) && p != _settings.Current.Ipv6Handling)
        {
            _settings.Update(s => s with { Ipv6Handling = p });
        }
    }

    private void SetDefaultAdapter(ChoiceOption o)
    {
        var id = string.IsNullOrEmpty(o.Value) ? null : o.Value;
        if (!_syncing && id != _settings.Current.DefaultAdapterId)
        {
            _settings.Update(s => s with { DefaultAdapterId = id });
        }
    }

    private void BuildAdapterOptions()
    {
        Dispatch(() =>
        {
            _syncing = true;
            try
            {
                AdapterOptions.Clear();
                var current = _settings.Current.DefaultAdapterId;
                AdapterOptions.Add(new ChoiceOption("Automatic (active adapter)", null, SetDefaultAdapter) { IsSelected = current is null });
                foreach (var a in _state.Adapters.Where(a => a.Kind != DNSManagerPro.Core.Models.AdapterKind.Virtual))
                {
                    AdapterOptions.Add(new ChoiceOption(a.Name, a.Id, SetDefaultAdapter) { IsSelected = a.Id == current });
                }

                if (current is not null && AdapterOptions.All(o => o.Value != current))
                {
                    AdapterOptions.Add(new ChoiceOption("Saved adapter (not currently present)", current, SetDefaultAdapter) { IsSelected = true });
                }
            }
            finally
            {
                _syncing = false;
            }
        });
    }

    private void Sync(AppSettings s)
    {
        _syncing = true;
        try
        {
            StartWithWindows = s.StartWithWindows;
            MinimizeToTray = s.MinimizeToTray;
            LaunchMinimized = s.LaunchMinimized;
            ConfirmDnsChanges = s.ConfirmDnsChanges;
            MonitorEnabled = s.MonitoringEnabled;
            NotifyChange = s.NotifyOnChange;
            NotifyFailure = s.NotifyOnFailure;
            PreferEncryptedDns = s.PreferEncryptedDns;
            RepetitionsText = s.BenchmarkRepetitions.ToString(CultureInfo.InvariantCulture);
            TimeoutText = s.BenchmarkTimeoutMs.ToString(CultureInfo.InvariantCulture);
            ParallelText = s.BenchmarkParallelism.ToString(CultureInfo.InvariantCulture);
            DomainsText = string.Join(", ", s.BenchmarkDomains);
            foreach (var o in IntervalOptions)
            {
                o.IsSelected = o.Value == s.MonitoringIntervalMinutes.ToString(CultureInfo.InvariantCulture);
            }

            foreach (var o in Ipv6Options)
            {
                o.IsSelected = o.Value == s.Ipv6Handling.ToString();
            }

            foreach (var o in AdapterOptions)
            {
                o.IsSelected = o.Value == s.DefaultAdapterId;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void Dispatch(Action a)
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

    // ---- Benchmark defaults (explicit save, validated) -----------------------------------------------------------

    [RelayCommand]
    private void SaveBenchmark()
    {
        var errors = new List<string>();
        var ok = Int(RepetitionsText, "Repetitions", 1, 100, errors, out var reps);
        ok &= Int(TimeoutText, "Timeout", 200, 10_000, errors, out var timeout);
        ok &= Int(ParallelText, "Parallel queries", 1, 16, errors, out var par);
        var domains = DomainsText.Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.ToLowerInvariant()).Distinct().ToList();
        if (domains.Count is < 1 or > 20)
        {
            errors.Add("Provide between 1 and 20 test domains.");
        }

        foreach (var d in domains)
        {
            if (!d.Contains('.', StringComparison.Ordinal) || !DnsValidator.ValidateHostName(d, "Domain").IsValid)
            {
                errors.Add($"'{d}' is not a valid domain name.");
            }
        }

        if (!ok || errors.Count > 0)
        {
            BenchmarkMessageIsError = true;
            BenchmarkMessage = string.Join(" ", errors);
            return;
        }

        _settings.Update(s => s with { BenchmarkRepetitions = reps, BenchmarkTimeoutMs = timeout, BenchmarkParallelism = par, BenchmarkDomains = domains });
        BenchmarkMessageIsError = false;
        BenchmarkMessage = "Benchmark defaults saved.";
    }

    [RelayCommand]
    private void ResetBenchmark()
    {
        var d = new AppSettings();
        _settings.Update(s => s with
        {
            BenchmarkRepetitions = d.BenchmarkRepetitions,
            BenchmarkTimeoutMs = d.BenchmarkTimeoutMs,
            BenchmarkParallelism = d.BenchmarkParallelism,
            BenchmarkDomains = d.BenchmarkDomains,
        });
        BenchmarkMessageIsError = false;
        BenchmarkMessage = "Benchmark defaults restored.";
    }

    private static bool Int(string text, string name, int min, int max, List<string> errors, out int value)
    {
        if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
        {
            return true;
        }

        errors.Add($"{name} must be a whole number from {min} to {max}.");
        return false;
    }

    // ---- Data ---------------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task BackupDatabaseAsync()
    {
        var path = _dialogs.PickSaveFile("Back up database", "DNS Manager Pro backup (*.db)|*.db", $"dnsmanagerpro-backup-{DateTime.Now:yyyyMMdd-HHmm}.db");
        if (path is null)
        {
            return;
        }

        var r = await _maintenance.BackupAsync(path);
        ShowData(r.Success, r.Success ? "Backup saved to " + path : r.Message);
    }

    [RelayCommand]
    private void RestoreDatabase()
    {
        var path = _dialogs.PickOpenFile("Restore database", "DNS Manager Pro backup (*.db)|*.db");
        if (path is null)
        {
            return;
        }

        var check = DatabaseMaintenance.Validate(path);
        if (!check.Success)
        {
            ShowData(false, check.Message);
            return;
        }

        if (!_dialogs.Confirm(
                "Restore database",
                "This replaces all providers, profiles, history and benchmark results with the contents of the selected backup. " +
                "Your current data is saved first in the app's backups folder, and the app restarts to load the restored data.",
                "Restore and restart"))
        {
            return;
        }

        var r = _maintenance.Restore(path, Path.Combine(AppPaths.Root, "backups"));
        if (!r.Success)
        {
            ShowData(false, r.Message);
            return;
        }

        RestartApp();
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (!_dialogs.Confirm(
                "Clear history",
                "This permanently deletes DNS change history, benchmark results, diagnostic runs and monitoring events. " +
                "It also deletes the saved settings that 'Restore previous settings' relies on. Providers, profiles and settings are kept. This cannot be undone.",
                "Clear history"))
        {
            return;
        }

        var r = await _maintenance.ClearHistoryAsync();
        if (r.Success)
        {
            await _session.LoadAsync();
        }

        ShowData(r.Success, r.Message);
    }

    [RelayCommand]
    private void ExportSettings()
    {
        var path = _dialogs.PickSaveFile("Export settings", "Settings (*.json)|*.json", "dnsmanagerpro-settings.json");
        if (path is null)
        {
            return;
        }

        try
        {
            _settings.ExportTo(path);
            ShowData(true, "Settings exported to " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowData(false, "Could not export: " + ex.Message);
        }
    }

    [RelayCommand]
    private void ImportSettings()
    {
        var path = _dialogs.PickOpenFile("Import settings", "Settings (*.json)|*.json");
        if (path is null)
        {
            return;
        }

        try
        {
            var before = _settings.Current;
            _settings.ImportFrom(path);
            if (_settings.Current.StartWithWindows != before.StartWithWindows || _settings.Current.LaunchMinimized != before.LaunchMinimized)
            {
                ApplyStartup(_settings.Current.StartWithWindows, _settings.Current.LaunchMinimized);
            }

            ShowData(true, "Settings imported. Out-of-range values were corrected.");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowData(false, "That file is not a valid settings file: " + ex.Message);
        }
    }

    [RelayCommand]
    private void ResetSettings()
    {
        if (!_dialogs.Confirm("Reset settings", "Restore every setting to its default value? Your data (providers, profiles, history) is not affected.", "Reset"))
        {
            return;
        }

        var hadStartup = _settings.Current.StartWithWindows;
        _settings.ResetToDefaults();
        if (hadStartup)
        {
            ApplyStartup(false, false);
        }

        ShowData(true, "Settings reset to defaults.");
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(AppPaths.Root);

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(AppPaths.Logs);

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
            psi.ArgumentList.Add(folder);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowData(false, "Could not open the folder: " + ex.Message);
        }
    }

    private void ShowData(bool success, string message)
    {
        DataMessageIsError = !success;
        DataMessage = message;
    }

    private static void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose();
        }

        System.Windows.Application.Current.Shutdown();
    }
}
