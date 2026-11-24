using System.Net;
using DNSManagerPro.Core.Profiles;
using DNSManagerPro.Data;
using DNSManagerPro.Windows.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DNSManagerPro.Tests.Profiles;

public class ProfileValidationTests
{
    private static DnsProfile P() => DnsProfile.New("Work") with { IPv4Primary = "1.1.1.1", IPv4Secondary = "1.0.0.1" };

    [Fact]
    public void Valid_profile_has_no_errors() => Assert.Empty(P().Validate());

    [Fact]
    public void Name_category_and_addresses_are_required()
    {
        Assert.NotEmpty((P() with { Name = " " }).Validate());
        Assert.NotEmpty((P() with { Category = "Nope" }).Validate());
        Assert.NotEmpty((P() with { IPv4Primary = null, IPv4Secondary = null }).Validate());
        Assert.NotEmpty((P() with { IPv4Primary = "999.1.1.1" }).Validate());
        Assert.NotEmpty((P() with { IPv6Primary = "1.1.1.1" }).Validate());
        Assert.NotEmpty((P() with { DohUrl = "http://insecure.example/dns" }).Validate());
        Assert.NotEmpty((P() with { Description = new string('x', 501) }).Validate());
    }

    [Fact]
    public void Automatic_profile_needs_no_addresses_and_ignores_them()
    {
        var auto = DnsProfile.New("Default", "Default") with { IsAutomatic = true };
        Assert.Empty(auto.Validate());
        Assert.False(auto.ToProvider().HasAddresses);
        Assert.Equal("Automatic (DHCP)", auto.Summary);
    }

    [Fact]
    public void Every_template_is_valid() => Assert.All(ProfileTemplates.All, t => Assert.Empty(t.Validate()));

    [Fact]
    public void Tags_are_split_and_trimmed() =>
        Assert.Equal(["a", "b", "c"], (P() with { Tags = " a, b ;c " }).TagList);
}

public class ProfileExchangeTests
{
    private static DnsProfile P(string name) =>
        DnsProfile.New(name, "Gaming") with { IPv4Primary = "9.9.9.9", AdapterId = "{machine}", AdapterName = "Ethernet", ProviderId = "quad9", DohUrl = "https://dns.quad9.net/dns-query" };

    [Fact]
    public void Roundtrip_keeps_settings_but_drops_machine_specific_data()
    {
        var json = ProfileExchange.Export([P("A"), P("B")]);
        Assert.DoesNotContain("{machine}", json, StringComparison.Ordinal);
        var r = ProfileExchange.Import(json);
        Assert.Empty(r.Errors);
        Assert.Equal(2, r.Profiles.Count);
        Assert.All(r.Profiles, p =>
        {
            Assert.Equal("9.9.9.9", p.IPv4Primary);
            Assert.Equal("Gaming", p.Category);
            Assert.Null(p.AdapterId);
            Assert.Null(p.ProviderId);
            Assert.Null(p.LastAppliedUtc);
        });
        Assert.NotEqual(r.Profiles[0].Id, r.Profiles[1].Id);   // fresh ids
    }

    [Fact]
    public void Invalid_entries_are_skipped_with_reasons_valid_ones_kept()
    {
        var json = """
            {"format":"dnsmanagerpro-profiles","version":1,"profiles":[
              {"name":"Good","ipv4Primary":"1.1.1.1"},
              {"name":"BadIp","ipv4Primary":"300.1.1.1"},
              {"name":"","ipv4Primary":"1.1.1.1"},
              {"name":"BadUrl","ipv4Primary":"1.1.1.1","dohUrl":"javascript:alert(1)"}
            ]}
            """;
        var r = ProfileExchange.Import(json);
        Assert.Equal(["Good"], r.Profiles.Select(p => p.Name));
        Assert.Equal(3, r.Errors.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"format\":\"other\",\"version\":1,\"profiles\":[]}")]
    [InlineData("{\"format\":\"dnsmanagerpro-profiles\",\"version\":99,\"profiles\":[]}")]
    [InlineData("[1,2,3]")]
    public void Malformed_or_foreign_files_are_rejected_without_throwing(string json)
    {
        var r = ProfileExchange.Import(json);
        Assert.Empty(r.Profiles);
        Assert.NotEmpty(r.Errors);
    }

    [Fact]
    public void Oversized_input_is_rejected()
    {
        var r = ProfileExchange.Import(new string(' ', ProfileExchange.MaxBytes + 1));
        Assert.Empty(r.Profiles);
    }

    [Fact]
    public void Too_many_profiles_are_rejected()
    {
        var many = string.Join(",", Enumerable.Range(0, ProfileExchange.MaxProfiles + 1).Select(i => $"{{\"name\":\"p{i}\",\"ipv4Primary\":\"1.1.1.1\"}}"));
        var r = ProfileExchange.Import($"{{\"format\":\"dnsmanagerpro-profiles\",\"version\":1,\"profiles\":[{many}]}}");
        Assert.Empty(r.Profiles);
        Assert.Contains("Too many", r.Errors[0], StringComparison.Ordinal);
    }
}

