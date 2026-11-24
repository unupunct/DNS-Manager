using System.Globalization;

namespace DNSManagerPro.Core.Diagnostics;

/// <summary>Turns observations into findings. Pure function: no I/O, fully unit-tested.</summary>
public static class DiagnosticRules
{
    public const double HighServerLatencyMs = 150;
    public const double SlowResolutionMs = 300;

    public static IReadOnlyList<DiagnosticFinding> Evaluate(DiagnosticFacts f)
    {
        var list = new List<DiagnosticFinding>();
        var a = f.Adapter;

        if (a is null || !a.IsUp)
        {
            list.Add(new(FindingSeverity.Critical, "No active network connection was found.",
                ["The network cable is unplugged or Wi-Fi is switched off", "The adapter is disabled", "The router or switch is down"],
                "Reconnect the network, then run the check again."));
            return list;
        }

        if (!a.HasIPv4)
        {
            list.Add(new(FindingSeverity.Critical, $"'{a.Name}' is up but has no IPv4 address.",
                ["The DHCP server (usually the router) is not answering", "A static address was never configured", "The adapter only obtained a self-assigned (169.254.x.x) address"],
                "Try 'ipconfig /renew' or restart the router."));
        }

        if (f.Gateway is { Success: false } g)
        {
            list.Add(new(FindingSeverity.Warning, $"The default gateway did not answer ICMP ping ({g.Status}).",
                ["The router is down or unreachable", "The router simply ignores ping (common and harmless)", "A wrong subnet or gateway address is configured"],
                "If websites still load, this can be ignored."));
        }

        var anyConfiguredAnswered = f.Configured.Any(c => c.Answered);
        var anyAltAnswered = f.Alternatives.Any(c => c.Answered);

        if (a.DnsServers.Count == 0)
        {
            list.Add(new(FindingSeverity.Critical, "No DNS server is configured on the active adapter.",
                ["DHCP did not provide DNS servers", "Manual settings were cleared"],
                "Set DNS to automatic or choose a provider."));
        }
        else if (!anyConfiguredAnswered && !anyAltAnswered && (f.Configured.Count + f.Alternatives.Count) > 0)
        {
            list.Add(new(FindingSeverity.Critical, "No DNS server could be reached directly, neither the configured ones nor public resolvers.",
                ["There is no working internet connection", "A firewall, security product or ISP blocks UDP port 53", "A captive portal (hotel or public Wi-Fi) is waiting for a login", "A VPN is intercepting traffic"],
                "Check that you are online, then retry. If a VPN is active, test with it off."));
        }
        else
        {
            foreach (var c in f.Configured.Where(c => !c.Answered && anyAltAnswered))
            {
                list.Add(new(FindingSeverity.Critical, $"The configured DNS server {c.Server} is not responding ({c.ResponseCode}), although public resolvers are reachable.",
                    ["The DNS server (often the router or ISP resolver) is down or overloaded", "A static address that no longer exists is configured", "A firewall rule blocks this specific server"],
                    "Switch to a public DNS provider or restore automatic DNS."));
            }
        }

        foreach (var c in f.Configured.Where(c => c.Answered && c.ResponseCode != "NoError"))
        {
            list.Add(new(FindingSeverity.Warning, $"The configured DNS server {c.Server} answered '{c.ResponseCode}' for a domain that exists.",
                ["The server blocks or filters this domain", "The server's upstream resolver is failing", "DNS hijacking or a captive portal rewrote the answer"],
                "Compare with another provider using the Propagation tab."));
        }

        var osFailed = f.OsResolutions.Count > 0 && f.OsResolutions.All(r => !r.Success);
        if (osFailed && anyConfiguredAnswered)
        {
            list.Add(new(FindingSeverity.Warning, "Windows could not resolve test domains, although the configured DNS server answers direct queries.",
                ["A stale or corrupt DNS cache (try flushing it)", "The DNS Client service is stopped or misbehaving", "A hosts file entry, proxy or VPN client overrides resolution", "A security product intercepts system DNS"],
                "Flush the DNS cache, then run the check again."));
        }
        else if (osFailed && !anyConfiguredAnswered && anyAltAnswered)
        {
            // already explained by the configured-server finding
        }

        foreach (var c in f.Configured.Where(c => c.Answered && c.Milliseconds > HighServerLatencyMs))
        {
            list.Add(new(FindingSeverity.Info, $"The DNS server {c.Server} responded slowly ({c.Milliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms).",
                ["The server is far away or overloaded", "Network congestion at the time of the test (one sample proves little)"],
                "Run the DNS Benchmark to compare providers over many queries."));
        }

        var okOs = f.OsResolutions.Where(r => r.Success).Select(r => r.Milliseconds).Order().ToList();
        if (okOs.Count > 0 && okOs[okOs.Count / 2] > SlowResolutionMs)
        {
            list.Add(new(FindingSeverity.Info, $"Name resolution through Windows was slow (median {okOs[okOs.Count / 2].ToString("F0", CultureInfo.InvariantCulture)} ms).",
                ["Cold caches make first lookups slower", "A slow DNS server or an unreachable secondary server causing timeouts"],
                null));
        }

        if (a.DnsManual && a.DnsServers.Count == 1)
        {
            list.Add(new(FindingSeverity.Info, "Only one manual DNS server is configured, so there is no fallback if it fails.",
                ["A secondary server was never entered"], "Add a secondary server."));
        }

        return list;
    }

    public static DiagnosticOverall Overall(IReadOnlyList<DiagnosticFinding> findings) =>
        findings.Any(x => x.Severity == FindingSeverity.Critical) ? DiagnosticOverall.Problems
        : findings.Any(x => x.Severity == FindingSeverity.Warning) ? DiagnosticOverall.Warnings
        : DiagnosticOverall.Healthy;
}
