using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DNSManagerPro.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DNSManagerPro.App.ViewModels;

public sealed record NavItem(NavSection Section, string Title, string Glyph);

public sealed class PlaceholderViewModel(string title, string description, string phase)
{
    public string Title { get; } = title;

    public string Description { get; } = description;

    public string Phase { get; } = phase;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly SystemStateService _state;
    private readonly Dictionary<NavSection, object> _pages = [];

    public MainViewModel(IServiceProvider services, SystemStateService state, INavigationService nav)
    {
        _services = services;
        _state = state;
        nav.NavigationRequested += s => SelectedItem = Items.First(i => i.Section == s);
        _state.PropertyChanged += OnStateChanged;
        SelectedItem = Items[0];
    }

    public ObservableCollection<NavItem> Items { get; } =
    [
        new(NavSection.Dashboard, "Dashboard", "\uE80F"),
        new(NavSection.Providers, "DNS Providers", "\uE774"),
        new(NavSection.Benchmark, "DNS Benchmark", "\uEC4A"),
        new(NavSection.CustomDns, "Custom DNS", "\uE70F"),
        new(NavSection.Adapters, "Network Adapters", "\uE839"),
        new(NavSection.Profiles, "DNS Profiles", "\uE8F1"),
        new(NavSection.Diagnostics, "DNS Diagnostics", "\uE9D9"),
        new(NavSection.History, "History", "\uE81C"),
        new(NavSection.Settings, "Settings", "\uE713"),
        new(NavSection.About, "About", "\uE946"),
    ];

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    [ObservableProperty]
    private NavItem? _selectedItem;

    [ObservableProperty]
    private object? _currentPage;

    public string ConnectivityText => _state.IsConnected ? "Connected" : "No connectivity";

    public bool IsConnected => _state.IsConnected;

    public string AdapterText => _state.ActiveAdapter?.Name ?? "No active adapter";

    public string DnsText
    {
        get
        {
            var d = _state.ActiveAdapter?.Dns;
            if (d is null)
            {
                return "DNS: -";
            }

            var p = d.Primary(false) ?? d.Primary(true);
            return $"DNS: {(p?.ToString() ?? "none")} ({(d.IPv4Automatic && d.IPv6Automatic ? "auto" : "manual")})";
        }
    }

    public string PrivilegeText => _state.IsElevated ? "Administrator" : "Standard user";

    public bool IsElevated => _state.IsElevated;

    public string BackgroundText => _state.BackgroundOperation;

    public bool IsBusy => _state.IsBusy;

    partial void OnSelectedItemChanged(NavItem? value)
    {
        if (value is null)
        {
            return;
        }

        if (!_pages.TryGetValue(value.Section, out var page))
        {
            page = Create(value.Section);
            _pages[value.Section] = page;
        }

        CurrentPage = page;
    }

    private object Create(NavSection s) => s switch
    {
        NavSection.Dashboard => _services.GetRequiredService<DashboardViewModel>(),
        NavSection.Providers => _services.GetRequiredService<ProvidersViewModel>(),
        NavSection.Benchmark => _services.GetRequiredService<BenchmarkViewModel>(),
        NavSection.CustomDns => _services.GetRequiredService<CustomDnsViewModel>(),
        NavSection.Adapters => _services.GetRequiredService<AdaptersViewModel>(),
        NavSection.Profiles => _services.GetRequiredService<ProfilesViewModel>(),
        NavSection.Diagnostics => _services.GetRequiredService<DiagnosticsViewModel>(),
        NavSection.History => _services.GetRequiredService<HistoryViewModel>(),
        NavSection.Settings => new PlaceholderViewModel("Settings", "General, benchmark, monitoring, network and data options.", "Phase 7"),
        NavSection.About => new PlaceholderViewModel("About", "Version, licenses and project information.", "Phase 8"),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ConnectivityText));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(AdapterText));
        OnPropertyChanged(nameof(DnsText));
        OnPropertyChanged(nameof(BackgroundText));
        OnPropertyChanged(nameof(IsBusy));
    }
}




