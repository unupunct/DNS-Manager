using System.Net;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Configuration;
using DNSManagerPro.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DNSManagerPro.Tests.Configuration;

internal static class Fx
{
    public static IPAddress Ip(string s) => IPAddress.Parse(s);

    public static NetworkAdapterInfo Adapter(bool auto4 = true, string[]? v4 = null, bool auto6 = true, string[]? v6 = null, bool up = true) =>
        new("{guid}", 7, "Ethernet", "Test NIC", "00:11:22:33:44:55", AdapterKind.Ethernet, up, true, 1_000_000_000,
            [Ip("192.168.1.9")], [], [Ip("192.168.1.1")],
            new AdapterDnsConfiguration((v4 ?? ["192.168.1.1"]).Select(Ip).ToList(), (v6 ?? []).Select(Ip).ToList(), auto4, auto6));

    public static DnsChangePlan Plan(NetworkAdapterInfo a, FamilyChange v4, FamilyChange? v6 = null) => new(a, v4, v6 ?? FamilyChange.Unchanged, "test");
}

/// <summary>In-memory "operating system": the configurator mutates it, the adapter service reads it.</summary>
internal sealed class FakeSystem : INetworkAdapterService, IDnsConfigurator
{
    public NetworkAdapterInfo Adapter { get; set; } = Fx.Adapter();

    public List<DnsChangePlan> Applied { get; } = [];

    public int FailApplyOnCall { get; set; } = -1;

    public bool IgnoreChanges { get; set; }

    public Task<IReadOnlyList<NetworkAdapterInfo>> GetAdaptersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NetworkAdapterInfo>>([Adapter]);

    public Task<OperationResult> ApplyAsync(DnsChangePlan plan, CancellationToken cancellationToken = default)
    {
        Applied.Add(plan);
        if (Applied.Count - 1 == FailApplyOnCall)
        {
            return Task.FromResult(OperationResult.Fail("simulated failure"));
        }

        if (!IgnoreChanges)
        {
            var d = Adapter.Dns;
            Adapter = Adapter with
            {
                Dns = new AdapterDnsConfiguration(
                    plan.IPv4.Mode == DnsMode.Unchanged ? d.IPv4Servers : plan.IPv4.Mode == DnsMode.Automatic ? [Fx.Ip("192.168.1.1")] : plan.IPv4.Servers,
                    plan.IPv6.Mode == DnsMode.Unchanged ? d.IPv6Servers : plan.IPv6.Mode == DnsMode.Automatic ? [] : plan.IPv6.Servers,
                    plan.IPv4.Mode == DnsMode.Unchanged ? d.IPv4Automatic : plan.IPv4.Mode == DnsMode.Automatic,
                    plan.IPv6.Mode == DnsMode.Unchanged ? d.IPv6Automatic : plan.IPv6.Mode == DnsMode.Automatic),
            };
        }

        return Task.FromResult(OperationResult.Ok());
    }
}

internal sealed class FakeHistory : IDnsHistoryRepository
{
    public List<(long Id, DnsSnapshot Prev, string Op, string Result)> Rows { get; } = [];

    public Task<long> BeginAsync(string adapterId, string adapterName, DnsSnapshot previous, string newDescription, string operation, CancellationToken cancellationToken = default)
    {
        Rows.Add((Rows.Count + 1, previous, operation, "Pending"));
        return Task.FromResult((long)Rows.Count);
    }

