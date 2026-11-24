using DNSManagerPro.Core.Providers;

namespace DNSManagerPro.Tests.Core;

public class DnsValidatorTests
{
    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("192.168.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData(" 8.8.8.8 ")]
    public void Valid_ipv4(string s) => Assert.True(DnsValidator.ValidateIPv4(s).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.1.1")]
    [InlineData("256.1.1.1")]
    [InlineData("1.1.1.1.1")]
    [InlineData("a.b.c.d")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("1.1.1.-1")]
    public void Invalid_ipv4(string s) => Assert.False(DnsValidator.ValidateIPv4(s).IsValid);

    [Theory]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::1")]
    public void Valid_ipv6(string s) => Assert.True(DnsValidator.ValidateIPv6(s).IsValid);

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("::")]
    [InlineData("ff02::1")]
    [InlineData("fe80::1")]
    [InlineData("fe80::1%12")]
    [InlineData("zzzz::1")]
    [InlineData("")]
    public void Invalid_ipv6(string s) => Assert.False(DnsValidator.ValidateIPv6(s).IsValid);

    [Theory]
    [InlineData("https://dns.google/dns-query", true)]
    [InlineData("http://dns.google/dns-query", false)]
    [InlineData("dns.google", false)]
    [InlineData("", true)]
    public void Doh_url(string s, bool ok) => Assert.Equal(ok, DnsValidator.ValidateHttpsUrl(s).IsValid);

    [Theory]
    [InlineData("dns.google", true)]
    [InlineData("one.one.one.one", true)]
    [InlineData("bad host", false)]
    [InlineData("-bad.com", false)]
    [InlineData("a;rm -rf", false)]
    public void Host_name(string s, bool ok) => Assert.Equal(ok, DnsValidator.ValidateHostName(s, "Host").IsValid);

    private static DnsProvider P(string? v4p = "1.1.1.1", string? v4s = "1.0.0.1", string? v6p = null, string? v6s = null, string name = "X") =>
        new("x", name, "", "", v4p, v4s, v6p, v6s, "", "", "", IsBuiltIn: false);

    [Fact]
    public void Provider_valid() => Assert.Empty(DnsValidator.ValidateProvider(P()));

    [Fact]
    public void Provider_needs_name() => Assert.NotEmpty(DnsValidator.ValidateProvider(P(name: " ")));

    [Fact]
    public void Provider_secondary_without_primary_is_invalid() =>
        Assert.NotEmpty(DnsValidator.ValidateProvider(P(v4p: null, v4s: "1.0.0.1")));

    [Fact]
    public void Provider_identical_servers_invalid() =>
        Assert.NotEmpty(DnsValidator.ValidateProvider(P(v4p: "1.1.1.1", v4s: "1.1.1.1")));

    [Fact]
    public void Provider_ipv6_in_ipv4_field_invalid() =>
        Assert.NotEmpty(DnsValidator.ValidateProvider(P(v4p: "2606:4700:4700::1111", v4s: null)));

    [Fact]
    public void All_builtins_are_valid() =>
        Assert.All(BuiltInProviders.All, p => Assert.Empty(DnsValidator.ValidateProvider(p)));

    [Fact]
    public void Protocols_reflect_endpoints()
    {
        var cf = BuiltInProviders.All.First(p => p.Id == "cloudflare");
        Assert.Equal(["UDP/TCP", "DoH", "DoT"], cf.Protocols);
        Assert.Equal(["Needs endpoint"], BuiltInProviders.All.First(p => p.Id == "nextdns").Protocols);
    }
}
