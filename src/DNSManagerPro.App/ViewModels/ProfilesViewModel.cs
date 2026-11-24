using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DNSManagerPro.App.Services;
using DNSManagerPro.Core.Profiles;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.App.ViewModels;

public sealed partial class ProfileCardViewModel(DnsProfile profile, ProfilesViewModel owner) : ObservableObject
{
    public DnsProfile Profile { get; } = profile;

    public string Name => Profile.Name;

    public string Category => Profile.Category;

    public string Summary => Profile.Summary;

    public string Description => Profile.Description;

    public IReadOnlyList<string> Tags => Profile.TagList;

    public string AdapterText => Profile.AdapterName is { Length: > 0 } n ? $"Adapter: {n}" : "Adapter: chosen when applying";

    public string CreatedText => $"Created {Profile.CreatedUtc.ToLocalTime():d}";

    public string AppliedText => Profile.LastAppliedUtc is { } t ? $"Last applied {t.ToLocalTime():g}" : "Never applied";

    public string EncryptedText => string.Join(" / ", new[] { Profile.DohUrl is null ? null : "DoH", Profile.DotHost is null ? null : "DoT", Profile.DoqHost is null ? null : "DoQ" }.Where(s => s is not null));

    public bool HasEncrypted => EncryptedText.Length > 0;

    [RelayCommand]
    private Task ApplyAsync() => owner.ApplyAsync(this);

    [RelayCommand]
    private void Edit() => owner.Edit(this);

    [RelayCommand]
    private Task DuplicateAsync() => owner.DuplicateAsync(this);

    [RelayCommand]
    private void Export() => owner.Export(this);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteAsync(this);
}

public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly ProfileService _profiles;
    private readonly IApplyFlow _flow;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _nav;
    private readonly ILogger<ProfilesViewModel> _log;
    private IReadOnlyList<DnsProfile> _all = [];

    public ProfilesViewModel(ProfileService profiles, IApplyFlow flow, IDialogService dialogs, INavigationService nav, ILogger<ProfilesViewModel> log)
    {
        _profiles = profiles;
        _flow = flow;
        _dialogs = dialogs;
        _nav = nav;
        _log = log;
        Categories.Add(new ChoiceOption("All", "All", o => CategoryFilter = o.Value ?? "All") { IsSelected = true });
        foreach (var c in ProfileCategories.All)
        {
            Categories.Add(new ChoiceOption(c, c, o => CategoryFilter = o.Value ?? "All"));
        }

        _profiles.Changed += () => _ = LoadAsync();
        _ = LoadAsync();
    }

    public ObservableCollection<ProfileCardViewModel> Cards { get; } = [];

    public ObservableCollection<ChoiceOption> Categories { get; } = [];

    public IReadOnlyList<DnsProfile> Templates { get; } = ProfileTemplates.All;

    [ObservableProperty]
    private string _categoryFilter = "All";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    [ObservableProperty]
    private bool _isEmpty;

    partial void OnCategoryFilterChanged(string value) => Refilter();

    partial void OnSearchTextChanged(string value) => Refilter();

    public async Task LoadAsync()
    {
        try
        {
            _all = await _profiles.GetAllAsync();
            Refilter();
        }
        catch (Exception ex)
        {
            Fail("Could not load profiles: " + ex.Message, ex);
        }
    }

    private void Refilter()
    {
        var q = SearchText.Trim();
        var rows = _all.Where(p => (CategoryFilter == "All" || p.Category == CategoryFilter)
            && (q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Tags.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Description.Contains(q, StringComparison.OrdinalIgnoreCase)));
        Cards.Clear();
        foreach (var p in rows)
        {
            Cards.Add(new ProfileCardViewModel(p, this));
        }

        IsEmpty = Cards.Count == 0;
    }

    private void Fail(string text, Exception ex)
    {
        _log.LogError(ex, "{Message}", text);
        MessageIsError = true;
        Message = text;
    }

    private void Info(string text)
    {
        MessageIsError = false;
        Message = text;
    }

    [RelayCommand]
    private void New()
    {
        _profiles.StartNew = true;
        _nav.NavigateTo(NavSection.CustomDns);
    }

    [RelayCommand]
    private async Task AddTemplateAsync(DnsProfile template)
    {
        try
        {
            var names = _all.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var copy = DnsProfile.New(ProfileService.UniqueName(template.Name, names), template.Category) with
            {
                Description = template.Description,
                IsAutomatic = template.IsAutomatic,
                ProviderId = template.ProviderId,
                IPv4Primary = template.IPv4Primary,
                IPv4Secondary = template.IPv4Secondary,
                IPv6Primary = template.IPv6Primary,
                IPv6Secondary = template.IPv6Secondary,
            };
            await _profiles.SaveAsync(copy);
            Info($"Added '{copy.Name}'. Edit it to adjust servers.");
        }
        catch (Exception ex)
        {
            Fail("Could not add template: " + ex.Message, ex);
        }
    }

    internal async Task ApplyAsync(ProfileCardViewModel card)
    {
        var ok = await _flow.ApplyProfileAsync(card.Profile);
        if (ok)
        {
            Info($"Applied '{card.Name}'.");
        }
    }

    internal void Edit(ProfileCardViewModel card)
    {
        _profiles.PendingEditId = card.Profile.Id;
        _nav.NavigateTo(NavSection.CustomDns);
    }

    internal async Task DuplicateAsync(ProfileCardViewModel card)
    {
        try
        {
            var copy = await _profiles.DuplicateAsync(card.Profile);
            Info($"Created '{copy.Name}'.");
        }
        catch (Exception ex)
        {
            Fail("Could not duplicate: " + ex.Message, ex);
        }
    }

    internal async Task DeleteAsync(ProfileCardViewModel card)
    {
        if (!_dialogs.Confirm("Delete profile", $"Delete profile '{card.Name}'? This cannot be undone. DNS settings already applied to adapters are not changed.", "Delete"))
        {
            return;
        }

        try
        {
            await _profiles.DeleteAsync(card.Profile.Id);
            Info($"Deleted '{card.Name}'.");
        }
        catch (Exception ex)
        {
            Fail("Could not delete: " + ex.Message, ex);
        }
    }

    internal void Export(ProfileCardViewModel card) => ExportCore([card.Profile], SafeFileName(card.Name));

    [RelayCommand]
    private void ExportAll() => ExportCore(_all, "dns-profiles");

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private void ExportCore(IReadOnlyList<DnsProfile> list, string name)
    {
        if (list.Count == 0)
        {
            Info("There are no profiles to export.");
            return;
        }

        var path = _dialogs.PickSaveFile("Export profiles", "DNS profiles (*.json)|*.json", name + ".json");
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, ProfileExchange.Export(list));
            Info($"Exported {list.Count} profile(s) to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail("Export failed: " + ex.Message, ex);
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = _dialogs.PickOpenFile("Import profiles", "DNS profiles (*.json)|*.json");
        if (path is null)
        {
            return;
        }

        try
        {
            if (new FileInfo(path).Length > ProfileExchange.MaxBytes)
            {
                Fail("That file is larger than 1 MB and was not read.", new InvalidDataException("file too large"));
                return;
            }

            var report = await _profiles.ImportAsync(await File.ReadAllTextAsync(path));
            MessageIsError = report.Errors.Count > 0 && report.Added == 0;
            Message = string.Create(CultureInfo.InvariantCulture, $"Imported {report.Added} profile(s).")
                + (report.Errors.Count > 0 ? " " + string.Join(" ", report.Errors) : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail("Import failed: " + ex.Message, ex);
        }
    }
}

