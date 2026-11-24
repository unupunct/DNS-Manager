using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DNSManagerPro.App.Services;
using DNSManagerPro.Core.Configuration;
using DNSManagerPro.Core.Models;
using DNSManagerPro.Core.Providers;

namespace DNSManagerPro.App.Dialogs;

public enum FamilyChoice
{
    Unchanged,
    Source,
    Automatic,
}

/// <summary>What the dialog was opened for. Exactly one of Provider / Snapshot is set, or neither for "restore automatic".</summary>
public sealed record ApplyDnsRequest(string Title, string SourceName, DnsProvider? Provider, DnsSnapshot? Snapshot, string RestartArguments);

public sealed partial class ApplyDnsViewModel : ObservableObject
{
    private readonly ApplyDnsRequest _request;
    private readonly DnsChangeService _service;
    private readonly IElevationService _elevation;
    private readonly bool _elevated;

    public ApplyDnsViewModel(
        ApplyDnsRequest request,
        IReadOnlyList<NetworkAdapterInfo> adapters,
        string? preselectAdapterId,
        DnsChangeService service,
        IElevationService elevation,
        bool isElevated)
    {
        _request = request;
        _service = service;
        _elevation = elevation;
        _elevated = isElevated;

        var list = request.Snapshot is { } snap
            ? adapters.Where(a => a.Id == snap.AdapterId).ToList()
            : adapters.Where(a => a.Kind != AdapterKind.Virtual).OrderByDescending(a => a.IsUp).ThenByDescending(a => a.HasGateway).ToList();
        foreach (var a in list)
        {
            AdapterOptions.Add(a);
        }

        HasSourceV4 = request.Snapshot is not null || !string.IsNullOrWhiteSpace(request.Provider?.IPv4Primary);
        HasSourceV6 = request.Snapshot is not null || !string.IsNullOrWhiteSpace(request.Provider?.IPv6Primary);
        var hasSource = request.Provider is not null || request.Snapshot is not null;
        _choiceV4 = !hasSource ? FamilyChoice.Automatic : HasSourceV4 ? FamilyChoice.Source : FamilyChoice.Unchanged;
        _choiceV6 = !hasSource ? FamilyChoice.Automatic : HasSourceV6 ? FamilyChoice.Source : FamilyChoice.Unchanged;
        _selectedAdapter = AdapterOptions.FirstOrDefault(a => a.Id == preselectAdapterId)
            ?? AdapterOptions.FirstOrDefault(a => a.IsUp && a.HasGateway) ?? AdapterOptions.FirstOrDefault();
        // Don't touch IPv6 by default on an adapter that has no IPv6 connectivity; the user can still opt in.
        if (_choiceV6 == FamilyChoice.Source && request.Snapshot is null
            && _selectedAdapter is { } sel && sel.IPv6Addresses.Count == 0 && sel.Dns.IPv6Servers.Count == 0)
        {
            _choiceV6 = FamilyChoice.Unchanged;
        }

        Recompute();
    }

    public event Action<bool>? CloseRequested;

    public string Title => _request.Title;

    public string SourceLabel => _request.Snapshot is not null ? "Use previous settings" : $"Use {_request.SourceName}";

    public bool IsSnapshotMode => _request.Snapshot is not null;

    public bool HasSourceV4 { get; }

    public bool HasSourceV6 { get; }

    public bool NeedsElevation => !_elevated;

    public ObservableCollection<NetworkAdapterInfo> AdapterOptions { get; } = [];

    public ObservableCollection<PreviewRow> Preview { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public ObservableCollection<string> Steps { get; } = [];

    [ObservableProperty]
    private NetworkAdapterInfo? _selectedAdapter;

    [ObservableProperty]
    private FamilyChoice _choiceV4;

    [ObservableProperty]
    private FamilyChoice _choiceV6;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isDone;

    [ObservableProperty]
    private string? _resultMessage;

    [ObservableProperty]
    private bool _resultIsError;

    public bool CanEdit => !IsBusy && !IsDone;

    public string PrimaryText => NeedsElevation ? "Restart as administrator" : "Apply changes";

    partial void OnSelectedAdapterChanged(NetworkAdapterInfo? value) => Recompute();

    partial void OnChoiceV4Changed(FamilyChoice value) => Recompute();

    partial void OnChoiceV6Changed(FamilyChoice value) => Recompute();

    private FamilyChange Build(FamilyChoice c, bool v6)
    {
        if (c == FamilyChoice.Unchanged)
        {
            return FamilyChange.Unchanged;
        }

        if (c == FamilyChoice.Automatic)
        {
            return FamilyChange.Automatic;
        }

        IReadOnlyList<string?> raw;
        if (_request.Snapshot is { } snap)
        {
            if (v6 ? snap.IPv6Automatic : snap.IPv4Automatic)
            {
                return FamilyChange.Automatic;
            }

            raw = v6 ? snap.IPv6Servers : snap.IPv4Servers;
        }
        else
        {
            var p = _request.Provider;
            raw = v6 ? [p?.IPv6Primary, p?.IPv6Secondary] : [p?.IPv4Primary, p?.IPv4Secondary];
        }

        return FamilyChange.Manual(raw.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => IPAddress.Parse(x!.Trim())));
    }

    public DnsChangePlan? Plan { get; private set; }

    private void Recompute()
    {
        Preview.Clear();
        Warnings.Clear();
        Problems.Clear();
        Plan = null;
        if (SelectedAdapter is null)
        {
            Problems.Add("No network adapter selected.");
            PrimaryCommand.NotifyCanExecuteChanged();
            return;
        }

        try
        {
            Plan = new DnsChangePlan(SelectedAdapter, Build(ChoiceV4, false), Build(ChoiceV6, true), _request.SourceName);
        }
        catch (FormatException ex)
        {
            Problems.Add("Provider contains an invalid address: " + ex.Message);
            PrimaryCommand.NotifyCanExecuteChanged();
            return;
        }

        foreach (var r in DnsChangePlanner.Preview(Plan))
        {
            Preview.Add(r);
        }

        foreach (var w in DnsChangePlanner.Warnings(Plan))
        {
            Warnings.Add(w);
        }

        foreach (var e in DnsChangePlanner.Validate(Plan))
        {
            Problems.Add(e);
        }

        if (Problems.Count == 0 && DnsChangePlanner.IsNoOp(Plan))
        {
            Problems.Add("The adapter already has these settings.");
        }

        PrimaryCommand.NotifyCanExecuteChanged();
    }

    private bool CanPrimary() => !IsBusy && !IsDone && (NeedsElevation || (Plan is not null && Problems.Count == 0));

    [RelayCommand(CanExecute = nameof(CanPrimary))]
    private async Task PrimaryAsync()
    {
        if (NeedsElevation)
        {
            if (!_elevation.RestartAsAdministrator(_request.RestartArguments))
            {
                ResultIsError = true;
                ResultMessage = "Administrator permission was not granted. Nothing was changed.";
            }

            return;
        }

        IsBusy = true;
        PrimaryCommand.NotifyCanExecuteChanged();
        Steps.Clear();
        ResultMessage = null;
        try
        {
            var progress = new Progress<string>(s => Steps.Add(s));
            var outcome = _request.Snapshot is { } snap
                ? await _service.RestoreAsync(snap, progress)
                : await _service.ApplyAsync(Plan!, "Apply", progress);
            ResultIsError = !outcome.Success;
            ResultMessage = outcome.Message;
            IsDone = true;
        }
        finally
        {
            IsBusy = false;
            PrimaryCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(IsDone && !ResultIsError);
}
