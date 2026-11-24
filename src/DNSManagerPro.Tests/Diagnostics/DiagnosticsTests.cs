using System.Net;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Configuration;
using DNSManagerPro.Core.Diagnostics;
using DNSManagerPro.Core.History;
using DNSManagerPro.Core.Models;
using DNSManagerPro.Core.Monitoring;
using DNSManagerPro.Core.Settings;
using DNSManagerPro.Data;
using DNSManagerPro.Infrastructure;
using DNSManagerPro.Networking;
using DNSManagerPro.Tests.Configuration;
using DNSManagerPro.Windows.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DNSManagerPro.Tests.Diagnostics;

public class ReverseAndPropagationTests
{
    [Theory]
    [InlineData("8.8.4.4", "4.4.8.8.in-addr.arpa")]
    [InlineData("192.168.1.10", "10.1.168.192.in-addr.arpa")]
    [InlineData("2001:db8::1", "1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2.ip6.arpa")]
    public void Reverse_names(string ip, string expected) => Assert.Equal(expected, ReverseName.For(IPAddress.Parse(ip)));

    [Fact]
    public void Prepare_converts_ip_for_ptr_but_not_for_other_types()
    {
        Assert.Equal("4.4.8.8.in-addr.arpa", DnsLookupClient.Prepare("8.8.4.4", LookupType.PTR));
        Assert.Equal("example.com", DnsLookupClient.Prepare(" Example.com. ", LookupType.A).ToLowerInvariant());
        Assert.Equal("xn--bcher-kva.example", DnsLookupClient.Prepare("bücher.example", LookupType.A));
    }

    private static LookupResult R(string name, string code, params string[] data) =>
        new("x.com", IPAddress.Parse("1.1.1.1"), name, LookupType.A, 10, code, [.. data.Select(d => new DnsRecordInfo("A", "x.com", 60, d))], null, false);

