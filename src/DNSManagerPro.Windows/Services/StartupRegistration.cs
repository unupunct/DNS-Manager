using DNSManagerPro.Core.Abstractions;
using Microsoft.Win32;

namespace DNSManagerPro.Windows.Services;

/// <summary>Per-user "start with Windows" entry in HKCU\...\Run. No administrator rights and no machine-wide change.</summary>
public sealed class StartupRegistration : IStartupRegistration
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "DNSManagerPro";
    public const string MinimizedArgument = "--minimized";

    /// <summary>Builds the Run-key command: the quoted executable path plus the optional minimized flag. Rejects paths that could break out of the quoting.</summary>
    public static string BuildCommand(string exePath, bool minimized)
    {
        if (string.IsNullOrWhiteSpace(exePath) || exePath.Contains('"', StringComparison.Ordinal) || exePath.Any(char.IsControl))
        {
            throw new ArgumentException("Executable path is not valid for a startup entry.", nameof(exePath));
        }

        return minimized ? $"\"{exePath}\" {MinimizedArgument}" : $"\"{exePath}\"";
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string s && s.Length > 0;
    }

    public void Configure(bool enabled, bool minimized)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the application path.");
        key.SetValue(ValueName, BuildCommand(exe, minimized), RegistryValueKind.String);
    }
}
