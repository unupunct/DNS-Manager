using DNSManagerPro.Core.Providers;
using DNSManagerPro.Data;

namespace DNSManagerPro.Tests.Data;

public sealed class ProviderRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dnsmp-test-{Guid.NewGuid():N}.db");
    private readonly SqliteProviderRepository _repo;

    public ProviderRepositoryTests()
    {
        var db = new SqliteDatabase(_path);
        db.Migrate();
        _repo = new SqliteProviderRepository(db);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // temp file; ignore
        }
    }

    private static DnsProvider Custom(string id = "custom1") =>
        new(id, "My DNS", "mine", "https://example.com", "10.0.0.1", "10.0.0.2", null, null, "p", "s", "f", IsBuiltIn: false);

    [Fact]
    public async Task Seed_inserts_all_builtins_once()
    {
        await _repo.SeedAsync();
        await _repo.SeedAsync();
        Assert.Equal(BuiltInProviders.All.Count, (await _repo.GetAllAsync()).Count);
    }

    [Fact]
    public async Task Seed_preserves_user_edits_but_restore_resets_them()
    {
        await _repo.SeedAsync();
        var cf = (await _repo.GetAllAsync()).First(p => p.Id == "cloudflare");
        await _repo.UpsertAsync(cf with { Notes = "edited" });
        await _repo.SeedAsync();
        Assert.Equal("edited", (await _repo.GetAllAsync()).First(p => p.Id == "cloudflare").Notes);
        await _repo.RestoreBuiltInsAsync();
        Assert.Equal(string.Empty, (await _repo.GetAllAsync()).First(p => p.Id == "cloudflare").Notes);
    }

    [Fact]
    public async Task Custom_provider_roundtrip_edit_delete()
    {
        await _repo.UpsertAsync(Custom());
        var got = Assert.Single(await _repo.GetAllAsync());
        Assert.Equal("10.0.0.1", got.IPv4Primary);
        Assert.False(got.IsBuiltIn);

        await _repo.UpsertAsync(Custom() with { Name = "Renamed", IPv4Secondary = null });
        got = Assert.Single(await _repo.GetAllAsync());
        Assert.Equal("Renamed", got.Name);
        Assert.Null(got.IPv4Secondary);

        await _repo.DeleteAsync("custom1");
        Assert.Empty(await _repo.GetAllAsync());
    }

    [Fact]
    public async Task Invalid_provider_is_rejected() =>
        await Assert.ThrowsAsync<ArgumentException>(() => _repo.UpsertAsync(Custom() with { IPv4Primary = "999.1.1.1" }));

    [Fact]
    public void Migration_is_idempotent()
    {
        var db = new SqliteDatabase(_path);
        db.Migrate();
        db.Migrate();
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        Assert.Equal(SqliteDatabase.LatestVersion, Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }
}
