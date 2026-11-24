using System.Diagnostics;
using System.Globalization;
using System.Net;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Diagnostics;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.Networking;

/// <summary>Direct record lookups against a chosen server (UDP, no client cache).</summary>
public sealed class DnsLookupClient(ILogger<DnsLookupClient> logger) : IDnsLookupClient
{
    public async Task<LookupResult> LookupAsync(IPAddress server, string serverName, string domain, LookupType type, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var name = Prepare(domain, type);
        var client = new LookupClient(new LookupClientOptions(server) { UseCache = false, Retries = 0, Timeout = timeout, UseTcpFallback = true });
        var sw = Stopwatch.StartNew();
        try
        {
            var r = await client.QueryAsync(name, Map(type), cancellationToken: cancellationToken).ConfigureAwait(false);
            sw.Stop();
            var records = r.Answers.Select(a => new DnsRecordInfo(a.RecordType.ToString(), a.DomainName.Value.TrimEnd('.'), (int)a.InitialTimeToLive, Format(a))).ToList();
            return new LookupResult(domain, server, serverName, type, sw.Elapsed.TotalMilliseconds, r.Header.ResponseCode.ToString(), records, null, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DnsResponseException ex)
        {
            sw.Stop();
            logger.LogDebug(ex, "Lookup {Name} on {Server} failed", name, server);
            var timedOut = ex.Code == DnsResponseCode.ConnectionTimeout;
            return new LookupResult(domain, server, serverName, type, sw.Elapsed.TotalMilliseconds, timedOut ? "Timeout" : ex.Code.ToString(), [], ex.InnerException?.Message ?? ex.Message, timedOut);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            sw.Stop();
            return new LookupResult(domain, server, serverName, type, sw.Elapsed.TotalMilliseconds, "Timeout", [], ex.Message, true);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException)
        {
            sw.Stop();
            return new LookupResult(domain, server, serverName, type, sw.Elapsed.TotalMilliseconds, "Error", [], ex.Message, false);
        }
    }

    /// <summary>PTR lookups accept an IP address and convert it to the in-addr.arpa / ip6.arpa name; other names are converted to ASCII (IDN).</summary>
    public static string Prepare(string domain, LookupType type)
    {
        var d = domain.Trim().TrimEnd('.');
        if (type == LookupType.PTR && IPAddress.TryParse(d, out var ip))
        {
            return ReverseName.For(ip);
        }

        try
        {
            return new IdnMapping().GetAscii(d);
        }
        catch (ArgumentException)
        {
            return d;
        }
    }

    private static QueryType Map(LookupType t) => t switch
    {
        LookupType.A => QueryType.A,
        LookupType.AAAA => QueryType.AAAA,
        LookupType.MX => QueryType.MX,
        LookupType.TXT => QueryType.TXT,
        LookupType.NS => QueryType.NS,
        LookupType.CNAME => QueryType.CNAME,
        LookupType.SOA => QueryType.SOA,
        _ => QueryType.PTR,
    };

    private static string Format(DnsResourceRecord r) => r switch
    {
        AddressRecord a => a.Address.ToString(),
        MxRecord m => $"{m.Preference.ToString(CultureInfo.InvariantCulture)} {m.Exchange.Value.TrimEnd('.')}",
        TxtRecord t => string.Join(string.Empty, t.Text),
        NsRecord n => n.NSDName.Value.TrimEnd('.'),
        CNameRecord c => c.CanonicalName.Value.TrimEnd('.'),
        PtrRecord p => p.PtrDomainName.Value.TrimEnd('.'),
        SoaRecord s => $"{s.MName.Value.TrimEnd('.')} {s.RName.Value.TrimEnd('.')} serial={s.Serial} refresh={s.Refresh} retry={s.Retry} expire={s.Expire} minimum={s.Minimum}",
        _ => r.ToString() ?? string.Empty,
    };
}

