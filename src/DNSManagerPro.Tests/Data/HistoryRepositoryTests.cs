using DNSManagerPro.Core.Configuration;
using DNSManagerPro.Data;

namespace DNSManagerPro.Tests.Data;

public sealed class HistoryRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dnsmp-h-{Guid.NewGuid():N}.db");
    private readonly SqliteHistoryRepository _repo;

    public HistoryRepositoryTests()
    {
        var db = new SqliteDatabase(_path);
        db.Migrate();
        _repo = new SqliteHistoryRepository(db);
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

    private static DnsSnapshot Snap(string s) => new("{g}", 3, "Ethernet", false, [s], true, [], DateTime.UtcNow);

    [Fact]
    public async Task Backup_is_stored_before_completion_and_found_only_after_success()
    {
        var id = await _repo.BeginAsync("{g}", "Ethernet", Snap("192.168.1.1"), "IPv4: 1.1.1.1", "Apply");
        Assert.Null(await _repo.GetLastSuccessfulChangeAsync("{g}"));
        var pending = Assert.Single(await _repo.GetRecentAsync(10));
        Assert.Equal("Pending", pending.Result);

        await _repo.CompleteAsync(id, "Success", null);
        var last = await _repo.GetLastSuccessfulChangeAsync("{g}");
        Assert.NotNull(last);
        Assert.Equal("192.168.1.1", DnsSnapshot.FromJson(last.PreviousConfiguration)!.IPv4Servers[0]);
    }

    [Fact]
    public async Task Latest_success_wins_and_rolled_back_is_ignored()
    {
        var a = await _repo.BeginAsync("{g}", "E", Snap("1.1.1.1"), "x", "Apply");
        await _repo.CompleteAsync(a, "Success", null);
        var b = await _repo.BeginAsync("{g}", "E", Snap("8.8.8.8"), "x", "Apply");
        await _repo.CompleteAsync(b, "RolledBack", "nope");
        var last = await _repo.GetLastSuccessfulChangeAsync("{g}");
        Assert.Equal("1.1.1.1", DnsSnapshot.FromJson(last!.PreviousConfiguration)!.IPv4Servers[0]);
    }
}
