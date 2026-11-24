using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DNSManagerPro.App.Services;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Providers;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.App.ViewModels;

public sealed partial class ProviderCardViewModel(DnsProvider provider, ProvidersViewModel owner) : ObservableObject
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0x38, 0xBD, 0xF8), Color.FromRgb(0x22, 0xC5, 0x5E),
        Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0x2D, 0xD4, 0xBF),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initial), nameof(IconBrush), nameof(IPv4Line), nameof(IPv6Line), nameof(IsCustom), nameof(KindText), nameof(Protocols), nameof(HasAddresses))]
    private DnsProvider _provider = provider;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string? _testResult;

    public string Initial => Provider.Name.Length > 0 ? Provider.Name[..1].ToUpperInvariant() : "?";

    public Brush IconBrush
    {
        get
        {
            var h = Provider.Id.Aggregate(17, (a, c) => unchecked((a * 31) + c));
            return new SolidColorBrush(Palette[Math.Abs(h % Palette.Length)]);
        }
    }

    public bool IsCustom => !Provider.IsBuiltIn;

    public bool HasAddresses => Provider.HasAddresses;

    public string KindText => Provider.IsBuiltIn ? "Built-in" : "Custom";

    public IReadOnlyList<string> Protocols => Provider.Protocols;

    public string IPv4Line => Join(Provider.IPv4Primary, Provider.IPv4Secondary);

    public string IPv6Line => Join(Provider.IPv6Primary, Provider.IPv6Secondary);

    private static string Join(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) ? "-" : string.IsNullOrWhiteSpace(b) ? a : $"{a}  /  {b}";

    [RelayCommand]
    private void ToggleInfo() => IsExpanded = !IsExpanded;

    [RelayCommand(CanExecute = nameof(HasAddresses))]
    private Task TestAsync() => owner.TestAsync(this);

    [RelayCommand(CanExecute = nameof(HasAddresses))]
    private Task ApplyAsync() => owner.ApplyAsync(this);

    [RelayCommand]
    private Task EditAsync() => owner.EditAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteAsync(this);
}

public sealed partial class ProvidersViewModel : ObservableObject
{
    private static readonly string[] QuickTestDomains = ["google.com", "microsoft.com", "github.com"];

    private readonly IProviderRepository _repo;
    private readonly IDialogService _dialogs;
    private readonly IDnsProbe _probe;
    private readonly SystemStateService _state;
    private readonly ILogger<ProvidersViewModel> _log;
    private readonly IApplyFlow _flow;

    public ProvidersViewModel(IProviderRepository repo, IDialogService dialogs, IDnsProbe probe, SystemStateService state, IApplyFlow flow, ILogger<ProvidersViewModel> log)
    {
        _repo = repo;
        _dialogs = dialogs;
        _probe = probe;
        _state = state;
        _log = log;
        _flow = flow;
        _ = LoadAsync();
    }

    public ObservableCollection<ProviderCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    private async Task LoadAsync()
    {
        try
        {
            var all = await _repo.GetAllAsync();
            Cards.Clear();
            foreach (var p in all)
            {
                Cards.Add(new ProviderCardViewModel(p, this));
            }
        }
        catch (Exception ex)
        {
            Fail("Could not load providers: " + ex.Message, ex);
        }
    }

    private void Fail(string text, Exception ex)
    {
        _log.LogError(ex, "{Message}", text);
        MessageIsError = true;
        Message = text;
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var created = _dialogs.EditProvider(null);
        if (created is null)
        {
            return;
        }

        try
        {
            await _repo.UpsertAsync(created);
            Cards.Add(new ProviderCardViewModel(created, this));
            MessageIsError = false;
            Message = $"Added {created.Name}.";
        }
        catch (Exception ex)
        {
            Fail("Could not save provider: " + ex.Message, ex);
        }
    }

    [RelayCommand]
    private async Task RestoreDefaultsAsync()
    {
        if (!_dialogs.Confirm("Restore built-in providers", "Built-in providers will be reset to their original values and any missing ones re-added. Your custom providers are not changed.", "Restore"))
        {
            return;
        }

        try
        {
            await _repo.RestoreBuiltInsAsync();
            await LoadAsync();
            MessageIsError = false;
            Message = "Built-in providers restored.";
        }
        catch (Exception ex)
        {
            Fail("Restore failed: " + ex.Message, ex);
        }
    }

    internal Task ApplyAsync(ProviderCardViewModel card) => _flow.ApplyProviderAsync(card.Provider);

    internal async Task EditAsync(ProviderCardViewModel card)
    {
        var edited = _dialogs.EditProvider(card.Provider);
        if (edited is null)
        {
            return;
        }

        try
        {
            await _repo.UpsertAsync(edited);
            card.Provider = edited;
            card.TestResult = null;
            card.TestCommand.NotifyCanExecuteChanged();
            card.ApplyCommand.NotifyCanExecuteChanged();
            MessageIsError = false;
            Message = $"Saved {edited.Name}.";
        }
        catch (Exception ex)
        {
            Fail("Could not save provider: " + ex.Message, ex);
        }
    }

    internal async Task DeleteAsync(ProviderCardViewModel card)
    {
        if (!_dialogs.Confirm("Delete provider", $"Delete custom provider '{card.Provider.Name}'? This cannot be undone.", "Delete"))
        {
            return;
        }

        try
        {
            await _repo.DeleteAsync(card.Provider.Id);
            Cards.Remove(card);
            MessageIsError = false;
            Message = $"Deleted {card.Provider.Name}.";
        }
        catch (Exception ex)
        {
            Fail("Could not delete provider: " + ex.Message, ex);
        }
    }

    internal async Task TestAsync(ProviderCardViewModel card)
    {
        var p = card.Provider;
        var target = new[] { p.IPv4Primary, p.IPv6Primary }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => IPAddress.TryParse(s, out var ip) ? ip : null)
            .FirstOrDefault(ip => ip is not null);
        if (target is null)
        {
            card.TestResult = "No server address to test.";
            return;
        }

        card.IsTesting = true;
        card.TestResult = "Testing...";
        _state.BackgroundOperation = $"Testing {p.Name}...";
        try
        {
            var times = new List<double>();
            var failures = 0;
            foreach (var d in QuickTestDomains)
            {
                var r = await _probe.ProbeAsync(target, d, TimeSpan.FromSeconds(2));
                if (r.Success)
                {
                    times.Add(r.ResponseTimeMs);
                }
                else
                {
                    failures++;
                }
            }

            card.TestResult = times.Count == 0
                ? $"Unreachable: all {QuickTestDomains.Length} queries to {target} failed."
                : string.Create(CultureInfo.InvariantCulture, $"{target}: avg {times.Average():F0} ms, {times.Count}/{QuickTestDomains.Length} answered (quick check only - use the benchmark to compare).");
        }
        finally
        {
            card.IsTesting = false;
            _state.BackgroundOperation = "Idle";
        }
    }
}

