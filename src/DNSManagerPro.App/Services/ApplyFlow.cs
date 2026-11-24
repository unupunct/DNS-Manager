using DNSManagerPro.App.Dialogs;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Core.Configuration;
using DNSManagerPro.Core.Profiles;
using DNSManagerPro.Core.Providers;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.App.Services;

/// <summary>Entry points that open the preview/confirm dialog for every way of changing DNS.</summary>
public interface IApplyFlow
{
    Task<bool> ApplyProviderAsync(DnsProvider provider, string? adapterId = null);

    Task<bool> ApplyProfileAsync(DnsProfile profile);

    Task RestoreAutomaticAsync(string? adapterId = null);

    Task RestorePreviousAsync(string adapterId);

    Task<bool> HasPreviousAsync(string adapterId);

    /// <summary>Handles command-line intents passed when the app was relaunched as administrator.</summary>
    Task HandleStartupArgsAsync(string[] args);
}

public sealed class ApplyFlow(
    SystemStateService state,
    DnsChangeService service,
    IElevationService elevation,
    IPrivilegeService privilege,
    IEncryptedDnsService encrypted,
    IDialogService dialogs,
    IDnsHistoryRepository history,
    IProviderRepository providers,
    ProfileService profiles,
    INavigationService nav,
    ILogger<ApplyFlow> logger) : IApplyFlow
{
    public const string ArgProvider = "--apply-provider";
    public const string ArgProfile = "--apply-profile";
    public const string ArgAutomatic = "--restore-automatic";
    public const string ArgPrevious = "--restore-previous";

    public async Task<bool> ApplyProviderAsync(DnsProvider provider, string? adapterId = null)
    {
        if (!provider.HasAddresses)
        {
            dialogs.Notify("No server addresses", $"{provider.Name} has no DNS server addresses to apply. Edit it and add them first.");
            return false;
        }

        await RefreshAdaptersAsync();
        var restart = provider.IsBuiltIn || provider.Id.StartsWith("custom-", StringComparison.Ordinal) ? $"{ArgProvider} {provider.Id}" : string.Empty;
        return await ShowAsync(new ApplyDnsRequest($"Apply {provider.Name}", provider.Name, provider, null, restart), adapterId);
    }

    public async Task<bool> ApplyProfileAsync(DnsProfile profile)
    {
        await RefreshAdaptersAsync();
        var restart = $"{ArgProfile} {profile.Id}";
        var ok = profile.IsAutomatic
            ? await ShowAsync(new ApplyDnsRequest($"Apply profile: {profile.Name}", profile.Name, null, null, restart), profile.AdapterId)
            : await ShowAsync(new ApplyDnsRequest($"Apply profile: {profile.Name}", profile.Name, profile.ToProvider(), null, restart), profile.AdapterId);
        if (ok)
        {
            await profiles.MarkAppliedAsync(profile.Id);
        }

        return ok;
    }

    public async Task RestoreAutomaticAsync(string? adapterId = null)
    {
        await RefreshAdaptersAsync();
        await ShowAsync(new ApplyDnsRequest("Restore automatic DNS", "Automatic", null, null, ArgAutomatic), adapterId);
    }

    public async Task<bool> HasPreviousAsync(string adapterId) => await history.GetLastSuccessfulChangeAsync(adapterId) is not null;

    public async Task RestorePreviousAsync(string adapterId)
    {
        var entry = await history.GetLastSuccessfulChangeAsync(adapterId);
        var snap = entry is null ? null : DnsSnapshot.FromJson(entry.PreviousConfiguration);
        if (snap is null)
        {
            dialogs.Notify("Nothing to restore", "No earlier DNS change made by DNS Manager Pro was found for this adapter.");
            return;
        }

        await RefreshAdaptersAsync();
        await ShowAsync(new ApplyDnsRequest("Restore previous DNS settings", "Previous settings", null, snap, $"{ArgPrevious} {adapterId}"), adapterId);
    }

    public async Task HandleStartupArgsAsync(string[] args)
    {
        try
        {
            if (args.Length >= 1 && args[0] == ArgAutomatic)
            {
                nav.NavigateTo(NavSection.Adapters);
                await RestoreAutomaticAsync();
            }
            else if (args.Length >= 2 && args[0] == ArgProvider)
            {
                nav.NavigateTo(NavSection.Providers);
                var p = (await providers.GetAllAsync()).FirstOrDefault(x => x.Id == args[1]);
                if (p is not null)
                {
                    await ApplyProviderAsync(p);
                }
            }
            else if (args.Length >= 2 && args[0] == ArgProfile)
            {
                nav.NavigateTo(NavSection.Profiles);
                var p = (await profiles.GetAllAsync()).FirstOrDefault(x => x.Id == args[1]);
                if (p is not null)
                {
                    await ApplyProfileAsync(p);
                }
            }
            else if (args.Length >= 2 && args[0] == ArgPrevious)
            {
                nav.NavigateTo(NavSection.Adapters);
                await RestorePreviousAsync(args[1]);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Startup intent failed");
        }
    }

    private async Task RefreshAdaptersAsync() => await state.RefreshAsync(false);

    private async Task<bool> ShowAsync(ApplyDnsRequest request, string? adapterId)
    {
        var vm = new ApplyDnsViewModel(request, state.Adapters, adapterId ?? state.ActiveAdapter?.Id, service, elevation, privilege.IsElevated, encrypted);
        dialogs.ShowApply(vm);
        state.MarkAppChange();
        await state.RefreshAsync(true); // always re-read: reflects both success and rollback
        return vm.Succeeded;
    }
}