public sealed class ProfileRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dnsmp-p-{Guid.NewGuid():N}.db");
    private readonly SqliteProfileRepository _repo;
    private readonly SqliteProviderRepository _providers;

    public ProfileRepositoryTests()
    {
        var db = new SqliteDatabase(_path);
        db.Migrate();
        _repo = new SqliteProfileRepository(db);
        _providers = new SqliteProviderRepository(db);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // temp
        }
    }

    private static DnsProfile P(string name = "Work") => DnsProfile.New(name) with { IPv4Primary = "1.1.1.1", IPv6Primary = "2606:4700:4700::1111", Tags = "office", AdapterId = "{a}", AdapterName = "Ethernet" };

    [Fact]
    public async Task Create_edit_delete_roundtrip()
    {
        var p = P();
        await _repo.UpsertAsync(p);
        var got = Assert.Single(await _repo.GetAllAsync());
        Assert.Equal("1.1.1.1", got.IPv4Primary);
        Assert.Equal("{a}", got.AdapterId);
        Assert.Equal("office", got.Tags);
        Assert.Null(got.LastAppliedUtc);

        await _repo.UpsertAsync(p with { Name = "Renamed", IPv4Secondary = "1.0.0.1" });
        got = Assert.Single(await _repo.GetAllAsync());
        Assert.Equal("Renamed", got.Name);
        Assert.Equal("1.0.0.1", got.IPv4Secondary);
        Assert.Equal(p.CreatedUtc, got.CreatedUtc, TimeSpan.FromSeconds(1));   // creation date is not overwritten

        await _repo.DeleteAsync(p.Id);
        Assert.Empty(await _repo.GetAllAsync());
    }

    [Fact]
    public async Task MarkApplied_sets_last_applied_and_survives_edits()
    {
        var p = P();
        await _repo.UpsertAsync(p);
        var when = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        await _repo.MarkAppliedAsync(p.Id, when);
        await _repo.UpsertAsync(p with { Description = "edited" });
        Assert.Equal(when, (await _repo.GetAllAsync())[0].LastAppliedUtc);
    }

    [Fact]
    public async Task Invalid_profile_is_rejected() =>
        await Assert.ThrowsAsync<ArgumentException>(() => _repo.UpsertAsync(P() with { IPv4Primary = "nope" }));

    [Fact]
    public async Task Deleting_a_provider_keeps_profiles_and_clears_the_link()
    {
        await _providers.SeedAsync();
        await _repo.UpsertAsync(P() with { ProviderId = "cloudflare" });
        await _providers.DeleteAsync("cloudflare");
        var got = Assert.Single(await _repo.GetAllAsync());
        Assert.Null(got.ProviderId);
    }

    [Fact]
    public async Task Automatic_profile_stores_no_addresses()
    {
        await _repo.UpsertAsync(DnsProfile.New("Default", "Default") with { IsAutomatic = true, IPv4Primary = "1.1.1.1" });
        var got = Assert.Single(await _repo.GetAllAsync());
        Assert.True(got.IsAutomatic);
        Assert.Null(got.IPv4Primary);
    }
}

public class EncryptedDnsTests
{
    [Fact]
    public void Builds_documented_netsh_commands_without_a_shell()
    {
        var cmds = NetshEncryptedDnsService.BuildCommands([IPAddress.Parse("1.1.1.1"), IPAddress.Parse("2606:4700:4700::1111")], "https://cloudflare-dns.com/dns-query", true);
        Assert.Equal(2, cmds.Count);
        Assert.Equal(["dns", "add", "encryption", "server=1.1.1.1", "dohtemplate=https://cloudflare-dns.com/dns-query", "autoupgrade=yes", "udpfallback=yes"], cmds[0]);
        Assert.Equal("server=2606:4700:4700::1111", cmds[1][3]);
        Assert.Equal("udpfallback=no", NetshEncryptedDnsService.BuildCommands([IPAddress.Loopback], "https://x.example/dns", false)[0][^1]);
    }

    private sealed class Runner : IProcessRunner
    {
        public int Calls { get; private set; }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ProcessResult(0, string.Empty));
        }
    }

    [Fact]
    public async Task Rejects_non_https_templates_before_running_anything()
    {
        var runner = new Runner();
        var svc = new NetshEncryptedDnsService(runner, NullLogger<NetshEncryptedDnsService>.Instance);
        var r = await svc.EnableDohAsync([IPAddress.Parse("1.1.1.1")], "http://insecure.example/dns", true);
        Assert.False(r.Success);
        Assert.Equal(0, runner.Calls);
    }
}
