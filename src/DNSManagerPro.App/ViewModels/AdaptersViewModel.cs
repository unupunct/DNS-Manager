using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DNSManagerPro.App.Services;
using DNSManagerPro.Core.Models;
using DNSManagerPro.Core.Providers;

namespace DNSManagerPro.App.ViewModels;

public sealed record AdapterRow(NetworkAdapterInfo Adapter, bool IsActive)
{
    public string Name => Adapter.Name;

    public string Description => Adapter.Description;

    public string Kind => Adapter.Kind.ToString();

    public string StateText => Adapter.IsUp ? "Connected" : "Disconnected";

    public string Tone => Adapter.IsUp ? "good" : "";

    public string Summary => Adapter.IPv4Addresses.Count > 0 ? Adapter.IPv4Addresses[0].ToString() : Adapter.StateHint();
}

internal static class AdapterExtensions
{
    public static string StateHint(this NetworkAdapterInfo a) => a.IsUp ? "No IPv4 address" : "Not connected";
}

public sealed partial class AdaptersViewModel : ObservableObject, IDisposable
{
    private readonly SystemStateService _state;

    public AdaptersViewModel(SystemStateService state)
    {
        _state = state;
        _state.PropertyChanged += OnStateChanged;
        Rebuild();
        _ = _state.RefreshAsync(false);
    }

    public ObservableCollection<AdapterRow> Adapters { get; } = [];

    public ObservableCollection<InfoItem> Details { get; } = [];

    [ObservableProperty]
    private AdapterRow? _selected;

    [ObservableProperty]
    private bool _showAll;

    [ObservableProperty]
    private string _summary = string.Empty;

    partial void OnSelectedChanged(AdapterRow? value) => BuildDetails(value);

    partial void OnShowAllChanged(bool value) => Rebuild();

    [RelayCommand]
    private Task RefreshAsync() => _state.RefreshAsync(false);

    public void Dispose() => _state.PropertyChanged -= OnStateChanged;

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemStateService.Adapters))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var keep = Selected?.Adapter.Id;
        var active = _state.ActiveAdapter?.Id;
        var rows = _state.Adapters
            .Where(a => ShowAll || (a.IsUp && a.Kind != AdapterKind.Virtual))
            .OrderByDescending(a => a.Id == active)
            .ThenByDescending(a => a.IsUp)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new AdapterRow(a, a.Id == active))
            .ToList();

        Adapters.Clear();
        foreach (var r in rows)
        {
            Adapters.Add(r);
        }

        Summary = $"{rows.Count} shown of {_state.Adapters.Count} detected";
        Selected = rows.FirstOrDefault(r => r.Adapter.Id == keep) ?? rows.FirstOrDefault();
    }

    private static string List(IEnumerable<IPAddress> a)
    {
        var s = string.Join(Environment.NewLine, a);
        return s.Length == 0 ? "-" : s;
    }

    private void BuildDetails(AdapterRow? row)
    {
        Details.Clear();
        if (row is null)
        {
            return;
        }

        var a = row.Adapter;
        var d = a.Dns;
        var provider = BuiltInProviders.All.FirstOrDefault(p => d.IPv4Servers.Concat(d.IPv6Servers).Any(p.Matches));
        Details.Add(new InfoItem("Name", a.Name));
        Details.Add(new InfoItem("Description", a.Description));
        Details.Add(new InfoItem("State", row.StateText, row.Tone));
        Details.Add(new InfoItem("Type", a.Kind.ToString()));
        Details.Add(new InfoItem("Link speed", a.SpeedBitsPerSecond > 0 ? (a.SpeedBitsPerSecond >= 1_000_000_000 ? $"{a.SpeedBitsPerSecond / 1_000_000_000.0:0.#} Gbps" : $"{a.SpeedBitsPerSecond / 1_000_000.0:0} Mbps") : "-"));
        Details.Add(new InfoItem("MAC address", a.MacAddress));
        Details.Add(new InfoItem("Interface index", a.InterfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Details.Add(new InfoItem("DHCP", a.IsDhcpEnabled ? "Enabled" : "Disabled"));
        Details.Add(new InfoItem("IPv4 address", List(a.IPv4Addresses)));
        Details.Add(new InfoItem("IPv6 address", List(a.IPv6Addresses)));
        Details.Add(new InfoItem("Gateway", List(a.Gateways)));
        Details.Add(new InfoItem("DNS mode (IPv4)", d.IPv4Automatic ? "Automatic (DHCP)" : "Manual", d.IPv4Automatic ? "" : "warn"));
        Details.Add(new InfoItem("DNS servers IPv4", List(d.IPv4Servers)));
        Details.Add(new InfoItem("DNS mode (IPv6)", d.IPv6Automatic ? "Automatic" : "Manual", d.IPv6Automatic ? "" : "warn"));
        Details.Add(new InfoItem("DNS servers IPv6", List(d.IPv6Servers)));
        Details.Add(new InfoItem("Known provider", provider?.Name ?? "None recognized", provider is null ? string.Empty : "good"));
    }
}