    [Fact]
    public void Identical_answers_are_consistent_regardless_of_order()
    {
        var s = PropagationAnalysis.Summarize([R("A", "NoError", "1.1.1.1", "2.2.2.2"), R("B", "NoError", "2.2.2.2", "1.1.1.1")], LookupType.A);
        Assert.True(s.Consistent);
        Assert.Single(s.Groups);
        Assert.Contains("same answer", s.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_answers_are_grouped_and_explained_without_calling_it_an_error()
    {
        var s = PropagationAnalysis.Summarize([R("A", "NoError", "1.1.1.1"), R("B", "NoError", "1.1.1.1"), R("C", "NoError", "9.9.9.9"), R("D", "Refused")], LookupType.A);
        Assert.False(s.Consistent);
        Assert.Equal(2, s.Groups.Count);
        Assert.Equal(2, s.Groups[0].Resolvers.Count);   // majority first
        Assert.Contains("CDN", s.Text, StringComparison.Ordinal);
        Assert.Equal(3, s.Responded);
        Assert.Equal(4, s.Total);
    }

    [Fact]
    public void Nobody_answering_is_reported() =>
        Assert.Contains("No resolver", PropagationAnalysis.Summarize([R("A", "Refused")], LookupType.A).Text, StringComparison.Ordinal);
}

public class RulesTests
{
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    private static AdapterFacts Adapter(bool up = true, bool v4 = true, bool manual = false, params string[] dns) =>
        new("Ethernet", up, v4, true, manual, [.. (dns.Length == 0 ? ["192.168.1.1"] : dns).Select(Ip)]);

    private static DnsServerCheck Check(string ip, bool answered, double ms = 20, string code = "NoError", bool configured = true) =>
        new(Ip(ip), configured, answered, ms, answered ? code : "Timeout", null);

    private static OsResolution Os(bool ok, double ms = 20) => new("x.com", ok, ms, ok ? ["1.2.3.4"] : [], ok ? null : "fail");

    private static DiagnosticFacts Facts(AdapterFacts? a = null, PingResult? gw = null, DnsServerCheck[]? configured = null, DnsServerCheck[]? alts = null, OsResolution[]? os = null) =>
        new(a ?? Adapter(), gw ?? new PingResult(true, 1, "Success"), configured ?? [Check("192.168.1.1", true)], alts ?? [Check("1.1.1.1", true, configured: false)], os ?? [Os(true), Os(true)]);

    [Fact]
    public void Healthy_system_has_no_problems()
    {
        var f = DiagnosticRules.Evaluate(Facts());
        Assert.Equal(DiagnosticOverall.Healthy, DiagnosticRules.Overall(f));
    }

    [Fact]
    public void No_adapter_is_critical_and_stops_further_analysis()
    {
        var f = DiagnosticRules.Evaluate(Facts(a: null) with { Adapter = null });
        var only = Assert.Single(f);
        Assert.Equal(FindingSeverity.Critical, only.Severity);
        Assert.NotEmpty(only.PossibleCauses);
    }

    [Fact]
    public void Dead_configured_server_while_public_resolvers_work_is_called_out()
    {
        var f = DiagnosticRules.Evaluate(Facts(configured: [Check("192.168.1.1", false)]));
        var finding = Assert.Single(f, x => x.Severity == FindingSeverity.Critical);
        Assert.Contains("192.168.1.1", finding.Detected, StringComparison.Ordinal);
        Assert.Contains("public resolvers are reachable", finding.Detected, StringComparison.Ordinal);
    }

    [Fact]
    public void Everything_unreachable_is_one_finding_not_many()
    {
        var f = DiagnosticRules.Evaluate(Facts(configured: [Check("192.168.1.1", false)], alts: [Check("1.1.1.1", false, configured: false), Check("8.8.8.8", false, configured: false)], os: [Os(false), Os(false)]));
        Assert.Single(f, x => x.Severity == FindingSeverity.Critical);
        Assert.Contains("No DNS server could be reached", f.First(x => x.Severity == FindingSeverity.Critical).Detected, StringComparison.Ordinal);
    }

    [Fact]
    public void Os_failure_with_working_direct_dns_points_at_the_local_resolver_and_suggests_flushing()
    {
        var f = DiagnosticRules.Evaluate(Facts(os: [Os(false), Os(false)]));
        var finding = Assert.Single(f, x => x.Severity == FindingSeverity.Warning);
        Assert.Contains("Windows could not resolve", finding.Detected, StringComparison.Ordinal);
        Assert.Contains("Flush", finding.Suggestion, StringComparison.Ordinal);
        Assert.Contains(finding.PossibleCauses, c => c.Contains("cache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Servfail_from_configured_server_is_a_warning()
    {
        var f = DiagnosticRules.Evaluate(Facts(configured: [Check("192.168.1.1", true, code: "ServerFailure")]));
        Assert.Contains(f, x => x.Severity == FindingSeverity.Warning && x.Detected.Contains("ServerFailure", StringComparison.Ordinal));
    }

    [Fact]
    public void Gateway_ping_failure_is_only_a_warning()
    {
        var f = DiagnosticRules.Evaluate(Facts(gw: new PingResult(false, 0, "TimedOut")));
        Assert.Equal(DiagnosticOverall.Warnings, DiagnosticRules.Overall(f));
        Assert.Contains(f.Single().PossibleCauses, c => c.Contains("ignores ping", StringComparison.Ordinal));
    }

    [Fact]
    public void Slow_server_and_single_manual_server_are_informational()
    {
        var f = DiagnosticRules.Evaluate(Facts(a: Adapter(manual: true, dns: "9.9.9.9"), configured: [Check("9.9.9.9", true, ms: 400)]));
        Assert.All(f, x => Assert.Equal(FindingSeverity.Info, x.Severity));
        Assert.True(f.Count >= 2);
        Assert.Equal(DiagnosticOverall.Healthy, DiagnosticRules.Overall(f));
    }

    [Fact]
    public void No_dns_servers_configured_is_critical() =>
        Assert.Contains(DiagnosticRules.Evaluate(Facts(a: new AdapterFacts("E", true, true, true, false, []), configured: [], alts: [Check("1.1.1.1", true, configured: false)])),
            x => x.Severity == FindingSeverity.Critical && x.Detected.Contains("No DNS server is configured", StringComparison.Ordinal));
}

internal sealed class ScriptedLookup(Func<IPAddress, LookupResult> script) : IDnsLookupClient
{
    public Task<LookupResult> LookupAsync(IPAddress server, string serverName, string domain, LookupType type, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(script(server));
}

internal sealed class ScriptedPing(bool ok) : IPingService
{
    public Task<PingResult> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(new PingResult(ok, 2, ok ? "Success" : "TimedOut"));
}

internal sealed class ScriptedOs(bool ok) : IOsResolver
{
    public Task<OsResolution> ResolveAsync(string host, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(new OsResolution(host, ok, 10, ok ? ["1.2.3.4"] : [], ok ? null : "no such host"));
}

public class WorkflowTests
{
    private static LookupResult Ok(IPAddress s) => new("cloudflare.com", s, s.ToString(), LookupType.A, 12, "NoError", [], null, false);

    private static LookupResult Down(IPAddress s) => new("cloudflare.com", s, s.ToString(), LookupType.A, 2000, "Timeout", [], "timeout", true);

    private static DiagnosticsWorkflow Build(Func<IPAddress, LookupResult> lookup, bool os = true, bool ping = true)
    {
        var sys = new FakeSystem();
        return new DiagnosticsWorkflow(sys, new ScriptedLookup(lookup), new ScriptedPing(ping), new ScriptedOs(os), NullLogger<DiagnosticsWorkflow>.Instance);
    }

    [Fact]
    public async Task Healthy_run_passes_every_step_and_produces_a_report()
    {
        var run = await Build(Ok).RunAsync();
        Assert.Equal(DiagnosticOverall.Healthy, run.Overall);
        Assert.DoesNotContain(run.Steps, s => s.Status == DiagnosticStatus.Fail);
        Assert.Contains("CHECKS PERFORMED", run.ReportText, StringComparison.Ordinal);
        Assert.Contains("DETECTED PROBLEMS", run.ReportText, StringComparison.Ordinal);
        Assert.Contains("POSSIBLE CAUSES", run.ReportText, StringComparison.Ordinal);
        Assert.True(run.Steps.Count >= 6);
    }

    [Fact]
    public async Task Dead_configured_dns_is_detected_and_report_separates_detected_from_possible_causes()
    {
        var run = await Build(s => s.ToString() == "192.168.1.1" ? Down(s) : Ok(s)).RunAsync();
        Assert.Equal(DiagnosticOverall.Problems, run.Overall);
        var detectedIdx = run.ReportText.IndexOf("DETECTED PROBLEMS", StringComparison.Ordinal);
        var causesIdx = run.ReportText.IndexOf("POSSIBLE CAUSES", StringComparison.Ordinal);
        Assert.True(detectedIdx < causesIdx);
        Assert.Contains("not confirmed", run.ReportText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Os_resolution_failure_is_reported_when_direct_dns_works()
    {
        var run = await Build(Ok, os: false).RunAsync();
        Assert.Contains(run.Findings, f => f.Detected.Contains("Windows could not resolve", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Progress_is_reported_per_step_and_cancellation_is_honoured()
    {
        var seen = new List<DiagnosticStep>();
        var run = await Build(Ok).RunAsync(new SyncProgress<DiagnosticStep>(seen.Add));
        Assert.Equal(run.Steps.Count, seen.Count);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(Ok).RunAsync(null, cts.Token));
    }

    private sealed class SyncProgress<T>(Action<T> a) : IProgress<T>
    {
        public void Report(T value) => a(value);
    }
}

public class MonitorTests
{
    private static MonitorObservation Obs(string fp = "A:1", bool? ok = true, string id = "a", double ms = 20) =>
        new(DateTime.UtcNow, id, "Ethernet", fp, "desc " + fp, ok, ok == true ? ms : null, ok == false ? "timeout" : null);

    [Fact]
    public void First_observation_only_sets_the_baseline() => Assert.Empty(new MonitorStateMachine().Observe(Obs()));

    [Fact]
    public void External_config_change_is_reported_once_with_before_and_after()
    {
        var m = new MonitorStateMachine();
        m.Observe(Obs("A:1"));
        var e = Assert.Single(m.Observe(Obs("M:9.9.9.9")));
        Assert.Equal(MonitorEventKind.ConfigChanged, e.Kind);
        Assert.Contains("A:1", e.Details, StringComparison.Ordinal);
        Assert.Empty(m.Observe(Obs("M:9.9.9.9")));   // not repeated
    }

    [Fact]
    public void Change_made_by_this_app_is_adopted_silently()
    {
        var m = new MonitorStateMachine();
        m.Observe(Obs("A:1"));
        Assert.Empty(m.Observe(Obs("M:1.1.1.1"), changeMadeByThisApp: true));
        Assert.Empty(m.Observe(Obs("M:1.1.1.1")));
    }

    [Fact]
    public void Failure_needs_consecutive_failures_then_reports_once_and_recovery_once()
    {
        var m = new MonitorStateMachine(2);
        Assert.Empty(m.Observe(Obs(ok: false)));                                   // 1st failure: ignored (could be packet loss)
        Assert.Equal(MonitorEventKind.ResolutionFailure, Assert.Single(m.Observe(Obs(ok: false))).Kind);
        Assert.True(m.IsFailing);
        Assert.Empty(m.Observe(Obs(ok: false)));                                   // still failing: no repeat
        Assert.Equal(MonitorEventKind.Recovered, Assert.Single(m.Observe(Obs(ok: true))).Kind);
        Assert.False(m.IsFailing);
        Assert.Empty(m.Observe(Obs(ok: true)));
    }

    [Fact]
    public void A_success_resets_the_failure_streak()
    {
        var m = new MonitorStateMachine(2);
        m.Observe(Obs(ok: false));
        m.Observe(Obs(ok: true));
        Assert.Empty(m.Observe(Obs(ok: false)));
    }

    [Fact]
    public void Adapter_switch_is_reported_and_unprobed_ticks_are_neutral()
    {
        var m = new MonitorStateMachine();
        m.Observe(Obs(id: "a"));
        Assert.Equal(MonitorEventKind.AdapterChanged, Assert.Single(m.Observe(Obs(id: "b"))).Kind);
        Assert.Empty(m.Observe(Obs(id: "b", ok: null)));
    }

    [Fact]
    public void Fingerprint_distinguishes_mode_and_servers()
    {
        Assert.NotEqual(MonitorStateMachine.Fingerprint(true, ["1.1.1.1"], true, []), MonitorStateMachine.Fingerprint(false, ["1.1.1.1"], true, []));
        Assert.NotEqual(MonitorStateMachine.Fingerprint(false, ["1.1.1.1"], true, []), MonitorStateMachine.Fingerprint(false, ["8.8.8.8"], true, []));
    }
}

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dnsmp-s-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // temp
        }
    }

    private SettingsService Svc() => new(Path.Combine(_dir, "settings.json"), NullLogger<SettingsService>.Instance);

    [Fact]
    public void Defaults_when_no_file_and_roundtrip_after_update()
    {
        var s = Svc();
        s.Load();
        Assert.False(s.Current.MonitoringEnabled);
        Assert.Equal(5, s.Current.MonitoringIntervalMinutes);
        s.Update(c => c with { MonitoringEnabled = true, MonitoringIntervalMinutes = 10, NotifyOnFailure = false });
        var again = Svc();
        again.Load();
        Assert.True(again.Current.MonitoringEnabled);
        Assert.Equal(10, again.Current.MonitoringIntervalMinutes);
        Assert.False(again.Current.NotifyOnFailure);
    }

    [Fact]
    public void Out_of_range_interval_is_clamped_and_change_event_fires()
    {
        var s = Svc();
        AppSettings? seen = null;
        s.Changed += x => seen = x;
        s.Update(c => c with { MonitoringIntervalMinutes = 0 });
        Assert.Equal(AppSettings.MinIntervalMinutes, s.Current.MonitoringIntervalMinutes);
        Assert.Equal(AppSettings.MinIntervalMinutes, seen!.MonitoringIntervalMinutes);
        s.Update(c => c with { MonitoringIntervalMinutes = 99999 });
        Assert.Equal(AppSettings.MaxIntervalMinutes, s.Current.MonitoringIntervalMinutes);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_is_set_aside()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        var s = Svc();
        s.Load();
        Assert.False(s.Current.MonitoringEnabled);
        Assert.True(File.Exists(path + ".bad"));
    }
}

public class CacheParseTests
{
    [Fact]
    public void Parses_array_single_object_and_empty()
    {
        var arr = PowerShellDnsCacheInspector.Parse("[{\"Entry\":\"a.com\",\"Name\":\"a.com\",\"Type\":1,\"Status\":0,\"TimeToLive\":120,\"Data\":\"1.2.3.4\"},{\"Entry\":\"b.com\",\"Type\":28,\"Status\":9003,\"TimeToLive\":5,\"Data\":null}]");
        Assert.Equal(2, arr.Count);
        Assert.Equal("A", arr[0].Type);
        Assert.Equal("1.2.3.4", arr[0].Data);
        Assert.Equal("AAAA", arr[1].Type);
        Assert.Equal("Name does not exist", arr[1].Status);
        Assert.Single(PowerShellDnsCacheInspector.Parse("{\"Entry\":\"x.com\",\"Type\":5,\"Status\":0,\"TimeToLive\":1,\"Data\":\"y.com\"}"));
        Assert.Empty(PowerShellDnsCacheInspector.Parse("  "));
    }

    [Fact]
    public void Malformed_output_throws_a_json_exception_instead_of_returning_garbage() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => PowerShellDnsCacheInspector.Parse("Access denied"));
}

public class TimelineTests
{
    private static TimelineEntry E(TimelineKind k, string title, DateTime t, TimelineStatus s = TimelineStatus.Success, string detail = "") =>
        new(new TimelineKey(k, Guid.NewGuid().ToString()), t, title, detail, s);

    [Fact]
    public void Filters_by_kind_failure_window_and_text_and_sorts_newest_first()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var all = new[]
        {
            E(TimelineKind.ConfigChange, "DNS settings changed", now.AddHours(-1), detail: "After: 1.1.1.1"),
            E(TimelineKind.ConfigChange, "DNS change failed", now.AddDays(-3), TimelineStatus.Failed),
            E(TimelineKind.Benchmark, "DNS benchmark", now.AddDays(-40)),
            E(TimelineKind.Monitoring, "DNS resolution failed 2 times in a row.", now.AddMinutes(-5), TimelineStatus.Failed),
        };
        Assert.Equal(4, TimelineFilter.None.Apply(all, now).Count());
        Assert.Equal("DNS resolution failed 2 times in a row.", TimelineFilter.None.Apply(all, now).First().Title);
        Assert.Equal(2, new TimelineFilter(null, true, TimeWindow.All, "").Apply(all, now).Count());
        Assert.Equal(2, new TimelineFilter(null, false, TimeWindow.Day, "").Apply(all, now).Count());
        Assert.Single(new TimelineFilter(TimelineKind.Benchmark, false, TimeWindow.All, "").Apply(all, now));
        Assert.Single(new TimelineFilter(null, false, TimeWindow.All, "1.1.1.1").Apply(all, now));   // searches detail too
    }

    [Fact]
    public void Exports_are_escaped_and_parseable()
    {
        var e = E(TimelineKind.ConfigChange, "=HYPERLINK(\"x\")", DateTime.UtcNow, detail: "a,b\nc");
        var csv = TimelineExporter.Csv([e]);
        Assert.Contains("'=HYPERLINK", csv, StringComparison.Ordinal);
        using var doc = System.Text.Json.JsonDocument.Parse(TimelineExporter.JsonText([e]));
        Assert.Equal(1, doc.RootElement.GetArrayLength());
    }
}

public sealed class DiagnosticsRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dnsmp-d-{Guid.NewGuid():N}.db");
    private readonly SqliteDatabase _db;

    public DiagnosticsRepositoryTests()
    {
        _db = new SqliteDatabase(_path);
        _db.Migrate();
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

    [Fact]
    public async Task Diagnostic_runs_and_monitor_events_roundtrip()
    {
        var d = new SqliteDiagnosticsRepository(_db);
        var run = new DiagnosticRun(Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow, [], [new DiagnosticFinding(FindingSeverity.Critical, "boom", [])], DiagnosticOverall.Problems, "REPORT");
        await d.SaveAsync(run);
        var got = Assert.Single(await d.GetRecentAsync(5));
        Assert.Equal("REPORT", got.ReportText);
        Assert.Equal(DiagnosticOverall.Problems, got.Overall);

        var m = new SqliteMonitorRepository(_db);
        await m.AddAsync(new MonitorEvent(DateTime.UtcNow, MonitorEventKind.ResolutionFailure, "failed", "why"));
        var ev = Assert.Single(await m.GetRecentAsync(5));
        Assert.Equal(MonitorEventKind.ResolutionFailure, ev.Kind);
        Assert.Equal("why", ev.Details);
    }

    [Fact]
    public async Task Timeline_merges_all_sources_maps_status_and_deletes_selected_only()
    {
        var hist = new SqliteHistoryRepository(_db);
        var okId = await hist.BeginAsync("{a}", "Ethernet", new DnsSnapshot("{a}", 3, "Ethernet", true, ["192.168.1.1"], true, [], DateTime.UtcNow), "IPv4: 1.1.1.1 (Cloudflare)", "Apply");
        await hist.CompleteAsync(okId, "Success", null);
        var badId = await hist.BeginAsync("{a}", "Ethernet", new DnsSnapshot("{a}", 3, "Ethernet", true, ["192.168.1.1"], true, [], DateTime.UtcNow), "IPv4: 9.9.9.9", "Apply");
        await hist.CompleteAsync(badId, "RolledBack", "server did not answer");
        await new SqliteMonitorRepository(_db).AddAsync(new MonitorEvent(DateTime.UtcNow, MonitorEventKind.Recovered, "works again"));
        await new SqliteDiagnosticsRepository(_db).SaveAsync(new DiagnosticRun(Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow, [], [], DiagnosticOverall.Healthy, "r"));

        var repo = new SqliteTimelineRepository(_db);
        var all = await repo.GetAsync(100);
        Assert.Equal(4, all.Count);
        var changed = all.First(e => e.Title == "DNS settings changed");
        Assert.Equal(TimelineStatus.Success, changed.Status);
        Assert.Contains("Before: IPv4: automatic", changed.Detail, StringComparison.Ordinal);
        Assert.Contains("After: IPv4: 1.1.1.1", changed.Detail, StringComparison.Ordinal);
        var rolled = all.First(e => e.Title == "DNS change rolled back");
        Assert.True(rolled.IsFailure);
        Assert.Contains("server did not answer", rolled.Detail, StringComparison.Ordinal);

        var removed = await repo.DeleteAsync([rolled.Key, all.First(e => e.Kind == TimelineKind.Diagnostic).Key]);
        Assert.Equal(2, removed);
        var left = await repo.GetAsync(100);
        Assert.Equal(2, left.Count);
        Assert.DoesNotContain(left, e => e.Kind == TimelineKind.Diagnostic);
        Assert.Contains(left, e => e.Kind == TimelineKind.Monitoring);
    }
}

