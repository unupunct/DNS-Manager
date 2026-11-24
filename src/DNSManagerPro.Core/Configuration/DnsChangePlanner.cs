using System.Net;
using System.Net.Sockets;
using DNSManagerPro.Core.Models;

namespace DNSManagerPro.Core.Configuration;

/// <summary>Validates a plan and renders the before/after preview shown to the user before anything is changed.</summary>
public static class DnsChangePlanner
{
    public const int MaxServersPerFamily = 4;

    public static IReadOnlyList<string> Validate(DnsChangePlan plan)
    {
        var errors = new List<string>();
        Check("IPv4", plan.IPv4, AddressFamily.InterNetwork, errors);
        Check("IPv6", plan.IPv6, AddressFamily.InterNetworkV6, errors);
        if (plan.IPv4.Mode == DnsMode.Unchanged && plan.IPv6.Mode == DnsMode.Unchanged)
        {
            errors.Add("Nothing selected to change.");
        }

        return errors;
    }

    private static void Check(string name, FamilyChange c, AddressFamily family, List<string> errors)
    {
        if (c.Mode != DnsMode.Manual)
        {
            return;
        }

        if (c.Servers.Count == 0)
        {
            errors.Add($"{name}: at least one DNS server is required for manual mode.");
        }

        if (c.Servers.Count > MaxServersPerFamily)
        {
            errors.Add($"{name}: at most {MaxServersPerFamily} DNS servers are supported.");
        }

        if (c.Servers.Any(s => s.AddressFamily != family))
        {
            errors.Add($"{name}: contains an address of the wrong IP family.");
        }

        if (c.Servers.Select(s => s.ToString()).Distinct().Count() != c.Servers.Count)
        {
            errors.Add($"{name}: duplicate DNS servers.");
        }

        foreach (var s in c.Servers)
        {
            var r = family == AddressFamily.InterNetwork ? Providers.DnsValidator.ValidateIPv4(s.ToString()) : Providers.DnsValidator.ValidateIPv6(s.ToString());
            if (!r.IsValid)
            {
                errors.Add($"{name}: {r.Error}");
            }
        }
    }

    public static bool IsNoOp(DnsChangePlan plan) =>
        FamilyIsNoOp(plan.IPv4, plan.Adapter.Dns.IPv4Automatic, plan.Adapter.Dns.IPv4Servers)
        && FamilyIsNoOp(plan.IPv6, plan.Adapter.Dns.IPv6Automatic, plan.Adapter.Dns.IPv6Servers);

    private static bool FamilyIsNoOp(FamilyChange c, bool currentAuto, IReadOnlyList<IPAddress> current) => c.Mode switch
    {
        DnsMode.Unchanged => true,
        DnsMode.Automatic => currentAuto,
        _ => !currentAuto && c.Servers.SequenceEqual(current),
    };

    public static IReadOnlyList<PreviewRow> Preview(DnsChangePlan plan)
    {
        var d = plan.Adapter.Dns;
        return
        [
            Row("IPv4 DNS", plan.IPv4, d.IPv4Automatic, d.IPv4Servers),
            Row("IPv6 DNS", plan.IPv6, d.IPv6Automatic, d.IPv6Servers),
        ];
    }

    private static PreviewRow Row(string label, FamilyChange c, bool auto, IReadOnlyList<IPAddress> current)
    {
        var before = (auto ? "Automatic" : "Manual") + " - " + (current.Count == 0 ? "no servers" : string.Join(", ", current));
        var after = c.Mode switch
        {
            DnsMode.Unchanged => "Unchanged",
            DnsMode.Automatic => "Automatic (from DHCP / router)",
            _ => "Manual - " + string.Join(", ", c.Servers),
        };
        return new PreviewRow(label, before, after, !FamilyIsNoOp(c, auto, current));
    }

    public static IReadOnlyList<string> Warnings(DnsChangePlan plan)
    {
        var w = new List<string>();
        var a = plan.Adapter;
        if (!a.IsUp)
        {
            w.Add("This adapter is not connected; the setting is stored and will be used when it connects.");
        }

        if (plan.IPv4.Mode != DnsMode.Unchanged && plan.IPv6.Mode == DnsMode.Unchanged && a.Dns.IPv6Servers.Count > 0)
        {
            w.Add($"IPv6 DNS servers ({string.Join(", ", a.Dns.IPv6Servers)}) stay active, so some lookups may still use them. Set IPv6 too if you want a single provider.");
        }

        if (plan.IPv6.Mode != DnsMode.Unchanged && plan.IPv4.Mode == DnsMode.Unchanged && a.Dns.IPv4Servers.Count > 0)
        {
            w.Add($"IPv4 DNS servers ({string.Join(", ", a.Dns.IPv4Servers)}) stay active.");
        }

        if (plan.IPv4.Mode == DnsMode.Automatic && !a.IsDhcpEnabled)
        {
            w.Add("This adapter has a static IP address (no DHCP), so 'automatic' DNS may leave it with no DNS servers.");
        }

        return w;
    }
}
