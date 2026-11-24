using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DNSManagerPro.App.Services;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Models;
using DNSManagerPro.Core.Profiles;
using DNSManagerPro.Core.Providers;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.App.ViewModels;

public sealed partial class ProfileListItem(DnsProfile profile) : ObservableObject
{
    public DnsProfile Profile { get; } = profile;

    public string Name => Profile.Name;

    public string Summary => Profile.Summary;

    public string Category => Profile.Category;
}

/// <summary>Full-page editor for DNS configurations (saved as profiles). Distinguishes plain DNS server addresses from encrypted-DNS endpoints.</summary>
public sealed partial class CustomDnsViewModel : ObservableObject
{
    private readonly ProfileService _profiles;
    private readonly IProviderRepository _providers;
    private readonly SystemStateService _state;
    private readonly IApplyFlow _flow;
    private readonly IDialogService _dialogs;
    private readonly ILogger<CustomDnsViewModel> _log;
    private DnsProfile _editing = DnsProfile.New(string.Empty);
    private bool _isLoadingEditor;
    private bool _isNew = true;

    public CustomDnsViewModel(ProfileService profiles, IProviderRepository providers, SystemStateService state, IApplyFlow flow, IDialogService dialogs, ILogger<CustomDnsViewModel> log)
    {
        _profiles = profiles;
        _providers = providers;
        _state = state;
        _flow = flow;
        _dialogs = dialogs;
        _log = log;
        foreach (var c in ProfileCategories.All)
        {
            CategoryOptions.Add(new ChoiceOption(c, c, o => Category = o.Value ?? "Custom"));
        }

        _profiles.Changed += () => _ = RefreshListAsync();
        LoadIntoEditor(DnsProfile.New(string.Empty), true);
        _ = RefreshListAsync();
    }

    public ObservableCollection<ProfileListItem> List { get; } = [];

    public ObservableCollection<ChoiceOption> CategoryOptions { get; } = [];

    public ObservableCollection<ChoiceOption> AdapterOptions { get; } = [];

    public ObservableCollection<DnsProvider> StarterProviders { get; } = [];

    public ObservableCollection<string> Errors { get; } = [];

