using System.Diagnostics;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Models;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.Windows.Services;

/// <summary>Runs a fixed, argument-free system command. No user input ever reaches the process arguments.</summary>
public sealed class WindowsDnsCacheService(ILogger<WindowsDnsCacheService> logger) : IDnsCacheService
{
    public async Task<OperationResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var psi = new ProcessStartInfo(Path.Combine(system32, "ipconfig.exe"), "/flushdns")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("ipconfig did not start.");
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                logger.LogInformation("DNS resolver cache flushed");
                return OperationResult.Ok("DNS resolver cache flushed.");
            }

            var needsAdmin = stdout.Contains("Function failed", StringComparison.OrdinalIgnoreCase)
                || stdout.Contains("administrator", StringComparison.OrdinalIgnoreCase);
            logger.LogWarning("ipconfig /flushdns exit {Code}: {Output}", process.ExitCode, stdout.Trim());
            return OperationResult.Fail(
                needsAdmin
                    ? "Flushing the DNS cache requires administrator rights. Restart DNS Manager Pro as administrator."
                    : "Flush failed: " + stdout.Trim(),
                needsAdmin);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogError(ex, "Could not run ipconfig");
            return OperationResult.Fail("Could not run ipconfig: " + ex.Message);
        }
    }
}
