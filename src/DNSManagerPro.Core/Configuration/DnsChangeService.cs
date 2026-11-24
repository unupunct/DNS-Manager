using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Models;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.Core.Configuration;

/// <summary>
/// Orchestrates a safe DNS change: validate, require elevation, back up, apply, verify the configuration,
/// verify that the new server actually answers, and roll back automatically if anything fails.
/// </summary>
public sealed class DnsChangeService(
    IDnsConfigurator configurator,
    INetworkAdapterService adapters,
    IDnsHistoryRepository history,
    IPrivilegeService privilege,
    IDnsProbe probe,
    IDnsCacheService cache,
    ILogger<DnsChangeService> logger)
{
    public const string ConnectivityTestDomain = "cloudflare.com";

    /// <summary>Delay between verification attempts. Public so tests can shorten it.</summary>
    public TimeSpan VerifyDelay { get; set; } = TimeSpan.FromMilliseconds(700);

    public async Task<ApplyOutcome> ApplyAsync(DnsChangePlan plan, string operation = "Apply", IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var steps = new List<string>();
        void Step(string s)
        {
            steps.Add(s);
            progress?.Report(s);
            logger.LogInformation("DNS change [{Adapter}]: {Step}", plan.Adapter.Name, s);
        }

        var errors = DnsChangePlanner.Validate(plan);
        if (errors.Count > 0)
        {
            return new ApplyOutcome(false, string.Join(" ", errors), steps);
        }

        if (DnsChangePlanner.IsNoOp(plan))
        {
            return new ApplyOutcome(true, "The adapter already has these settings. Nothing to change.", steps);
        }

        if (!privilege.IsElevated)
        {
            return new ApplyOutcome(false, "Changing DNS settings requires administrator rights.", steps, RequiresElevation: true);
        }

        // Read the adapter fresh: the backup must reflect what is on the system right now, not what the UI showed earlier.
        var fresh = (await adapters.GetAdaptersAsync(ct)).FirstOrDefault(a => a.Id == plan.Adapter.Id);
        if (fresh is null)
        {
            return new ApplyOutcome(false, $"Adapter '{plan.Adapter.Name}' is no longer present.", steps);
        }

        var snapshot = DnsSnapshot.From(fresh);
        var effective = plan with { Adapter = fresh };
        var historyId = await history.BeginAsync(fresh.Id, fresh.Name, snapshot, Describe(effective), operation, ct);
        Step($"Saved backup of previous settings ({snapshot.Describe()})");

        OperationResult applied;
        try
        {
            Step("Applying new settings...");
            applied = await configurator.ApplyAsync(effective, ct);
        }
        catch (OperationCanceledException)
        {
            return await RollbackAsync(fresh, snapshot, historyId, "Cancelled", steps, progress, ct: CancellationToken.None);
        }

        if (!applied.Success)
        {
            return await RollbackAsync(fresh, snapshot, historyId, applied.Message, steps, progress, applied.RequiresElevation, CancellationToken.None);
        }

        Step("Verifying configuration...");
        var verifyError = await VerifyAsync(effective, ct);
        if (verifyError is not null)
        {
            return await RollbackAsync(fresh, snapshot, historyId, verifyError, steps, progress, ct: CancellationToken.None);
        }

        Step("Configuration verified.");

        var target = FirstManualServer(effective);
        if (target is not null)
        {
            Step($"Testing that {target} answers DNS queries...");
            var ok = false;
            for (var i = 0; i < 2 && !ok; i++)
            {
                var r = await probe.ProbeAsync(target, ConnectivityTestDomain, TimeSpan.FromSeconds(2), ct);
                ok = r.Success;
            }

            if (!ok)
            {
                return await RollbackAsync(fresh, snapshot, historyId, $"The new DNS server {target} did not answer queries.", steps, progress, ct: CancellationToken.None);
            }

            Step("DNS server answered.");
        }

        var flush = await cache.FlushAsync(ct);
        Step(flush.Success ? "DNS cache flushed." : "DNS cache not flushed: " + flush.Message);

        await history.CompleteAsync(historyId, "Success", null, CancellationToken.None);
        return new ApplyOutcome(true, "DNS settings applied and verified.", steps);
    }

    /// <summary>Restores a stored snapshot (both IP families), going through the same safety pipeline.</summary>
    public async Task<ApplyOutcome> RestoreAsync(DnsSnapshot snapshot, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var current = (await adapters.GetAdaptersAsync(ct)).FirstOrDefault(a => a.Id == snapshot.AdapterId);
        if (current is null)
        {
            return new ApplyOutcome(false, $"Adapter '{snapshot.AdapterName}' is no longer present.", []);
        }

        return await ApplyAsync(snapshot.ToPlan(current, "Restore previous settings"), "Restore", progress, ct);
    }

    private async Task<ApplyOutcome> RollbackAsync(
        NetworkAdapterInfo adapter, DnsSnapshot snapshot, long historyId, string reason,
        List<string> steps, IProgress<string>? progress, bool elevation = false, CancellationToken ct = default)
    {
        void Step(string s)
        {
            steps.Add(s);
            progress?.Report(s);
            logger.LogWarning("DNS change [{Adapter}]: {Step}", adapter.Name, s);
        }

        Step($"Failed: {reason}");
        Step("Rolling back to previous settings...");
        var back = snapshot.ToPlan(adapter, "Automatic rollback");
        var r = await configurator.ApplyAsync(back, ct);
        if (!r.Success)
        {
            var msg = $"{reason} ROLLBACK ALSO FAILED ({r.Message}). Your previous settings were: {snapshot.Describe()}";
            await history.CompleteAsync(historyId, "Failed", msg, ct);
            return new ApplyOutcome(false, msg, steps, RolledBack: false, RequiresElevation: elevation);
        }

        var err = await VerifyAsync(back, ct);
        Step(err is null ? "Previous settings restored." : "Previous settings re-applied, but verification said: " + err);
        await history.CompleteAsync(historyId, "RolledBack", reason, ct);
        return new ApplyOutcome(false, $"{reason} Your previous DNS settings were restored.", steps, RolledBack: true, RequiresElevation: elevation);
    }

    private async Task<string?> VerifyAsync(DnsChangePlan plan, CancellationToken ct)
    {
        string? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(VerifyDelay, ct);
            }

            var now = (await adapters.GetAdaptersAsync(ct)).FirstOrDefault(a => a.Id == plan.Adapter.Id);
            if (now is null)
            {
                last = "Adapter disappeared while verifying.";
                continue;
            }

            last = Mismatch("IPv4", plan.IPv4, now.Dns.IPv4Automatic, now.Dns.IPv4Servers)
                ?? Mismatch("IPv6", plan.IPv6, now.Dns.IPv6Automatic, now.Dns.IPv6Servers);
            if (last is null)
            {
                return null;
            }
        }

        return last;
    }

    private static string? Mismatch(string name, FamilyChange c, bool auto, IReadOnlyList<System.Net.IPAddress> servers) => c.Mode switch
    {
        DnsMode.Unchanged => null,
        DnsMode.Automatic => auto ? null : $"{name} DNS is still manual after the change.",
        _ => !auto && c.Servers.SequenceEqual(servers)
            ? null
            : $"{name} DNS reads back as {(auto ? "automatic" : string.Join(", ", servers))}, expected {string.Join(", ", c.Servers)}.",
    };

    private static System.Net.IPAddress? FirstManualServer(DnsChangePlan p) =>
        p.IPv4.Mode == DnsMode.Manual && p.IPv4.Servers.Count > 0 ? p.IPv4.Servers[0]
        : p.IPv6.Mode == DnsMode.Manual && p.IPv6.Servers.Count > 0 ? p.IPv6.Servers[0]
        : null;

    private static string Describe(DnsChangePlan p) =>
        $"IPv4: {Fam(p.IPv4)}; IPv6: {Fam(p.IPv6)} ({p.Source})";

    private static string Fam(FamilyChange c) => c.Mode switch
    {
        DnsMode.Unchanged => "unchanged",
        DnsMode.Automatic => "automatic",
        _ => string.Join(", ", c.Servers),
    };
}

