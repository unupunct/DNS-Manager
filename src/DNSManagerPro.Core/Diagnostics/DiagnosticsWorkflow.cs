using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Models;
using DNSManagerPro.Core.Providers;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.Core.Diagnostics;

/// <summary>The automated troubleshooting sequence: connectivity, gateway, DNS servers, OS resolution, alternative resolvers, analysis, report.</summary>
public sealed class DiagnosticsWorkflow(
    INetworkAdapterService adapters,
    IDnsLookupClient lookup,
    IPingService ping,
    IOsResolver osResolver,
    ILogger<DiagnosticsWorkflow> logger)
{
    public const string ServerTestDomain = "cloudflare.com";

    private static readonly string[] OsTestHosts = ["cloudflare.com", "microsoft.com"];
    private static readonly (string Name, IPAddress Ip)[] AlternativeResolvers =
    [
        ("Cloudflare", IPAddress.Parse("1.1.1.1")),
        ("Google", IPAddress.Parse("8.8.8.8")),
    ];

    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(2);

    public async Task<DiagnosticRun> RunAsync(IProgress<DiagnosticStep>? progress = null, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var steps = new List<DiagnosticStep>();
        void Add(DiagnosticStatus s, string title, string detail)
        {
            var step = new DiagnosticStep(title, s, detail);
            steps.Add(step);
            progress?.Report(step);
            logger.LogInformation("Diagnostics: [{Status}] {Title}: {Detail}", s, title, detail);
        }

        // 1. connectivity
        var all = await adapters.GetAdaptersAsync(ct);
        var active = ActiveAdapterSelector.Select(all);
        AdapterFacts? facts = null;
        if (active is null)
        {
            Add(DiagnosticStatus.Fail, "Network connectivity", "No connected network adapter was found.");
        }
        else
        {
            facts = new AdapterFacts(
                active.Name, active.IsUp, active.IPv4Addresses.Count > 0, active.HasGateway, !active.Dns.IPv4Automatic || !active.Dns.IPv6Automatic,
                [.. active.Dns.IPv4Servers, .. active.Dns.IPv6Servers]);
            Add(facts.HasIPv4 ? DiagnosticStatus.Pass : DiagnosticStatus.Fail, "Network connectivity",
                facts.HasIPv4 ? $"'{active.Name}' is connected with address {active.IPv4Addresses[0]}." : $"'{active.Name}' is connected but has no IPv4 address.");
        }

        // 2. gateway
        PingResult? gw = null;
        if (active is { Gateways.Count: > 0 })
        {
            var gateway = active.Gateways.FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork) ?? active.Gateways[0];
            gw = await ping.PingAsync(gateway, TimeSpan.FromMilliseconds(1500), ct);
            Add(gw.Success ? DiagnosticStatus.Pass : DiagnosticStatus.Warning, "Default gateway",
                gw.Success ? $"{gateway} answered ping in {gw.Milliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms." : $"{gateway} did not answer ping ({gw.Status}). Some routers ignore ping.");
        }
        else
        {
            Add(DiagnosticStatus.Skipped, "Default gateway", active is null ? "Skipped: no active adapter." : "The adapter has no default gateway.");
        }

        // 3. configured DNS servers
        var configured = new List<DnsServerCheck>();
        var servers = active is null ? [] : active.Dns.IPv4Servers.Concat(active.Dns.IPv6Servers).Take(4).ToList();
        foreach (var s in servers)
        {
            ct.ThrowIfCancellationRequested();
            var r = await lookup.LookupAsync(s, s.ToString(), ServerTestDomain, LookupType.A, DnsTimeout, ct);
            configured.Add(new DnsServerCheck(s, true, r.Answered, r.Milliseconds, r.ResponseCode, r.Error));
            Add(r.Success ? DiagnosticStatus.Pass : r.Answered ? DiagnosticStatus.Warning : DiagnosticStatus.Fail, $"DNS server {s}",
                r.Success ? $"Answered in {r.Milliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms." : r.Answered ? $"Answered with {r.ResponseCode}." : $"No answer ({r.ResponseCode}{(r.Error is null ? string.Empty : ": " + r.Error)}).");
        }

        if (servers.Count == 0)
        {
            Add(active is null ? DiagnosticStatus.Skipped : DiagnosticStatus.Fail, "DNS servers", active is null ? "Skipped: no active adapter." : "No DNS servers are configured on the adapter.");
        }

        // 4. OS resolution
        var os = new List<OsResolution>();
        foreach (var h in OsTestHosts)
        {
            ct.ThrowIfCancellationRequested();
            var r = await osResolver.ResolveAsync(h, TimeSpan.FromSeconds(4), ct);
            os.Add(r);
            Add(r.Success ? DiagnosticStatus.Pass : DiagnosticStatus.Fail, $"Windows name resolution: {h}",
                r.Success ? $"Resolved to {string.Join(", ", r.Addresses.Take(3))} in {r.Milliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms." : $"Failed: {r.Error}");
        }

        // 5. alternative resolvers
        var alts = new List<DnsServerCheck>();
        foreach (var (name, ip) in AlternativeResolvers.Where(a => !servers.Contains(a.Ip)))
        {
            ct.ThrowIfCancellationRequested();
            var r = await lookup.LookupAsync(ip, name, ServerTestDomain, LookupType.A, DnsTimeout, ct);
            alts.Add(new DnsServerCheck(ip, false, r.Answered, r.Milliseconds, r.ResponseCode, r.Error));
            Add(r.Success ? DiagnosticStatus.Pass : DiagnosticStatus.Warning, $"Comparison resolver {name} ({ip})",
                r.Success ? $"Answered in {r.Milliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms." : $"No usable answer ({r.ResponseCode}).");
        }

        // 6. analysis
        var findings = DiagnosticRules.Evaluate(new DiagnosticFacts(facts, gw, configured, alts, os));
        var overall = DiagnosticRules.Overall(findings);
        var problemCount = findings.Count(f => f.Severity != FindingSeverity.Info);
        Add(overall == DiagnosticOverall.Healthy ? DiagnosticStatus.Pass : overall == DiagnosticOverall.Warnings ? DiagnosticStatus.Warning : DiagnosticStatus.Fail,
            "Analysis", problemCount == 0 ? "No problems detected." : $"{problemCount} problem(s) detected.");

        // 7. report
        var report = DiagnosticReport.Build(started, overall, steps, findings);
        return new DiagnosticRun(Guid.NewGuid(), started, DateTime.UtcNow, steps, findings, overall, report);
    }
}

