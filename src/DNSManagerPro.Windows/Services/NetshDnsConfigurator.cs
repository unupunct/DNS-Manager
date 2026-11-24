using System.Globalization;
using System.Net;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Configuration;
using DNSManagerPro.Core.Models;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.Windows.Services;

/// <summary>
/// Applies DNS settings with the documented <c>netsh interface ipv4|ipv6 set|add dnsservers</c> commands.
/// Arguments are built only from an interface index (int) and parsed <see cref="IPAddress"/> values, passed as an
/// argument list (no shell), so no user-supplied text can alter the command.
/// </summary>
public sealed class NetshDnsConfigurator(IProcessRunner runner, ILogger<NetshDnsConfigurator> logger) : IDnsConfigurator
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<OperationResult> ApplyAsync(DnsChangePlan plan, CancellationToken cancellationToken = default)
    {
        if (plan.Adapter.InterfaceIndex <= 0)
        {
            return OperationResult.Fail("The adapter has no valid interface index.");
        }

        foreach (var (family, change) in new[] { ("ipv4", plan.IPv4), ("ipv6", plan.IPv6) })
        {
            foreach (var args in BuildCommands(plan.Adapter.InterfaceIndex, family, change))
            {
                ProcessResult r;
                try
                {
                    r = await runner.RunAsync(NetshPath(), args, Timeout, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
                {
                    logger.LogError(ex, "netsh failed to run");
                    return OperationResult.Fail("Could not run netsh: " + ex.Message);
                }

                if (r.ExitCode != 0)
                {
                    var elevation = r.Output.Contains("elevation", StringComparison.OrdinalIgnoreCase)
                        || r.Output.Contains("administrator", StringComparison.OrdinalIgnoreCase);
                    logger.LogWarning("netsh {Args} exit {Code}: {Output}", string.Join(' ', args), r.ExitCode, r.Output);
                    return OperationResult.Fail(
                        elevation ? "Windows refused the change: administrator rights are required." : $"netsh ({family}) failed: {r.Output}",
                        elevation);
                }
            }
        }

        return OperationResult.Ok();
    }

    private static string NetshPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

    /// <summary>Builds the exact netsh argument lists for one IP family. Public for unit testing.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> BuildCommands(int interfaceIndex, string family, FamilyChange change)
    {
        var idx = interfaceIndex.ToString(CultureInfo.InvariantCulture);
        switch (change.Mode)
        {
            case DnsMode.Unchanged:
                return [];
            case DnsMode.Automatic:
                return [["interface", family, "set", "dnsservers", $"name={idx}", "source=dhcp"]];
            default:
                var list = new List<IReadOnlyList<string>>
                {
                    new[] { "interface", family, "set", "dnsservers", $"name={idx}", "source=static", $"address={change.Servers[0]}", "validate=no" },
                };
                for (var i = 1; i < change.Servers.Count; i++)
                {
                    list.Add(["interface", family, "add", "dnsservers", $"name={idx}", $"address={change.Servers[i]}", $"index={(i + 1).ToString(CultureInfo.InvariantCulture)}", "validate=no"]);
                }

                return list;
        }
    }
}
