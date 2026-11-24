using System.Text.Json;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.Windows.Services;

/// <summary>Reads the Windows DNS client cache via a fixed PowerShell command (no user input is ever included).</summary>
public sealed class PowerShellDnsCacheInspector(IProcessRunner runner, IPrivilegeService privilege, ILogger<PowerShellDnsCacheInspector> logger) : IDnsCacheInspector
{
    private const string Script = "Get-DnsClientCache | Select-Object Entry,Name,Type,Status,TimeToLive,Data | ConvertTo-Json -Compress";
    private const int MaxEntries = 5000;

    /// <exception cref="UnauthorizedAccessException">Windows only exposes the DNS cache to administrators; without them Get-DnsClientCache returns nothing, which would look like an empty cache.</exception>
    public async Task<IReadOnlyList<DnsCacheEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        if (!privilege.IsElevated)
        {
            throw new UnauthorizedAccessException("Reading the Windows DNS cache requires administrator rights.");
        }

        var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var r = await runner.RunAsync(ps, ["-NoProfile", "-NonInteractive", "-Command", Script], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            logger.LogWarning("Get-DnsClientCache exit {Code}: {Output}", r.ExitCode, r.Output);
            throw new InvalidOperationException("Could not read the DNS cache: " + r.Output);
        }

        return Parse(r.Output);
    }

    /// <summary>Public for unit tests. Accepts an array, a single object, or empty output.</summary>
    public static IReadOnlyList<DnsCacheEntry> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        var list = new List<DnsCacheEntry>();
        foreach (var e in items.Take(MaxEntries))
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = Str(e, "Entry") ?? Str(e, "Name") ?? string.Empty;
            list.Add(new DnsCacheEntry(name, TypeName(Int(e, "Type")), Str(e, "Data") ?? string.Empty, Int(e, "TimeToLive"), StatusName(Int(e, "Status"))));
        }

        return list;
    }

    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    internal static string TypeName(int t) => t switch
    {
        1 => "A",
        2 => "NS",
        5 => "CNAME",
        6 => "SOA",
        12 => "PTR",
        15 => "MX",
        16 => "TXT",
        28 => "AAAA",
        33 => "SRV",
        _ => $"TYPE{t}",
    };

    internal static string StatusName(int s) => s switch
    {
        0 => "Success",
        9003 => "Name does not exist",
        9501 => "No records",
        _ => $"Code {s}",
    };
}

