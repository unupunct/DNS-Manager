using System.Net;
using System.Net.Sockets;

namespace DNSManagerPro.Core.Diagnostics;

public static class ReverseName
{
    /// <summary>"8.8.4.4" -> "4.4.8.8.in-addr.arpa"; IPv6 uses nibble format under ip6.arpa.</summary>
    public static string For(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return string.Join('.', b.Reverse()) + ".in-addr.arpa";
        }

        var nibbles = b.SelectMany(x => new[] { (x >> 4) & 0xF, x & 0xF }).Reverse().Select(n => n.ToString("x", System.Globalization.CultureInfo.InvariantCulture));
        return string.Join('.', nibbles) + ".ip6.arpa";
    }
}

public sealed record PropagationGroup(string Answer, IReadOnlyList<string> Resolvers);

public sealed record PropagationSummary(IReadOnlyList<PropagationGroup> Groups, int Responded, int Total, bool Consistent, string Text);

public static class PropagationAnalysis
{
    /// <summary>
    /// Groups resolvers by the set of records they returned for the requested type. Different answer sets are not
    /// necessarily wrong (CDNs and geo-DNS legitimately vary), so the text says so.
    /// </summary>
    public static PropagationSummary Summarize(IReadOnlyList<LookupResult> results, LookupType type)
    {
        var responded = results.Where(r => r.Success).ToList();
        var groups = responded
            .GroupBy(r => string.Join(" | ", r.Records.Where(x => x.Type == type.ToString()).Select(x => x.Data).Order(StringComparer.Ordinal)))
            .Select(g => new PropagationGroup(g.Key.Length == 0 ? "(no records)" : g.Key, [.. g.Select(r => r.ServerName)]))
            .OrderByDescending(g => g.Resolvers.Count)
            .ToList();
        var consistent = groups.Count <= 1 && responded.Count > 0;
        var failed = results.Count - responded.Count;
        var text = responded.Count == 0
            ? "No resolver returned a usable answer."
            : consistent
                ? $"All {responded.Count} responding resolvers returned the same answer." + (failed > 0 ? $" {failed} resolver(s) did not answer or returned an error." : string.Empty)
                : $"{groups.Count} different answers from {responded.Count} resolvers. For CDN-hosted or geo-balanced domains this is normal; for a record you recently changed it suggests the change has not reached every resolver yet (old answers can be cached until their TTL expires)."
                  + (failed > 0 ? $" {failed} resolver(s) did not answer." : string.Empty);
        return new PropagationSummary(groups, responded.Count, results.Count, consistent, text);
    }
}
