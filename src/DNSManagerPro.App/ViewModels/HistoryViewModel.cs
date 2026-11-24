using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DNSManagerPro.App.Services;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.History;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.App.ViewModels;

public sealed partial class TimelineRowViewModel(TimelineEntry entry) : ObservableObject
{
    public TimelineEntry Entry { get; } = entry;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpanded;

    public string Title => Entry.Title;

    public string Detail => Entry.Detail;

    public bool HasDetail => Entry.Detail.Length > 0;

    public string Time => Entry.TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string Subtitle => KindText + (string.IsNullOrEmpty(Entry.Adapter) ? string.Empty : "  -  " + Entry.Adapter);

    public string Glyph => Entry.Kind switch
    {
        TimelineKind.ConfigChange => "\uE713",
        TimelineKind.Benchmark => "\uEC4A",
        TimelineKind.Diagnostic => "\uE9D9",
        _ => "\uE7B3",
    };

    public string KindText => Entry.Kind switch
    {
        TimelineKind.ConfigChange => "Configuration",
        TimelineKind.Benchmark => "Benchmark",
        TimelineKind.Diagnostic => "Diagnostics",
        _ => "Monitoring",
    };

    public string Tone => Entry.Status switch { TimelineStatus.Success => "good", TimelineStatus.Warning => "warn", TimelineStatus.Failed => "bad", _ => string.Empty };

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly ITimelineRepository _repo;
    private readonly IDialogService _dialogs;
    private readonly ILogger<HistoryViewModel> _log;
    private IReadOnlyList<TimelineEntry> _all = [];

    public HistoryViewModel(ITimelineRepository repo, IDialogService dialogs, ILogger<HistoryViewModel> log)
    {
        _repo = repo;
        _dialogs = dialogs;
        _log = log;
        void Kind(string label, TimelineKind? k, bool sel = false) => KindOptions.Add(new ChoiceOption(label, k?.ToString(), o => KindFilter = o.Value is null ? null : Enum.Parse<TimelineKind>(o.Value)) { IsSelected = sel });
        Kind("All", null, true);
        Kind("Configuration", TimelineKind.ConfigChange);
        Kind("Benchmarks", TimelineKind.Benchmark);
        Kind("Diagnostics", TimelineKind.Diagnostic);
        Kind("Monitoring", TimelineKind.Monitoring);
        foreach (var (label, w) in new[] { ("24 hours", TimeWindow.Day), ("7 days", TimeWindow.Week), ("30 days", TimeWindow.Month), ("All time", TimeWindow.All) })
        {
            WindowOptions.Add(new ChoiceOption(label, w.ToString(), o => WindowFilter = Enum.Parse<TimeWindow>(o.Value ?? "All")) { IsSelected = w == TimeWindow.All });
        }
    }

    public ObservableCollection<ChoiceOption> KindOptions { get; } = [];

    public ObservableCollection<ChoiceOption> WindowOptions { get; } = [];

    public ObservableCollection<TimelineRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private TimelineKind? _kindFilter;

    [ObservableProperty]
    private TimeWindow _windowFilter = TimeWindow.All;

    [ObservableProperty]
    private bool _failuresOnly;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    [ObservableProperty]
    private int _selectedCount;

    partial void OnKindFilterChanged(TimelineKind? value) => Refilter();

    partial void OnWindowFilterChanged(TimeWindow value) => Refilter();

    partial void OnFailuresOnlyChanged(bool value) => Refilter();

    partial void OnSearchTextChanged(string value) => Refilter();

    public async Task LoadAsync()
    {
        try
        {
            _all = await _repo.GetAsync(500);
            Refilter();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not load history");
            MessageIsError = true;
            Message = "Could not load history: " + ex.Message;
        }
    }

    private TimelineFilter Filter => new(KindFilter, FailuresOnly, WindowFilter, SearchText);

    private IReadOnlyList<TimelineEntry> Filtered => [.. Filter.Apply(_all, DateTime.UtcNow)];

    private void Refilter()
    {
        foreach (var r in Rows)
        {
            r.PropertyChanged -= OnRowChanged;
        }

        Rows.Clear();
        var list = Filtered;
        foreach (var e in list)
        {
            var row = new TimelineRowViewModel(e);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }

        Summary = $"{list.Count} of {_all.Count} entries" + (list.Count(e => e.IsFailure) is > 0 and var f ? $" - {f} failure(s)" : string.Empty);
        SelectedCount = 0;
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineRowViewModel.IsSelected))
        {
            SelectedCount = Rows.Count(r => r.IsSelected);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private void SelectAllShown()
    {
        foreach (var r in Rows)
        {
            r.IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var r in Rows)
        {
            r.IsSelected = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var chosen = Rows.Where(r => r.IsSelected).Select(r => r.Entry).ToList();
        if (chosen.Count == 0)
        {
            Message = "Tick the entries you want to delete first.";
            MessageIsError = false;
            return;
        }

        var configs = chosen.Count(e => e.Kind == TimelineKind.ConfigChange);
        var warn = configs > 0 ? $"\n\n{configs} configuration change(s) are included. Deleting them also removes their saved backups, so 'Restore previous settings' may no longer find them." : string.Empty;
        if (!_dialogs.Confirm("Delete history entries", $"Permanently delete {chosen.Count} selected entr{(chosen.Count == 1 ? "y" : "ies")}? This cannot be undone.{warn}", "Delete"))
        {
            return;
        }

        try
        {
            var n = await _repo.DeleteAsync(chosen.Select(e => e.Key));
            MessageIsError = false;
            Message = $"Deleted {n} entr{(n == 1 ? "y" : "ies")}.";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not delete history");
            MessageIsError = true;
            Message = "Could not delete: " + ex.Message;
        }
    }

    private static string Stamp => DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

    [RelayCommand]
    private void ExportCsv() => Export("CSV file (*.csv)|*.csv", $"dns-history-{Stamp}.csv", TimelineExporter.Csv);

    [RelayCommand]
    private void ExportJson() => Export("JSON file (*.json)|*.json", $"dns-history-{Stamp}.json", TimelineExporter.JsonText);

    private void Export(string filter, string name, Func<IEnumerable<TimelineEntry>, string> format)
    {
        var path = _dialogs.PickSaveFile("Export history", filter, name);
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, format(Filtered), new System.Text.UTF8Encoding(true));
            MessageIsError = false;
            Message = $"Exported {Filtered.Count} entries to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageIsError = true;
            Message = "Export failed: " + ex.Message;
        }
    }
}


