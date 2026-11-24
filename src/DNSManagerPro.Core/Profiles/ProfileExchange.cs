using System.Text.Json;

namespace DNSManagerPro.Core.Profiles;

public sealed record ImportResult(IReadOnlyList<DnsProfile> Profiles, IReadOnlyList<string> Errors);

/// <summary>Versioned JSON export/import. Imports are treated as untrusted input: bounded, validated, and given fresh ids.</summary>
public static class ProfileExchange
{
    public const string Format = "dnsmanagerpro-profiles";
    public const int Version = 1;
    public const int MaxBytes = 1_000_000;
    public const int MaxProfiles = 500;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private sealed record FileDto(string? Format, int Version, List<ProfileDto>? Profiles);

    private sealed record ProfileDto(
        string? Name, string? Description, string? Category, string? ProviderId, bool IsAutomatic,
        string? IPv4Primary, string? IPv4Secondary, string? IPv6Primary, string? IPv6Secondary,
        string? DohUrl, string? DotHost, string? DoqHost, string? Tags, string? Notes);

    /// <summary>Machine-specific data (adapter association, timestamps, ids) is deliberately not exported.</summary>
    public static string Export(IEnumerable<DnsProfile> profiles) => JsonSerializer.Serialize(
        new FileDto(Format, Version, [.. profiles.Select(p => new ProfileDto(
            p.Name, p.Description, p.Category, p.ProviderId, p.IsAutomatic, p.IPv4Primary, p.IPv4Secondary, p.IPv6Primary, p.IPv6Secondary,
            p.DohUrl, p.DotHost, p.DoqHost, p.Tags, p.Notes))]),
        Json);

    public static ImportResult Import(string json)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes)
        {
            return new ImportResult([], ["The file is empty or larger than 1 MB."]);
        }

        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(json, Json);
        }
        catch (JsonException ex)
        {
            return new ImportResult([], ["The file is not valid JSON: " + ex.Message]);
        }

        if (file is null || file.Format != Format)
        {
            return new ImportResult([], ["This is not a DNS Manager Pro profile file."]);
        }

        if (file.Version != Version)
        {
            return new ImportResult([], [$"Unsupported file version {file.Version} (this app reads version {Version})."]);
        }

        var list = file.Profiles ?? [];
        if (list.Count > MaxProfiles)
        {
            return new ImportResult([], [$"Too many profiles (maximum {MaxProfiles})."]);
        }

        var result = new List<DnsProfile>();
        var n = 0;
        foreach (var d in list)
        {
            n++;
            var now = DateTime.UtcNow;
            var p = DnsProfile.New((d.Name ?? string.Empty).Trim(), string.IsNullOrWhiteSpace(d.Category) ? "Custom" : d.Category!) with
            {
                Description = d.Description ?? string.Empty,
                ProviderId = null, // provider ids are local to each installation
                IsAutomatic = d.IsAutomatic,
                IPv4Primary = Clean(d.IPv4Primary),
                IPv4Secondary = Clean(d.IPv4Secondary),
                IPv6Primary = Clean(d.IPv6Primary),
                IPv6Secondary = Clean(d.IPv6Secondary),
                DohUrl = Clean(d.DohUrl),
                DotHost = Clean(d.DotHost),
                DoqHost = Clean(d.DoqHost),
                Tags = d.Tags ?? string.Empty,
                Notes = d.Notes ?? string.Empty,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            var problems = p.Validate();
            if (problems.Count > 0)
            {
                errors.Add($"Profile {n} ('{(string.IsNullOrWhiteSpace(p.Name) ? "unnamed" : p.Name)}') skipped: {string.Join(" ", problems)}");
            }
            else
            {
                result.Add(p);
            }
        }

        return new ImportResult(result, errors);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
