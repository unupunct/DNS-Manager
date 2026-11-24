using System.Net;
using DNSManagerPro.Core.Models;
using DNSManagerPro.Core.Providers;

namespace DNSManagerPro.Tests.Core;

public class ActiveAdapterSelectorTests
{
    private static NetworkAdapterInfo Adapter(string name, bool up, AdapterKind kind, bool gateway, long speed = 1_000_000_000) =>
        new(name, 1, name, name, "00:00:00:00:00:00", kind, up, true, speed,
            [IPAddress.Parse("192.168.1.10")], [],
            gateway ? [IPAddress.Parse("192.168.1.1")] : [],
            AdapterDnsConfiguration.Empty);

    [Fact]
    public void Prefers_up_adapter_with_gateway()
    {
        var chosen = ActiveAdapterSelector.Select(
        [
            Adapter("down", false, AdapterKind.Ethernet, true),
            Adapter("nogw", true, AdapterKind.Ethernet, false),
            Adapter("good", true, AdapterKind.WiFi, true, 300_000_000),
        ]);
        Assert.Equal("good", chosen?.Name);
    }

    [Fact]
    public void Ignores_virtual_adapters()
    {
        Assert.Null(ActiveAdapterSelector.Select([Adapter("vm", true, AdapterKind.Virtual, true)]));
    }

    [Fact]
    public void Returns_null_for_empty_list() => Assert.Null(ActiveAdapterSelector.Select([]));
}

public class DnsProviderTests
{
    [Theory]
    [InlineData("1.1.1.1", "Cloudflare")]
    [InlineData("8.8.4.4", "Google Public DNS")]
    [InlineData("149.112.112.112", "Quad9")]
    [InlineData("2606:4700:4700::1001", "Cloudflare")]
    public void Identifies_provider_by_address(string address, string expected)
    {
        var ip = IPAddress.Parse(address);
        Assert.Equal(expected, BuiltInProviders.All.First(p => p.Matches(ip)).Name);
    }

    [Fact]
    public void Unknown_address_matches_nothing() =>
        Assert.DoesNotContain(BuiltInProviders.All, p => p.Matches(IPAddress.Parse("192.168.1.1")));

    [Fact]
    public void All_builtin_addresses_are_valid_ips() =>
        Assert.All(BuiltInProviders.All.SelectMany(p => p.AllAddresses), a => Assert.True(IPAddress.TryParse(a, out _), a));

    [Fact]
    public void AdapterDns_primary_and_secondary_are_safe_when_empty()
    {
        Assert.Null(AdapterDnsConfiguration.Empty.Primary(false));
        Assert.Null(AdapterDnsConfiguration.Empty.Secondary(true));
    }
}
