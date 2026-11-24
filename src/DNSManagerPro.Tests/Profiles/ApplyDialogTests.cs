using DNSManagerPro.App.Dialogs;
using DNSManagerPro.App.Services;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Configuration;
using DNSManagerPro.Core.Models;
using DNSManagerPro.Core.Providers;
using DNSManagerPro.Tests.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DNSManagerPro.Tests.Profiles;

/// <summary>Two-adapter in-memory OS.</summary>
internal sealed class TwoAdapterSystem : INetworkAdapterService, IDnsConfigurator
{
    public List<NetworkAdapterInfo> Adapters { get; } =
    [
        Fx.Adapter() with { Id = "{a}", Name = "Eth A", InterfaceIndex = 5 },
        Fx.Adapter() with { Id = "{b}", Name = "Eth B", InterfaceIndex = 6 },
    ];

    public string? FailOnAdapter { get; set; }

    public List<string> AppliedTo { get; } = [];

    public Task<IReadOnlyList<NetworkAdapterInfo>> GetAdaptersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NetworkAdapterInfo>>([.. Adapters]);

    public Task<OperationResult> ApplyAsync(DnsChangePlan plan, CancellationToken cancellationToken = default)
    {
        AppliedTo.Add(plan.Adapter.Id);
        if (plan.Adapter.Id == FailOnAdapter)
        {
            return Task.FromResult(OperationResult.Fail("simulated failure"));
        }

        var i = Adapters.FindIndex(a => a.Id == plan.Adapter.Id);
        var d = Adapters[i].Dns;
        Adapters[i] = Adapters[i] with
        {
            Dns = new AdapterDnsConfiguration(
                plan.IPv4.Mode == DnsMode.Manual ? plan.IPv4.Servers : plan.IPv4.Mode == DnsMode.Automatic ? [Fx.Ip("192.168.1.1")] : d.IPv4Servers,
                d.IPv6Servers,
                plan.IPv4.Mode == DnsMode.Unchanged ? d.IPv4Automatic : plan.IPv4.Mode == DnsMode.Automatic,
                d.IPv6Automatic),
        };
        return Task.FromResult(OperationResult.Ok());
    }
}

internal sealed class FakeElevation : IElevationService
{
    public bool RestartAsAdministrator(string? arguments) => true;
}

public class ApplyDialogViewModelTests
{
    private static readonly DnsProvider Cloudflare = BuiltInProviders.All.First(p => p.Id == "cloudflare");

    private static (ApplyDnsViewModel Vm, TwoAdapterSystem Sys) Build(bool elevated = true, string? failOn = null)
    {
        var sys = new TwoAdapterSystem { FailOnAdapter = failOn };
        var svc = new DnsChangeService(sys, sys, new FakeHistory(), new FakePrivilege(elevated), new FakeProbe(), new FakeCache(), NullLogger<DnsChangeService>.Instance) { VerifyDelay = TimeSpan.Zero };
        var req = new ApplyDnsRequest("Apply", "Cloudflare", Cloudflare, null, string.Empty);
        return (new ApplyDnsViewModel(req, sys.Adapters, "{a}", svc, new FakeElevation(), elevated), sys);
    }

    [Fact]
    public void Only_the_preselected_adapter_is_checked_by_default()
    {
        var (vm, _) = Build();
        Assert.Equal(["{a}"], vm.Choices.Where(c => c.IsChecked).Select(c => c.Adapter.Id));
        Assert.Single(vm.Plans);
        Assert.True(vm.AllowMultiple);
    }

    [Fact]
    public void Ticking_a_second_adapter_adds_a_plan_and_labels_preview_rows_per_adapter()
    {
        var (vm, _) = Build();
        vm.Choices[1].IsChecked = true;
        Assert.Equal(2, vm.Plans.Count);
        Assert.Contains(vm.Preview, r => r.Label.StartsWith("Eth B", StringComparison.Ordinal));
        Assert.Contains(vm.Preview, r => r.Label.StartsWith("Eth A", StringComparison.Ordinal));
    }

    [Fact]
    public void Unticking_everything_blocks_apply_with_a_clear_reason()
    {
        var (vm, _) = Build();
        vm.Choices[0].IsChecked = false;
        Assert.Empty(vm.Plans);
        Assert.Contains(vm.Problems, p => p.Contains("at least one", StringComparison.OrdinalIgnoreCase));
        Assert.False(vm.PrimaryCommand.CanExecute(null));
    }

    [Fact]
    public async Task Applying_changes_only_checked_adapters()
    {
        var (vm, sys) = Build();
        await vm.PrimaryCommand.ExecuteAsync(null);
        Assert.True(vm.Succeeded);
        Assert.Equal(["{a}"], sys.AppliedTo);
        Assert.False(sys.Adapters[0].Dns.IPv4Automatic);
        Assert.True(sys.Adapters[1].Dns.IPv4Automatic);   // untouched
    }

    [Fact]
    public async Task Failure_on_second_adapter_reports_first_as_kept_and_is_not_a_success()
    {
        var (vm, sys) = Build(failOn: "{b}");
        vm.Choices[1].IsChecked = true;
        await vm.PrimaryCommand.ExecuteAsync(null);
        Assert.False(vm.Succeeded);
        Assert.True(vm.ResultIsError);
        Assert.Contains("Eth A", vm.ResultMessage, StringComparison.Ordinal);       // listed as already changed
        Assert.True(sys.Adapters[1].Dns.IPv4Automatic);                            // second rolled back
    }

    [Fact]
    public void Not_elevated_offers_restart_instead_of_apply()
    {
        var (vm, _) = Build(elevated: false);
        Assert.True(vm.NeedsElevation);
        Assert.Equal("Restart as administrator", vm.PrimaryText);
    }

    [Fact]
    public void Ipv6_defaults_to_unchanged_when_no_selected_adapter_has_ipv6()
    {
        var (vm, _) = Build();
        Assert.Equal(FamilyChoice.Unchanged, vm.ChoiceV6);
        Assert.Equal(FamilyChoice.Source, vm.ChoiceV4);
    }
}

public class ProfileServiceNameTests
{
    [Fact]
    public void Unique_name_appends_a_counter_and_respects_case()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Gaming", "Gaming (2)" };
        Assert.Equal("Work", ProfileService.UniqueName("Work", existing));
        Assert.Equal("gaming (3)", ProfileService.UniqueName("gaming", existing));   // collision check ignores case; the caller's spelling is kept
    }

    [Fact]
    public void Unique_name_stays_within_the_length_limit()
    {
        var longName = new string('x', 60);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { longName };
        Assert.True(ProfileService.UniqueName(longName, existing).Length <= 60);
    }
}