    public Task CompleteAsync(long id, string result, string? message, CancellationToken cancellationToken = default)
    {
        var i = (int)id - 1;
        Rows[i] = Rows[i] with { Result = result };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DnsHistoryEntry>> GetRecentAsync(int count, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DnsHistoryEntry>>([]);

    public Task<DnsHistoryEntry?> GetLastSuccessfulChangeAsync(string adapterId, CancellationToken cancellationToken = default) =>
        Task.FromResult<DnsHistoryEntry?>(null);
}

internal sealed class FakeProbe(bool succeed = true) : IDnsProbe
{
    public Task<DnsProbeResult> ProbeAsync(IPAddress server, string domain, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DnsProbeResult(server, domain, succeed, 5, succeed ? "NoError" : "Timeout", succeed ? null : "timeout"));
}

internal sealed class FakePrivilege(bool elevated) : IPrivilegeService
{
    public bool IsElevated { get; } = elevated;
}

internal sealed class FakeCache : IDnsCacheService
{
    public int Flushes { get; private set; }

    public Task<OperationResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        Flushes++;
        return Task.FromResult(OperationResult.Ok());
    }
}

public class DnsChangeServiceTests
{
    private static (DnsChangeService Svc, FakeSystem Sys, FakeHistory Hist, FakeCache Cache) Build(bool elevated = true, bool probeOk = true)
    {
        var sys = new FakeSystem();
        var hist = new FakeHistory();
        var cache = new FakeCache();
        var svc = new DnsChangeService(sys, sys, hist, new FakePrivilege(elevated), new FakeProbe(probeOk), cache, NullLogger<DnsChangeService>.Instance)
        {
            VerifyDelay = TimeSpan.Zero,
        };
        return (svc, sys, hist, cache);
    }

    private static DnsChangePlan Cloudflare(NetworkAdapterInfo a) =>
        Fx.Plan(a, FamilyChange.Manual([Fx.Ip("1.1.1.1"), Fx.Ip("1.0.0.1")]));

    [Fact]
    public async Task Successful_apply_backs_up_verifies_and_flushes()
    {
        var (svc, sys, hist, cache) = Build();
        var o = await svc.ApplyAsync(Cloudflare(sys.Adapter));
        Assert.True(o.Success, o.Message);
        Assert.False(o.RolledBack);
        Assert.Equal(["1.1.1.1", "1.0.0.1"], sys.Adapter.Dns.IPv4Servers.Select(s => s.ToString()));
        Assert.False(sys.Adapter.Dns.IPv4Automatic);
        var row = Assert.Single(hist.Rows);
        Assert.Equal("Success", row.Result);
        Assert.True(row.Prev.IPv4Automatic);   // backup holds the ORIGINAL state
        Assert.Equal(1, cache.Flushes);
    }

    [Fact]
    public async Task Not_elevated_makes_no_change_and_reports_it()
    {
        var (svc, sys, hist, _) = Build(elevated: false);
        var o = await svc.ApplyAsync(Cloudflare(sys.Adapter));
        Assert.False(o.Success);
        Assert.True(o.RequiresElevation);
        Assert.Empty(sys.Applied);
        Assert.Empty(hist.Rows);
    }

    [Fact]
    public async Task Apply_failure_rolls_back_to_original()
    {
        var (svc, sys, hist, _) = Build();
        sys.FailApplyOnCall = 0;
        var o = await svc.ApplyAsync(Cloudflare(sys.Adapter));
        Assert.False(o.Success);
        Assert.True(o.RolledBack);
        Assert.True(sys.Adapter.Dns.IPv4Automatic);
        Assert.Equal("RolledBack", hist.Rows[0].Result);
        Assert.Equal(2, sys.Applied.Count); // attempt + rollback
    }

