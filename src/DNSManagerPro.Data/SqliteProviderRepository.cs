using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Models;
using DNSManagerPro.Core.Providers;
using Microsoft.Data.Sqlite;

namespace DNSManagerPro.Data;

public sealed class SqliteProviderRepository(SqliteDatabase db) : IProviderRepository, IAdapterStore
{
    private const string Columns =
        "Id, Name, Description, Website, IPv4Primary, IPv4Secondary, IPv6Primary, IPv6Secondary, AdditionalInformation, Privacy, Security, Filtering, DohUrl, DotHost, DoqHost, IsBuiltIn";

    private const string UpsertSql = """
        INSERT INTO DNSProviders (Id, Name, Description, Website, IPv4Primary, IPv4Secondary, IPv6Primary, IPv6Secondary,
                                  AdditionalInformation, Privacy, Security, Filtering, DohUrl, DotHost, DoqHost, IsBuiltIn, SortOrder, UpdatedAt)
        VALUES ($id, $name, $desc, $web, $v4p, $v4s, $v6p, $v6s, $notes, $privacy, $security, $filtering, $doh, $dot, $doq, $builtin, $sort, $now)
        ON CONFLICT(Id) DO UPDATE SET
            Name=$name, Description=$desc, Website=$web, IPv4Primary=$v4p, IPv4Secondary=$v4s, IPv6Primary=$v6p, IPv6Secondary=$v6s,
            AdditionalInformation=$notes, Privacy=$privacy, Security=$security, Filtering=$filtering,
            DohUrl=$doh, DotHost=$dot, DoqHost=$doq, UpdatedAt=$now;
        """;

    /// <summary>Inserts built-ins that are missing. Existing rows (including user edits) are left alone.</summary>
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        var i = 0;
        foreach (var p in BuiltInProviders.All)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM DNSProviders WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id", p.Id);
            var exists = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) > 0;
            if (!exists)
            {
                await ExecUpsertAsync(c, p, i, cancellationToken).ConfigureAwait(false);
            }

            i++;
        }
    }

    public async Task<IReadOnlyList<DnsProvider>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM DNSProviders ORDER BY SortOrder, Name COLLATE NOCASE";
        var list = new List<DnsProvider>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new DnsProvider(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                Nullable(r, 4), Nullable(r, 5), Nullable(r, 6), Nullable(r, 7),
                r.GetString(9), r.GetString(10), r.GetString(11), r.GetString(8),
                r.GetInt32(15) != 0, Nullable(r, 12), Nullable(r, 13), Nullable(r, 14)));
        }

        return list;
    }

    public async Task UpsertAsync(DnsProvider provider, CancellationToken cancellationToken = default)
    {
        var errors = DnsValidator.ValidateProvider(provider);
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid provider: " + string.Join(" ", errors), nameof(provider));
        }

        await using var c = db.Open();
        await ExecUpsertAsync(c, provider, provider.IsBuiltIn ? BuiltInIndex(provider.Id) : 1000, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM DNSProviders WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreBuiltInsAsync(CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        var i = 0;
        foreach (var p in BuiltInProviders.All)
        {
            await ExecUpsertAsync(c, p, i++, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RecordAsync(IEnumerable<NetworkAdapterInfo> adapters, CancellationToken cancellationToken = default)
    {
        await using var c = db.Open();
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var a in adapters)
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO NetworkAdapters (Id, AdapterName, InterfaceDescription, MacAddress, LastDetectedAt)
                VALUES ($id, $name, $desc, $mac, $now)
                ON CONFLICT(Id) DO UPDATE SET AdapterName=$name, InterfaceDescription=$desc, MacAddress=$mac, LastDetectedAt=$now;
                """;
            cmd.Parameters.AddWithValue("$id", a.Id);
            cmd.Parameters.AddWithValue("$name", a.Name);
            cmd.Parameters.AddWithValue("$desc", a.Description);
            cmd.Parameters.AddWithValue("$mac", a.MacAddress);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int BuiltInIndex(string id)
    {
        for (var i = 0; i < BuiltInProviders.All.Count; i++)
        {
            if (BuiltInProviders.All[i].Id == id)
            {
                return i;
            }
        }

        return 500;
    }

    private static string? Nullable(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static async Task ExecUpsertAsync(SqliteConnection c, DnsProvider p, int sort, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = UpsertSql;
        cmd.Parameters.AddWithValue("$id", p.Id);
        cmd.Parameters.AddWithValue("$name", p.Name.Trim());
        cmd.Parameters.AddWithValue("$desc", p.Description ?? string.Empty);
        cmd.Parameters.AddWithValue("$web", p.Website ?? string.Empty);
        cmd.Parameters.AddWithValue("$v4p", (object?)Clean(p.IPv4Primary) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$v4s", (object?)Clean(p.IPv4Secondary) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$v6p", (object?)Clean(p.IPv6Primary) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$v6s", (object?)Clean(p.IPv6Secondary) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$notes", p.Notes ?? string.Empty);
        cmd.Parameters.AddWithValue("$privacy", p.Privacy ?? string.Empty);
        cmd.Parameters.AddWithValue("$security", p.Security ?? string.Empty);
        cmd.Parameters.AddWithValue("$filtering", p.Filtering ?? string.Empty);
        cmd.Parameters.AddWithValue("$doh", (object?)Clean(p.DohUrl) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dot", (object?)Clean(p.DotHost) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$doq", (object?)Clean(p.DoqHost) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$builtin", p.IsBuiltIn ? 1 : 0);
        cmd.Parameters.AddWithValue("$sort", sort);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
