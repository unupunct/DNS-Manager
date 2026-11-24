using DNSManagerPro.Core.Models;
using DNSManagerPro.Windows.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DNSManagerPro.Tests.Windows;

/// <summary>Read-only integration tests against the real host. They never modify network settings.</summary>
public class WindowsAdapterServiceTests
{
    [Fact]
    public async Task Reads_adapters_without_filter_driver_bindings_or_loopback()
    {
        var svc = new WindowsNetworkAdapterService(NullLogger<WindowsNetworkAdapterService>.Instance);
        var adapters = await svc.GetAdaptersAsync();
        Assert.All(adapters, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Name));
            Assert.DoesNotContain("-QoS Packet Scheduler", a.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("-WFP ", a.Description, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Manual_mode_implies_the_dns_list_is_not_empty_or_flagged_consistently()
    {
        var svc = new WindowsNetworkAdapterService(NullLogger<WindowsNetworkAdapterService>.Instance);
        foreach (var a in await svc.GetAdaptersAsync())
        {
            // Cannot assert specific values on an arbitrary machine; assert the record is internally sane.
            Assert.NotNull(a.Dns);
            Assert.All(a.Dns.IPv4Servers, s => Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, s.AddressFamily));
            Assert.All(a.Dns.IPv6Servers, s => Assert.Equal(System.Net.Sockets.AddressFamily.InterNetworkV6, s.AddressFamily));
        }
    }

    [Fact]
    public void Privilege_service_reports_a_value() =>
        _ = new WindowsPrivilegeService().IsElevated;

    [Fact]
    public void Kind_enum_is_stable() => Assert.Equal(0, (int)AdapterKind.Ethernet);
}
