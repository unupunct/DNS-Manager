using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DNSManagerPro.App.Services;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Configuration;
using DNSManagerPro.Core.Models;
using DNSManagerPro.Core.Providers;
using DNSManagerPro.Core.Settings;

namespace DNSManagerPro.App.Dialogs;

public enum FamilyChoice
{
    Unchanged,
    Source,
    Automatic,
}

/// <summary>What the dialog was opened for. Provider and Snapshot are both null for "restore automatic".</summary>
public sealed record ApplyDnsRequest(string Title, string SourceName, DnsProvider? Provider, DnsSnapshot? Snapshot, string RestartArguments);

/// <summary>User preferences that shape the dialog's defaults. The preview is shown regardless.</summary>
public sealed record ApplyDnsOptions(bool RequireConfirmation = false, bool PreferEncryptedDns = false, Ipv6Preference Ipv6 = Ipv6Preference.Auto);

/// <summary>One selectable adapter. Nothing is checked unless the user (or the preselected adapter) says so.</summary>
public sealed partial class AdapterChoice(NetworkAdapterInfo adapter, bool isChecked) : ObservableObject
{
    public NetworkAdapterInfo Adapter { get; } = adapter;

    [ObservableProperty]
    private bool _isChecked = isChecked;
}

public sealed partial class ApplyDnsViewModel : ObservableObject
{
    private readonly ApplyDnsRequest _request;
    private readonly DnsChangeService _service;
    private readonly IElevationService _elevation;
    private readonly IEncryptedDnsService? _encrypted;
    private readonly bool _elevated;
    private readonly ApplyDnsOptions _options;

    public ApplyDnsViewModel(
        ApplyDnsRequest request,
        IReadOnlyList<NetworkAdapterInfo> adapters,
        string? preselectAdapterId,
        DnsChangeService service,
        IElevationService elevation,
        bool isElevated,
        IEncryptedDnsService? encrypted = null,
        ApplyDnsOptions? options = null)
    {
        _options = options ?? new ApplyDnsOptions();
        _request = request;
        _service = service;
        _elevation = elevation;
        _elevated = isElevated;
        _encrypted = encrypted;

        var list = request.Snapshot is { } snap
            ? adapters.Where(a => a.Id == snap.AdapterId).ToList()
            : adapters.Where(a => a.Kind != AdapterKind.Virtual).OrderByDescending(a => a.IsUp).ThenByDescending(a => a.HasGateway).ToList();
        var pre = list.FirstOrDefault(a => a.Id == preselectAdapterId) ?? list.FirstOrDefault(a => a.IsUp && a.HasGateway) ?? list.FirstOrDefault();
        foreach (var a in list)
        {
            var c = new AdapterChoice(a, a == pre);
            c.PropertyChanged += OnChoiceChanged;
            Choices.Add(c);
        }

        HasSourceV4 = request.Snapshot is not null || !string.IsNullOrWhiteSpace(request.Provider?.IPv4Primary);
        HasSourceV6 = request.Snapshot is not null || !string.IsNullOrWhiteSpace(request.Provider?.IPv6Primary);
        var hasSource = request.Provider is not null || request.Snapshot is not null;
        _choiceV4 = !hasSource ? FamilyChoice.Automatic : HasSourceV4 ? FamilyChoice.Source : FamilyChoice.Unchanged;
        _choiceV6 = !hasSource ? FamilyChoice.Automatic : HasSourceV6 ? FamilyChoice.Source : FamilyChoice.Unchanged;

        // Don't touch IPv6 by default when no selected adapter has IPv6; the user can still opt in.
        if (_choiceV6 == FamilyChoice.Source && request.Snapshot is null)
        {
            var noIpv6 = Checked.All(a => a.IPv6Addresses.Count == 0 && a.Dns.IPv6Servers.Count == 0);
            if (_options.Ipv6 == Ipv6Preference.Never || (_options.Ipv6 == Ipv6Preference.Auto && noIpv6))
            {
                _choiceV6 = FamilyChoice.Unchanged;
            }
        }

        _enableDoh = _options.PreferEncryptedDns && ShowDohOption;

        Recompute();
    }

    public event Action<bool>? CloseRequested;

    public string Title => _request.Title;

    public string SourceLabel => _request.Snapshot is not null ? "Use previous settings" : $"Use {_request.SourceName}";

    public bool IsSnapshotMode => _request.Snapshot is not null;

    public bool AllowMultiple => !IsSnapshotMode && Choices.Count > 1;

    public bool HasSourceV4 { get; }

    public bool HasSourceV6 { get; }

    public bool NeedsElevation => !_elevated;

    public ObservableCollection<AdapterChoice> Choices { get; } = [];