    [Fact]
    public async Task Verification_mismatch_rolls_back()
    {
        var (svc, sys, hist, _) = Build();
        sys.IgnoreChanges = true; // OS silently ignores the change
        var o = await svc.ApplyAsync(Cloudflare(sys.Adapter));
        Assert.False(o.Success);
        Assert.True(o.RolledBack);
        Assert.Contains("reads back", o.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("RolledBack", hist.Rows[0].Result);
    }

    [Fact]
    public async Task Unreachable_new_server_rolls_back()
    {
        var (svc, sys, _, _) = Build(probeOk: false);
        var o = await svc.ApplyAsync(Cloudflare(sys.Adapter));
        Assert.False(o.Success);
        Assert.True(o.RolledBack);
        Assert.True(sys.Adapter.Dns.IPv4Automatic);
        Assert.Contains("did not answer", o.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Noop_changes_nothing()
    {
        var (svc, sys, hist, _) = Build();
        var o = await svc.ApplyAsync(Fx.Plan(sys.Adapter, FamilyChange.Automatic)); // already automatic
        Assert.True(o.Success);
        Assert.Empty(sys.Applied);
        Assert.Empty(hist.Rows);
    }

    [Fact]
    public async Task Invalid_plan_is_rejected_before_touching_anything()
    {
        var (svc, sys, _, _) = Build();
        var o = await svc.ApplyAsync(Fx.Plan(sys.Adapter, FamilyChange.Manual([Fx.Ip("2606:4700:4700::1111")])));
        Assert.False(o.Success);
        Assert.Empty(sys.Applied);
    }

    [Fact]
    public async Task Restore_snapshot_returns_to_previous_state()
    {
        var (svc, sys, hist, _) = Build();
        await svc.ApplyAsync(Cloudflare(sys.Adapter));
        var o = await svc.RestoreAsync(hist.Rows[0].Prev);
        Assert.True(o.Success, o.Message);
        Assert.True(sys.Adapter.Dns.IPv4Automatic);
    }

    [Fact]
    public async Task Ipv4_and_ipv6_are_independent()
    {
        var (svc, sys, _, _) = Build();
        var plan = Fx.Plan(sys.Adapter, FamilyChange.Unchanged, FamilyChange.Manual([Fx.Ip("2606:4700:4700::1111")]));
        var o = await svc.ApplyAsync(plan);
        Assert.True(o.Success, o.Message);
        Assert.True(sys.Adapter.Dns.IPv4Automatic);       // IPv4 untouched
        Assert.False(sys.Adapter.Dns.IPv6Automatic);
    }
}

public class DnsChangePlannerTests
{
    [Fact]
    public void Preview_marks_changing_rows()
    {
        var a = Fx.Adapter();
        var rows = DnsChangePlanner.Preview(Fx.Plan(a, FamilyChange.Manual([Fx.Ip("8.8.8.8")])));
        Assert.True(rows[0].Changes);
        Assert.False(rows[1].Changes);
        Assert.Contains("8.8.8.8", rows[0].After, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_empty_duplicates_and_too_many()
    {
        var a = Fx.Adapter();
        Assert.NotEmpty(DnsChangePlanner.Validate(Fx.Plan(a, FamilyChange.Manual([]))));
        Assert.NotEmpty(DnsChangePlanner.Validate(Fx.Plan(a, FamilyChange.Manual([Fx.Ip("1.1.1.1"), Fx.Ip("1.1.1.1")]))));
        Assert.NotEmpty(DnsChangePlanner.Validate(Fx.Plan(a, FamilyChange.Manual(Enumerable.Range(1, 5).Select(i => Fx.Ip($"1.1.1.{i}"))))));
        Assert.NotEmpty(DnsChangePlanner.Validate(Fx.Plan(a, FamilyChange.Unchanged)));
        Assert.Empty(DnsChangePlanner.Validate(Fx.Plan(a, FamilyChange.Manual([Fx.Ip("1.1.1.1")]))));
    }

    [Fact]
    public void Warns_when_ipv6_servers_stay_active()
    {
        var a = Fx.Adapter(v6: ["2001:db8::1"]);
        var w = DnsChangePlanner.Warnings(Fx.Plan(a, FamilyChange.Manual([Fx.Ip("1.1.1.1")])));
        Assert.Contains(w, x => x.Contains("IPv6", StringComparison.Ordinal));
    }

    [Fact]
    public void Snapshot_json_roundtrip_and_plan()
    {
        var a = Fx.Adapter(auto4: false, v4: ["9.9.9.9", "149.112.112.112"]);
        var snap = DnsSnapshot.From(a);
        var back = DnsSnapshot.FromJson(snap.ToJson())!;
        Assert.Equal(snap.IPv4Servers, back.IPv4Servers);
        var plan = back.ToPlan(a, "x");
        Assert.Equal(DnsMode.Manual, plan.IPv4.Mode);
        Assert.Equal(DnsMode.Automatic, plan.IPv6.Mode);
    }
}
