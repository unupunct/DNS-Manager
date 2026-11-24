using DNSManagerPro.Core.Configuration;
using DNSManagerPro.Tests.Configuration;
using DNSManagerPro.Windows.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DNSManagerPro.Tests.Windows;

public class NetshConfiguratorTests
{
    private sealed class RecordingRunner(int failOn = -1, string output = "") : IProcessRunner
    {
        public List<string[]> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add([.. arguments]);
            var fail = Calls.Count - 1 == failOn;
            return Task.FromResult(new ProcessResult(fail ? 1 : 0, fail ? output : string.Empty));
        }
    }

    [Fact]
    public async Task Manual_ipv4_builds_set_then_add_with_indexes()
    {
        var runner = new RecordingRunner();
        var cfg = new NetshDnsConfigurator(runner, NullLogger<NetshDnsConfigurator>.Instance);
        var plan = Fx.Plan(Fx.Adapter(), FamilyChange.Manual([Fx.Ip("1.1.1.1"), Fx.Ip("1.0.0.1")]));
        Assert.True((await cfg.ApplyAsync(plan)).Success);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(["interface", "ipv4", "set", "dnsservers", "name=7", "source=static", "address=1.1.1.1", "validate=no"], runner.Calls[0]);
        Assert.Equal(["interface", "ipv4", "add", "dnsservers", "name=7", "address=1.0.0.1", "index=2", "validate=no"], runner.Calls[1]);
    }

    [Fact]
    public async Task Automatic_both_families_uses_dhcp_and_unchanged_runs_nothing()
    {
        var runner = new RecordingRunner();
        var cfg = new NetshDnsConfigurator(runner, NullLogger<NetshDnsConfigurator>.Instance);
        await cfg.ApplyAsync(new DnsChangePlan(Fx.Adapter(), FamilyChange.Automatic, FamilyChange.Automatic, "t"));
        Assert.Equal(["interface", "ipv4", "set", "dnsservers", "name=7", "source=dhcp"], runner.Calls[0]);
        Assert.Equal(["interface", "ipv6", "set", "dnsservers", "name=7", "source=dhcp"], runner.Calls[1]);

        var none = new RecordingRunner();
        await new NetshDnsConfigurator(none, NullLogger<NetshDnsConfigurator>.Instance).ApplyAsync(Fx.Plan(Fx.Adapter(), FamilyChange.Unchanged));
        Assert.Empty(none.Calls);
    }

    [Fact]
    public async Task Elevation_error_is_reported_as_such()
    {
        var runner = new RecordingRunner(failOn: 0, output: "The requested operation requires elevation (Run as administrator).");
        var r = await new NetshDnsConfigurator(runner, NullLogger<NetshDnsConfigurator>.Instance)
            .ApplyAsync(Fx.Plan(Fx.Adapter(), FamilyChange.Manual([Fx.Ip("1.1.1.1")])));
        Assert.False(r.Success);
        Assert.True(r.RequiresElevation);
    }

    [Fact]
    public void Arguments_contain_no_shell_metacharacters() =>
        Assert.All(
            NetshDnsConfigurator.BuildCommands(7, "ipv6", FamilyChange.Manual([Fx.Ip("2606:4700:4700::1111")])).SelectMany(c => c),
            a => Assert.DoesNotContain(';', a));
}
