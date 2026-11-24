using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Profiles;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.App.Services;

public sealed record ImportReport(int Added, IReadOnlyList<string> Errors);

/// <summary>Single access point for saved profiles. Raises <see cref="Changed"/> so the Profiles and Custom DNS pages stay in sync.</summary>
public sealed class ProfileService(IProfileRepository repo, ILogger<ProfileService> logger)
{
    public event Action? Changed;

    /// <summary>Set by the Profiles page; consumed by the Custom DNS editor when it is shown.</summary>
    public string? PendingEditId { get; set; }

    public bool StartNew { get; set; }

    public Task<IReadOnlyList<DnsProfile>> GetAllAsync() => repo.GetAllAsync();

    public async Task SaveAsync(DnsProfile profile)
    {
        await repo.UpsertAsync(profile);
        Changed?.Invoke();
    }

    public async Task DeleteAsync(string id)
    {
        await repo.DeleteAsync(id);
        Changed?.Invoke();
    }

    public async Task MarkAppliedAsync(string id)
    {
        try
        {
            await repo.MarkAppliedAsync(id, DateTime.UtcNow);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record last-applied time");
        }
    }

    public async Task<DnsProfile> DuplicateAsync(DnsProfile source)
    {
        var existing = (await repo.GetAllAsync()).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copy = source with
        {
            Id = DnsProfile.New("x").Id,
            Name = UniqueName(source.Name + " (copy)", existing),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            LastAppliedUtc = null,
        };
        await SaveAsync(copy);
        return copy;
    }

    public async Task<ImportReport> ImportAsync(string json)
    {
        var parsed = ProfileExchange.Import(json);
        var names = (await repo.GetAllAsync()).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var p in parsed.Profiles)
        {
            var unique = UniqueName(p.Name, names);
            names.Add(unique);
            await repo.UpsertAsync(p with { Name = unique });
            added++;
        }

        if (added > 0)
        {
            Changed?.Invoke();
        }

        return new ImportReport(added, parsed.Errors);
    }

    public static string UniqueName(string wanted, ISet<string> existing)
    {
        if (!existing.Contains(wanted))
        {
            return wanted;
        }

        var baseName = wanted.Length > 52 ? wanted[..52] : wanted;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
