using System.Net;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Models;
using DNSManagerPro.Core.Providers;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.Windows.Services;

/// <summary>
/// Windows 11's documented encrypted-DNS support: <c>netsh dns add encryption server=IP dohtemplate=URL autoupgrade=yes udpfallback=yes|no</c>.
/// It tells Windows to upgrade queries to that server IP to DoH. It only has an effect for server IPs that are also
/// configured as the adapter's DNS servers. DoT and DoQ have no equivalent Windows setting, so they are not configured.
/// </summary>
public sealed class NetshEncryptedDnsService(IProcessRunner runner, ILogger<NetshEncryptedDnsService> logger) : IEncryptedDnsService
{
    private const int Windows11MinBuild = 22000;

    public bool IsSupported => Environment.OSVersion.Version.Build >= Windows11MinBuild;

    public async Task<OperationResult> EnableDohAsync(IReadOnlyList<IPAddress> servers, string dohTemplate, bool allowUdpFallback, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return OperationResult.Fail("Encrypted DNS (DoH) configuration requires Windows 11 or later.");
        }

        var url = DnsValidator.ValidateHttpsUrl(dohTemplate);
        if (string.IsNullOrWhiteSpace(dohTemplate) || !url.IsValid)
        {
            return OperationResult.Fail(url.Error ?? "A DoH URL is required.");
        }

        var netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
        foreach (var args in BuildCommands(servers, dohTemplate.Trim(), allowUdpFallback))
        {
            try
            {
                var r = await runner.RunAsync(netsh, args, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                if (r.ExitCode != 0)
                {
                    logger.LogWarning("netsh {Args} exit {Code}: {Output}", string.Join(' ', args), r.ExitCode, r.Output);
                    return OperationResult.Fail($"Windows could not register DoH for {args[3]}: {r.Output}");
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return OperationResult.Fail("Could not run netsh: " + ex.Message);
            }
        }

        return OperationResult.Ok();
    }

    public static IReadOnlyList<IReadOnlyList<string>> BuildCommands(IReadOnlyList<IPAddress> servers, string dohTemplate, bool allowUdpFallback) =>
        [.. servers.Select(s => (IReadOnlyList<string>)new[]
        {
            "dns", "add", "encryption", $"server={s}", $"dohtemplate={dohTemplate}", "autoupgrade=yes",
            $"udpfallback={(allowUdpFallback ? "yes" : "no")}",
        })];
}
