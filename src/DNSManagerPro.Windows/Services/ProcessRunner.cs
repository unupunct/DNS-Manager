using System.Diagnostics;
using System.Text;

namespace DNSManagerPro.Windows.Services;

public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>Runs an executable with an explicit argument list (never through a shell).</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{fileName} did not start.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var sb = new StringBuilder();
        var outTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var errTask = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            sb.Append(await outTask.ConfigureAwait(false)).Append(await errTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            throw;
        }

        return new ProcessResult(process.ExitCode, sb.ToString().Trim());
    }
}
