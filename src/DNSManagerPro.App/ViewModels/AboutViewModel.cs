using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DNSManagerPro.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.App.ViewModels;

public sealed record LicenseItem(string Name, string Purpose, string License);

public sealed partial class AboutViewModel(ILogger<AboutViewModel> log) : ObservableObject
{
    public const string ProjectUrl = "https://github.com/unupunct/DNS-Manager";

    public string Version { get; } = ReadVersion();

    public string Runtime { get; } = RuntimeInformation.FrameworkDescription;

    public string Copyright { get; } = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;

    public string DataFolder { get; } = @"%LocalAppData%\" + Path.GetFileName(AppPaths.Root);

    public IReadOnlyList<LicenseItem> Licenses { get; } =
    [
        new(".NET 10 / WPF", "Runtime and UI framework", "MIT"),
        new("CommunityToolkit.Mvvm", "MVVM source generators", "MIT"),
        new("Microsoft.Extensions.* (DI, Hosting, Logging)", "Dependency injection and logging", "MIT"),
        new("Microsoft.Data.Sqlite", "Local database", "MIT"),
        new("SQLitePCLRaw (bundled SQLite)", "SQLite engine", "Apache-2.0 / public domain"),
        new("DnsClient", "Direct DNS queries for benchmark and diagnostics", "Apache-2.0"),
        new("System.Management", "Reading network adapter details", "MIT"),
    ];

    public IReadOnlyList<string> Principles { get; } =
    [
        "Local only: no account, no telemetry, no cloud service.",
        "Starts as a normal user; asks for administrator rights only when you apply a change.",
        "Every change shows a preview, is backed up first, verified, and rolled back on failure.",
        "No arbitrary shell commands; external tools run with fixed paths and validated arguments.",
    ];

    [ObservableProperty]
    private string? _message;

    [RelayCommand]
    private void OpenProject()
    {
        try
        {
            // The URL is a compile-time constant; the default browser opens it.
            Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            log.LogWarning(ex, "Could not open project page");
            Message = "Could not open the browser. The address is " + ProjectUrl;
        }
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            System.Windows.Clipboard.SetText($"DNS Manager Pro {Version}\n{Runtime}\n{Environment.OSVersion.VersionString}");
            Message = "Version details copied.";
        }
        catch (ExternalException ex)
        {
            log.LogWarning(ex, "Clipboard unavailable");
            Message = "The clipboard is in use by another program. Try again.";
        }
    }

    private static string ReadVersion()
    {
        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        }

        var plus = info.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? info[..plus] : info;
    }
}
