using System.Diagnostics;
using System.Net;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Models;
using DnsClient;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.Networking;

/// <summary>Direct DNS-over-UDP query to one server, bypassing the OS resolver and DnsClient's cache.</summary>
public sealed class UdpDnsProbe(ILogger<UdpDnsProbe> logger) : IDnsProbe
{
    public async Task<DnsProbeResult> ProbeAsync(
        IPAddress server,
        string domain,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var options = new LookupClientOptions(server)
        {
            UseCache = false,
            Retries = 0,
            Timeout = timeout,
            UseTcpFallback = false,
        };
        var client = new LookupClient(options);
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await client.QueryAsync(domain, QueryType.A, cancellationToken: cancellationToken).ConfigureAwait(false);
            sw.Stop();
            var ok = !response.HasError;
            return new DnsProbeResult(server, domain, ok, sw.Elapsed.TotalMilliseconds, response.Header.ResponseCode.ToString(), ok ? null : response.ErrorMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DnsResponseException ex)
        {
            sw.Stop();
            logger.LogDebug(ex, "Probe {Server} {Domain} failed", server, domain);
            return new DnsProbeResult(server, domain, false, sw.Elapsed.TotalMilliseconds, ex.Code.ToString(), ex.Message);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            sw.Stop();
            logger.LogDebug(ex, "Probe {Server} {Domain} timed out", server, domain);
            return new DnsProbeResult(server, domain, false, sw.Elapsed.TotalMilliseconds, "Timeout", ex.Message);
        }
    }
}
