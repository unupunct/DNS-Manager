using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace DNSManagerPro.App.Services;

public interface IElevationService
{
    /// <summary>Relaunches the app with the Windows UAC administrator prompt, then exits this instance. False if the user declined.</summary>
    bool RestartAsAdministrator(string? arguments);
}

public sealed class ElevationService : IElevationService
{
    private const int ErrorCancelled = 1223;

    public bool RestartAsAdministrator(string? arguments)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", Arguments = arguments ?? string.Empty });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false; // user declined the UAC prompt
        }

        Application.Current.Shutdown();
        return true;
    }
}
