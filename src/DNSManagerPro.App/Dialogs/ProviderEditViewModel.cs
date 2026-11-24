using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DNSManagerPro.Core.Providers;

namespace DNSManagerPro.App.Dialogs;

public sealed partial class ProviderEditViewModel : ObservableObject
{
    private readonly DnsProvider? _existing;

    public ProviderEditViewModel(DnsProvider? existing)
    {
        _existing = existing;
        if (existing is not null)
        {
            _name = existing.Name;
            _description = existing.Description;
            _website = existing.Website;
            _ipv4Primary = existing.IPv4Primary ?? string.Empty;
            _ipv4Secondary = existing.IPv4Secondary ?? string.Empty;
            _ipv6Primary = existing.IPv6Primary ?? string.Empty;
            _ipv6Secondary = existing.IPv6Secondary ?? string.Empty;
            _privacy = existing.Privacy;
            _security = existing.Security;
            _filtering = existing.Filtering;
            _notes = existing.Notes;
            _dohUrl = existing.DohUrl ?? string.Empty;
            _dotHost = existing.DotHost ?? string.Empty;
            _doqHost = existing.DoqHost ?? string.Empty;
        }

        Revalidate();
    }

    public event Action<bool>? CloseRequested;

    public string WindowTitle => _existing is null ? "Add DNS provider" : $"Edit {_existing.Name}";

    public DnsProvider? Result { get; private set; }

    public ObservableCollection<string> Errors { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _website = string.Empty;

    [ObservableProperty]
    private string _ipv4Primary = string.Empty;

    [ObservableProperty]
    private string _ipv4Secondary = string.Empty;

    [ObservableProperty]
    private string _ipv6Primary = string.Empty;

    [ObservableProperty]
    private string _ipv6Secondary = string.Empty;

    [ObservableProperty]
    private string _privacy = string.Empty;

    [ObservableProperty]
    private string _security = string.Empty;

    [ObservableProperty]
    private string _filtering = string.Empty;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private string _dohUrl = string.Empty;

    [ObservableProperty]
    private string _dotHost = string.Empty;

    [ObservableProperty]
    private string _doqHost = string.Empty;

    public bool CanSave => Errors.Count == 0;

    private static string? N(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private DnsProvider Build() => new(
        _existing?.Id ?? $"custom-{Guid.NewGuid():N}"[..15],
        Name.Trim(), Description.Trim(), Website.Trim(),
        N(Ipv4Primary), N(Ipv4Secondary), N(Ipv6Primary), N(Ipv6Secondary),
        Privacy.Trim(), Security.Trim(), Filtering.Trim(), Notes.Trim(),
        _existing?.IsBuiltIn ?? false,
        N(DohUrl), N(DotHost), N(DoqHost));

    private void Revalidate()
    {
        Errors.Clear();
        foreach (var e in DnsValidator.ValidateProvider(Build()))
        {
            Errors.Add(e);
        }

        OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(CanSave)))
        {
            Revalidate();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        Result = Build();
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