    [ObservableProperty]
    private ProfileListItem? _selectedItem;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _category = "Custom";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddressesEnabled))]
    private bool _isAutomatic;

    [ObservableProperty]
    private string _ipv4Primary = string.Empty;

    [ObservableProperty]
    private string _ipv4Secondary = string.Empty;

    [ObservableProperty]
    private string _ipv6Primary = string.Empty;

    [ObservableProperty]
    private string _ipv6Secondary = string.Empty;

    [ObservableProperty]
    private string _dohUrl = string.Empty;

    [ObservableProperty]
    private string _dotHost = string.Empty;

    [ObservableProperty]
    private string _doqHost = string.Empty;

    [ObservableProperty]
    private string _tags = string.Empty;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    public bool AddressesEnabled => !IsAutomatic;

    public bool IsEditingExisting => !_isNew;

    public string EditorTitle => _isNew ? "New configuration" : $"Edit: {_editing.Name}";

    public bool CanSave => Errors.Count == 0;

    public string LastAppliedText => _editing.LastAppliedUtc is { } t ? $"Last applied {t.ToLocalTime():g}" : _isNew ? string.Empty : "Never applied";

    /// <summary>Called by the view when it is shown: picks up an edit/new request and refreshes lists.</summary>
    public async Task OnShownAsync()
    {
        await RefreshListAsync();
        try
        {
            StarterProviders.Clear();
            foreach (var p in (await _providers.GetAllAsync()).Where(p => p.HasAddresses))
            {
                StarterProviders.Add(p);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not load providers for starters");
        }

        if (_profiles.PendingEditId is { } id)
        {
            _profiles.PendingEditId = null;
            if (List.FirstOrDefault(i => i.Profile.Id == id) is { } item)
            {
                SelectedItem = item;
            }
        }
        else if (_profiles.StartNew)
        {
            _profiles.StartNew = false;
            NewCommand.Execute(null);
        }
    }

    private async Task RefreshListAsync()
    {
        try
        {
            var keep = _editing.Id;
            var all = await _profiles.GetAllAsync();
            List.Clear();
            foreach (var p in all)
            {
                List.Add(new ProfileListItem(p));
            }

            RebuildAdapterOptions();
            _isLoadingEditor = true;
            SelectedItem = List.FirstOrDefault(i => i.Profile.Id == keep && !_isNew);
            _isLoadingEditor = false;
        }
        catch (Exception ex)
        {
            Fail("Could not load profiles: " + ex.Message, ex);
        }
    }

    private void RebuildAdapterOptions()
    {
        var selected = AdapterOptions.FirstOrDefault(o => o.IsSelected)?.Value ?? _editing.AdapterId;
        AdapterOptions.Clear();
        AdapterOptions.Add(new ChoiceOption("Any adapter (choose when applying)", null, _ => Revalidate()));
        foreach (var a in _state.Adapters.Where(a => a.Kind != AdapterKind.Virtual))
        {
            AdapterOptions.Add(new ChoiceOption(a.Name, a.Id, _ => Revalidate()));
        }

        var match = AdapterOptions.FirstOrDefault(o => o.Value == selected) ?? AdapterOptions[0];
        match.IsSelected = true;
    }

    partial void OnSelectedItemChanged(ProfileListItem? value)
    {
        if (_isLoadingEditor || value is null)
        {
            return;
        }

        LoadIntoEditor(value.Profile, false);
    }

    private void LoadIntoEditor(DnsProfile p, bool isNew)
    {
        _isLoadingEditor = true;
        _editing = p;
        _isNew = isNew;
        Name = p.Name;
        Description = p.Description;
        Category = p.Category;
        IsAutomatic = p.IsAutomatic;
        Ipv4Primary = p.IPv4Primary ?? string.Empty;
        Ipv4Secondary = p.IPv4Secondary ?? string.Empty;
        Ipv6Primary = p.IPv6Primary ?? string.Empty;
        Ipv6Secondary = p.IPv6Secondary ?? string.Empty;
        DohUrl = p.DohUrl ?? string.Empty;
        DotHost = p.DotHost ?? string.Empty;
        DoqHost = p.DoqHost ?? string.Empty;
        Tags = p.Tags;
        Notes = p.Notes;
        foreach (var o in CategoryOptions)
        {
            o.IsSelected = o.Value == p.Category;
        }

        RebuildAdapterOptions();
        _isLoadingEditor = false;
        Message = null;
        Revalidate();
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(IsEditingExisting));
        OnPropertyChanged(nameof(LastAppliedText));
    }

    private static string? N(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private DnsProfile Build()
    {
        var adapter = AdapterOptions.FirstOrDefault(o => o.IsSelected);
        return _editing with
        {
            Name = Name.Trim(),
            Description = Description.Trim(),
            Category = Category,
            IsAutomatic = IsAutomatic,
            IPv4Primary = N(Ipv4Primary),
            IPv4Secondary = N(Ipv4Secondary),
            IPv6Primary = N(Ipv6Primary),
            IPv6Secondary = N(Ipv6Secondary),
            DohUrl = N(DohUrl),
            DotHost = N(DotHost),
            DoqHost = N(DoqHost),
            Tags = Tags.Trim(),
            Notes = Notes.Trim(),
            AdapterId = adapter?.Value,
            AdapterName = adapter?.Value is null ? null : adapter.Label,
        };
    }

    private void Revalidate()
    {
        if (_isLoadingEditor)
        {
            return;
        }

        Errors.Clear();
        foreach (var e in Build().Validate())
        {
            Errors.Add(e);
        }

        OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsNewCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        SaveAndApplyCommand.NotifyCanExecuteChanged();
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Name) or nameof(Description) or nameof(Category) or nameof(IsAutomatic) or nameof(Ipv4Primary) or nameof(Ipv4Secondary)
            or nameof(Ipv6Primary) or nameof(Ipv6Secondary) or nameof(DohUrl) or nameof(DotHost) or nameof(DoqHost) or nameof(Tags) or nameof(Notes))
        {
            Revalidate();
        }
    }

    private void Fail(string text, Exception ex)
    {
        _log.LogError(ex, "{Message}", text);
        MessageIsError = true;
        Message = text;
    }

    [RelayCommand]
    private void New()
    {
        _isLoadingEditor = true;
        SelectedItem = null;
        _isLoadingEditor = false;
        LoadIntoEditor(DnsProfile.New(string.Empty), true);
    }

    [RelayCommand]
    private void StartFrom(DnsProvider provider)
    {
        Ipv4Primary = provider.IPv4Primary ?? string.Empty;
        Ipv4Secondary = provider.IPv4Secondary ?? string.Empty;
        Ipv6Primary = provider.IPv6Primary ?? string.Empty;
        Ipv6Secondary = provider.IPv6Secondary ?? string.Empty;
        DohUrl = provider.DohUrl ?? string.Empty;
        DotHost = provider.DotHost ?? string.Empty;
        DoqHost = provider.DoqHost ?? string.Empty;
        IsAutomatic = false;
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = provider.Name + " (custom)";
        }

        _editing = _editing with { ProviderId = provider.Id };
        Message = null;
    }

    private bool CanSaveCmd() => CanSave;

    private async Task<DnsProfile?> SaveCoreAsync(bool asNew)
    {
        try
        {
            var p = Build();
            if (asNew)
            {
                var existing = List.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                p = p with
                {
                    Id = DnsProfile.New("x").Id,
                    Name = ProfileService.UniqueName(p.Name, existing),
                    CreatedUtc = DateTime.UtcNow,
                    LastAppliedUtc = null,
                };
            }

            await _profiles.SaveAsync(p);
            _isNew = false;
            _editing = p;
            await RefreshListAsync();
            _isLoadingEditor = true;
            SelectedItem = List.FirstOrDefault(i => i.Profile.Id == p.Id);
            _isLoadingEditor = false;
            LoadIntoEditor(List.FirstOrDefault(i => i.Profile.Id == p.Id)?.Profile ?? p, false);
            MessageIsError = false;
            Message = $"Saved '{p.Name}'.";
            return p;
        }
        catch (Exception ex)
        {
            Fail("Could not save: " + ex.Message, ex);
            return null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveCmd))]
    private async Task SaveAsync() => await SaveCoreAsync(false);

    [RelayCommand(CanExecute = nameof(CanSaveCmd))]
    private async Task SaveAsNewAsync() => await SaveCoreAsync(true);

    [RelayCommand(CanExecute = nameof(CanSaveCmd))]
    private async Task SaveAndApplyAsync()
    {
        if (await SaveCoreAsync(false) is { } saved)
        {
            await _flow.ApplyProfileAsync(saved);
        }
    }

    /// <summary>Applies the form as it is, without saving it.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveCmd))]
    private async Task ApplyAsync()
    {
        var p = Build();
        if (p.IsAutomatic)
        {
            await _flow.RestoreAutomaticAsync(p.AdapterId);
        }
        else
        {
            await _flow.ApplyProviderAsync(p.ToProvider(), p.AdapterId);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_isNew || !List.Any(i => i.Profile.Id == _editing.Id))
        {
            return;
        }

        if (!_dialogs.Confirm("Delete configuration", $"Delete '{_editing.Name}'? This cannot be undone. DNS settings already applied to adapters are not changed.", "Delete"))
        {
            return;
        }

        try
        {
            await _profiles.DeleteAsync(_editing.Id);
            New();
            MessageIsError = false;
            Message = "Deleted.";
        }
        catch (Exception ex)
        {
            Fail("Could not delete: " + ex.Message, ex);
        }
    }
}

