using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Diagnostics;

namespace DNSManagerPro.Networking;

public sealed class IcmpPingService : IPingService
{
    public async Task<PingResult> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(address, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new PingResult(reply.Status == IPStatus.Success, reply.RoundtripTime, reply.Status.ToString());
        }
        catch (PingException ex)
        {
            return new PingResult(false, 0, ex.InnerException?.Message ?? ex.Message);
        }
    }
}

public sealed class SystemResolver : IOsResolver
{
    public async Task<OsResolution> ResolveAsync(string host, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return new OsResolution(host, addrs.Length > 0, sw.Elapsed.TotalMilliseconds, [.. addrs.Select(a => a.ToString())], addrs.Length > 0 ? null : "No addresses returned.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new OsResolution(host, false, sw.Elapsed.TotalMilliseconds, [], "Timed out.");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return new OsResolution(host, false, sw.Elapsed.TotalMilliseconds, [], ex.Message);
        }
    }
}
