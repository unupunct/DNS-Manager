namespace DNSManagerPro.Infrastructure;

/// <summary>Per-user data locations. Everything stays local under %LocalAppData%.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DNSManagerPro");

    public static string Logs => Path.Combine(Root, "logs");

    public static string Database => Path.Combine(Root, "dnsmanagerpro.db");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }
}
