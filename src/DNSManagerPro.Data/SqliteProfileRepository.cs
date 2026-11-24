using System.Globalization;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Profiles;
using Microsoft.Data.Sqlite;

namespace DNSManagerPro.Data;

public sealed class SqliteProfileRepository(SqliteDatabase db) : IProfileRepository
{
    private const string Cols =
        "Id, Name, Description, Category, ProviderId, IsAutomatic, IPv4Primary, IPv4Secondary, IPv6Primary, IPv6Secondary, DohUrl, DotHost, DoqHost, AdapterId, AdapterName, Tags, Notes, CreatedAt, UpdatedAt, LastAppliedAt";

    public async Task<IReadOnlyList<DnsProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM DNSProfiles ORDER BY Name COLLATE NOCASE";
        var list = new List<DnsProfile>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new DnsProfile(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), N(r, 4), r.GetInt32(5) != 0,
                N(r, 6), N(r, 7), N(r, 8), N(r, 9), N(r, 10), N(r, 11), N(r, 12), N(r, 13), N(r, 14),
                r.GetString(15), r.GetString(16), Parse(r.GetString(17)), Parse(r.GetString(18)),
                r.IsDBNull(19) ? null : Parse(r.GetString(19))));
        }

        return list;
    }

    public async Task UpsertAsync(DnsProfile p, CancellationToken cancellationToken = default)
    {
        var errors = p.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid profile: " + string.Join(" ", errors), nameof(p));
        }

        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DNSProfiles (Id, Name, Description, Category, ProviderId, IsAutomatic, IPv4Primary, IPv4Secondary, IPv6Primary, IPv6Secondary,
                                     DohUrl, DotHost, DoqHost, AdapterId, AdapterName, Tags, Notes, CreatedAt, UpdatedAt, LastAppliedAt)
            VALUES ($id,$name,$desc,$cat,$prov,$auto,$v4p,$v4s,$v6p,$v6s,$doh,$dot,$doq,$aid,$an,$tags,$notes,$created,$updated,$applied)
            ON CONFLICT(Id) DO UPDATE SET
                Name=$name, Description=$desc, Category=$cat, ProviderId=$prov, IsAutomatic=$auto,
                IPv4Primary=$v4p, IPv4Secondary=$v4s, IPv6Primary=$v6p, IPv6Secondary=$v6s,
                DohUrl=$doh, DotHost=$dot, DoqHost=$doq, AdapterId=$aid, AdapterName=$an, Tags=$tags, Notes=$notes, UpdatedAt=$updated;
            """;
        void P(string n, string? v) => cmd.Parameters.AddWithValue(n, (object?)v ?? DBNull.Value);
        P("$id", p.Id);
        P("$name", p.Name.Trim());
        P("$desc", p.Description);
        P("$cat", p.Category);
        P("$prov", p.ProviderId);
        cmd.Parameters.AddWithValue("$auto", p.IsAutomatic ? 1 : 0);
        P("$v4p", p.IsAutomatic ? null : p.IPv4Primary);
        P("$v4s", p.IsAutomatic ? null : p.IPv4Secondary);
        P("$v6p", p.IsAutomatic ? null : p.IPv6Primary);
        P("$v6s", p.IsAutomatic ? null : p.IPv6Secondary);
        P("$doh", p.DohUrl);
        P("$dot", p.DotHost);
        P("$doq", p.DoqHost);
        P("$aid", p.AdapterId);
        P("$an", p.AdapterName);
        P("$tags", p.Tags);
        P("$notes", p.Notes);
        P("$created", Iso(p.CreatedUtc));
        P("$updated", Iso(DateTime.UtcNow));
        P("$applied", p.LastAppliedUtc is null ? null : Iso(p.LastAppliedUtc.Value));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM DNSProfiles WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkAppliedAsync(string id, DateTime utc, CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE DNSProfiles SET LastAppliedAt=$t WHERE Id=$id";
        cmd.Parameters.AddWithValue("$t", Iso(utc));
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static string Iso(DateTime d) => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime Parse(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
