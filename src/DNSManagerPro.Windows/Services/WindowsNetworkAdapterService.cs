using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DNSManagerPro.Windows.Services;

/// <summary>
/// Reads adapters through System.Net.NetworkInformation. Automatic-vs-manual DNS is determined from the
/// per-interface registry value that Windows itself uses (HKLM\...\Tcpip[6]\Parameters\Interfaces\{guid}\NameServer),
/// which is readable without administrator rights.
/// </summary>
public sealed class WindowsNetworkAdapterService(ILogger<WindowsNetworkAdapterService> logger) : INetworkAdapterService
{
    private const string Tcpip4 = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string Tcpip6 = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces";

    public Task<IReadOnlyList<NetworkAdapterInfo>> GetAdaptersAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<NetworkAdapterInfo>>(Read, cancellationToken);

    private List<NetworkAdapterInfo> Read()
    {
        var result = new List<NetworkAdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel || IsFilterDriverBinding(nic.Description))
            {
                continue;
            }

            try
            {
                result.Add(Convert(nic));
            }
            catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or PlatformNotSupportedException)
            {
                logger.LogWarning(ex, "Skipping adapter {Name}: properties unreadable", nic.Name);
            }
        }

        return result;
    }

    private static NetworkAdapterInfo Convert(NetworkInterface nic)
    {
        var props = nic.GetIPProperties();
        var v4 = new List<IPAddress>();
        var v6 = new List<IPAddress>();
        foreach (var u in props.UnicastAddresses)
        {
            if (u.Address.AddressFamily == AddressFamily.InterNetwork)
            {
                v4.Add(u.Address);
            }
            else if (u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal)
            {
                v6.Add(u.Address);
            }
        }

        var dns = props.DnsAddresses;
        var dns4 = dns.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
        // Windows reports link-local fec0:0:0:ffff:: site-local placeholders when no IPv6 DNS is set; hide them.
        var dns6 = dns.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6SiteLocal).ToList();

        var dhcp = false;
        try
        {
            dhcp = props.GetIPv4Properties().IsDhcpEnabled;
        }
        catch (NetworkInformationException)
        {
            // IPv4 not bound to this adapter.
        }

        var guid = nic.Id;
        var manual4 = HasStaticNameServer(Tcpip4, guid);
        var manual6 = HasStaticNameServer(Tcpip6, guid);

        long speed;
        try
        {
            speed = nic.Speed;
        }
        catch (PlatformNotSupportedException)
        {
            speed = 0;
        }

        var index = 0;
        try
        {
            index = props.GetIPv4Properties().Index;
        }
        catch (NetworkInformationException)
        {
            try
            {
                index = props.GetIPv6Properties().Index;
            }
            catch (NetworkInformationException)
            {
                // No IP binding; index stays 0.
            }
        }

        return new NetworkAdapterInfo(
            guid,
            index,
            nic.Name,
            nic.Description,
            FormatMac(nic.GetPhysicalAddress()),
            Classify(nic),
            nic.OperationalStatus == OperationalStatus.Up,
            dhcp,
            speed,
            v4,
            v6,
            props.GatewayAddresses.Select(g => g.Address).Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)).ToList(),
            new AdapterDnsConfiguration(dns4, dns6, !manual4, !manual6));
    }

    private static bool HasStaticNameServer(string basePath, string guid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{basePath}\{guid}");
            return key?.GetValue("NameServer") is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>NDIS lightweight-filter bindings (QoS, WFP, Npcap...) appear as extra "adapters" but are not configurable network interfaces.</summary>
    private static bool IsFilterDriverBinding(string description) =>
        FilterMarkers.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] FilterMarkers =
    [
        "-QoS Packet Scheduler", "-WFP ", "-Native WiFi Filter", "-Virtual WiFi Filter", "-Npcap Packet Driver", "-VirtualBox NDIS", "-Hyper-V Virtual Switch Extension Filter",
    ];

    private static AdapterKind Classify(NetworkInterface nic)
    {
        var desc = nic.Description;
        if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
        {
            return AdapterKind.Virtual;
        }

        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => AdapterKind.Ethernet,
            NetworkInterfaceType.Wireless80211 => AdapterKind.WiFi,
            _ => AdapterKind.Other,
        };
    }

    private static string FormatMac(PhysicalAddress mac)
    {
        var bytes = mac.GetAddressBytes();
        return bytes.Length == 0 ? "-" : string.Join(':', bytes.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }
}