    public ObservableCollection<PreviewRow> Preview { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public ObservableCollection<string> Steps { get; } = [];

    /// <summary>Plans for the currently checked adapters.</summary>
    public IReadOnlyList<DnsChangePlan> Plans { get; private set; } = [];

    private IEnumerable<NetworkAdapterInfo> Checked => Choices.Where(c => c.IsChecked).Select(c => c.Adapter);

    /// <summary>True only when every selected adapter was changed (or already matched) and verified.</summary>
    public bool Succeeded { get; private set; }

    public string? DohUrl => _request.Provider?.DohUrl;

    public bool ShowDohOption => !IsSnapshotMode && !string.IsNullOrWhiteSpace(DohUrl) && _encrypted?.IsSupported == true && _request.Provider?.HasAddresses == true;

    public string DohOptionText => $"Also enable DNS-over-HTTPS for these servers (Windows 11): {DohUrl}";

    [ObservableProperty]
    private FamilyChoice _choiceV4;

    [ObservableProperty]
    private FamilyChoice _choiceV6;

    [ObservableProperty]
    private bool _enableDoh;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryText))]
    [NotifyPropertyChangedFor(nameof(ShowConfirmHint))]
    private bool _isArmed;

    public string PrimaryText => NeedsElevation
        ? "Restart as administrator"
        : IsArmed ? "Yes, apply these changes" : _options.RequireConfirmation ? "Review and apply" : "Apply changes";

    public bool ShowConfirmHint => IsArmed;

    partial void OnChoiceV4Changed(FamilyChoice value) => Recompute();

    partial void OnChoiceV6Changed(FamilyChoice value) => Recompute();

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e) => Recompute();

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

    private void Recompute()
    {
        IsArmed = false; // any change to the selection invalidates a pending confirmation
        Preview.Clear();
        Warnings.Clear();
        Problems.Clear();
        Plans = [];
        var selected = Checked.ToList();
        if (selected.Count == 0)
        {
            Problems.Add("Select at least one network adapter.");
            PrimaryCommand.NotifyCanExecuteChanged();
            return;
        }

        var plans = new List<DnsChangePlan>();
        try
        {
            foreach (var a in selected)
            {
                plans.Add(new DnsChangePlan(a, Build(ChoiceV4, false), Build(ChoiceV6, true), _request.SourceName));
            }
        }
        catch (FormatException ex)
        {
            Problems.Add("Provider contains an invalid address: " + ex.Message);
            PrimaryCommand.NotifyCanExecuteChanged();
            return;
        }

        Plans = plans;
        var multi = plans.Count > 1;
        foreach (var plan in plans)
        {
            var prefix = multi ? plan.Adapter.Name + ": " : string.Empty;
            foreach (var r in DnsChangePlanner.Preview(plan))
            {
                Preview.Add(r with { Label = multi ? $"{plan.Adapter.Name} - {r.Label}" : r.Label });
            }

            foreach (var w in DnsChangePlanner.Warnings(plan))
            {
                Warnings.Add(prefix + w);
            }

            foreach (var e in DnsChangePlanner.Validate(plan))
            {
                Problems.Add(prefix + e);
            }
        }

        if (Problems.Count == 0 && plans.All(DnsChangePlanner.IsNoOp))
        {
            Problems.Add(multi ? "All selected adapters already have these settings." : "The adapter already has these settings.");
        }

        PrimaryCommand.NotifyCanExecuteChanged();
    }

    private bool CanPrimary() => !IsBusy && !IsDone && (NeedsElevation || (Plans.Count > 0 && Problems.Count == 0));

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

        if (_options.RequireConfirmation && !IsArmed)
        {
            IsArmed = true; // first click only arms; the user re-reads the preview and confirms explicitly
            return;
        }

        IsArmed = false;
        OnPropertyChanged(nameof(ShowConfirmHint));
        IsBusy = true;
        PrimaryCommand.NotifyCanExecuteChanged();
        Steps.Clear();
        ResultMessage = null;
        try
        {
            var plans = Plans.ToList();
            var multi = plans.Count > 1;
            var applied = new List<DnsChangePlan>();
            ApplyOutcome? failure = null;
            DnsChangePlan? failedPlan = null;
            foreach (var plan in plans)
            {
                if (multi)
                {
                    Steps.Add($"== {plan.Adapter.Name} ==");
                }

                var progress = new Progress<string>(s => Steps.Add(s));
                var outcome = _request.Snapshot is { } snap
                    ? await _service.RestoreAsync(snap, progress)
                    : await _service.ApplyAsync(plan, "Apply", progress);
                if (!outcome.Success)
                {
                    failure = outcome;
                    failedPlan = plan;
                    break;
                }

                applied.Add(plan);
            }

            if (failure is not null)
            {
                ResultIsError = true;
                ResultMessage = multi && applied.Count > 0
                    ? $"{failedPlan!.Adapter.Name}: {failure.Message} Already changed (kept): {string.Join(", ", applied.Select(p => p.Adapter.Name))}. Use 'Restore previous settings' on an adapter to undo."
                    : (multi ? $"{failedPlan!.Adapter.Name}: " : string.Empty) + failure.Message;
            }
            else
            {
                Succeeded = true;
                ResultIsError = false;
                ResultMessage = multi ? $"DNS settings applied and verified on {applied.Count} adapters." : "DNS settings applied and verified.";
                if (EnableDoh && ShowDohOption)
                {
                    var servers = applied.SelectMany(p => new[] { p.IPv4, p.IPv6 }).Where(c => c.Mode == DnsMode.Manual).SelectMany(c => c.Servers).Distinct().ToList();
                    Steps.Add("Registering DNS-over-HTTPS with Windows...");
                    var doh = await _encrypted!.EnableDohAsync(servers, DohUrl!, allowUdpFallback: true);
                    Steps.Add(doh.Success ? "DoH registered (plain UDP fallback allowed)." : "DoH not registered: " + doh.Message);
                    ResultMessage += doh.Success
                        ? " DoH is registered for these servers; Windows upgrades to it when supported."
                        : " DNS works over plain DNS, but DoH could not be enabled: " + doh.Message;
                }
            }

            IsDone = true;
        }
        finally
        {
            IsBusy = false;
            PrimaryCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(Succeeded);
}
