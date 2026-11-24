using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Benchmark;
using DnsClient;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.Networking;

/// <summary>
/// Direct DNS queries over UDP, TCP (via DnsClient, never the OS resolver, no client-side cache) and DoH (RFC 8484
/// wire format over HTTP/2 with a reused connection). The DoH host name itself is resolved by the OS once; DoH
/// timings therefore reflect a warm TLS connection, not the handshake.
/// </summary>
public sealed class DnsQueryClient(ILogger<DnsQueryClient> logger) : IDnsQueryClient, IDisposable
{
    private readonly ConcurrentDictionary<(IPAddress, bool, TimeSpan), LookupClient> _clients = new();
    private readonly HttpClient _http = CreateHttp();
    private int _idCounter = Random.Shared.Next(1, 60000);

    public void Dispose() => _http.Dispose();

    public Task<QueryOutcome> QueryAsync(BenchmarkTarget target, string queryName, RecordKind kind, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        target.Transport == DnsTransport.Doh
            ? QueryDohAsync(target, queryName, kind, timeout, cancellationToken)
            : QueryClassicAsync(target, queryName, kind, timeout, cancellationToken);

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), EnableMultipleHttp2Connections = true })
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        return h;
    }

    private async Task<QueryOutcome> QueryClassicAsync(BenchmarkTarget t, string name, RecordKind kind, TimeSpan timeout, CancellationToken ct)
    {
        if (t.Address is null)
        {
            return new QueryOutcome(false, 0, "Error", false, "No server address.");
        }

        var tcp = t.Transport == DnsTransport.Tcp;
        var client = _clients.GetOrAdd((t.Address, tcp, timeout), key =>
            new LookupClient(new LookupClientOptions(key.Item1)
            {
                UseCache = false,
                Retries = 0,
                Timeout = key.Item3,
                UseTcpFallback = false,
                UseTcpOnly = key.Item2,
            }));
        var sw = Stopwatch.StartNew();
        try
        {
            var r = await client.QueryAsync(name, kind == RecordKind.A ? QueryType.A : QueryType.AAAA, cancellationToken: ct).ConfigureAwait(false);
            sw.Stop();
            return new QueryOutcome(true, sw.Elapsed.TotalMilliseconds, r.Header.ResponseCode.ToString(), false, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DnsResponseException ex)
        {
            sw.Stop();
            var timedOut = ex.Code == DnsResponseCode.ConnectionTimeout;
            logger.LogDebug(ex, "Query {Server} {Name} failed", t.Address, name);
            if (ex.InnerException is System.Net.Sockets.SocketException se)
            {
                return new QueryOutcome(false, sw.Elapsed.TotalMilliseconds, $"Socket:{se.SocketErrorCode}", false, se.Message);
            }

            return new QueryOutcome(false, sw.Elapsed.TotalMilliseconds, timedOut ? "Timeout" : ex.Code.ToString(), timedOut, ex.Message);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            sw.Stop();
            return new QueryOutcome(false, sw.Elapsed.TotalMilliseconds, "Timeout", true, ex.Message);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or InvalidOperationException)
        {
            sw.Stop();
            return new QueryOutcome(false, sw.Elapsed.TotalMilliseconds, "Error", false, ex.Message);
        }
    }

    private async Task<QueryOutcome> QueryDohAsync(BenchmarkTarget t, string name, RecordKind kind, TimeSpan timeout, CancellationToken ct)
    {
        if (!Uri.TryCreate(t.DohUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return new QueryOutcome(false, 0, "Error", false, "Invalid DoH URL.");
        }

        var id = (ushort)(Interlocked.Increment(ref _idCounter) & 0xFFFF);
        var body = DnsWire.BuildQuery(name, kind == RecordKind.A ? DnsWire.TypeA : DnsWire.TypeAaaa, id);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            using var response = await _http.PostAsync(uri, content, cts.Token).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            sw.Stop();
            if (!response.IsSuccessStatusCode)
            {
                return new QueryOutcome(false, sw.Elapsed.TotalMilliseconds, $"HTTP{(int)response.StatusCode}", false, response.ReasonPhrase);
            }

            return DnsWire.ParseRcode(bytes) is { } rc
                ? new QueryOutcome(true, sw.Elapsed.TotalMilliseconds, DnsWire.RcodeName(rc), false, null)
                : new QueryOutcome(false, sw.Elapsed.TotalMilliseconds, "BadResponse", false, "Not a DNS response.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new QueryOutcome(false, sw.Elapsed.TotalMilliseconds, "Timeout", true, "Timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            sw.Stop();
            return new QueryOutcome(false, sw.Elapsed.TotalMilliseconds, "Error", false, ex.Message);
        }
    }
}

