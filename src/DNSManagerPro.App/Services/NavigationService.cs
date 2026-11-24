namespace DNSManagerPro.App.Services;

public enum NavSection
{
    Dashboard,
    Providers,
    Benchmark,
    CustomDns,
    Adapters,
    Profiles,
    Diagnostics,
    History,
    Settings,
    About,
}

/// <summary>Lets any view model request navigation without knowing about the shell.</summary>
public interface INavigationService
{
    event Action<NavSection>? NavigationRequested;

    void NavigateTo(NavSection section);
}

public sealed class NavigationService : INavigationService
{
    public event Action<NavSection>? NavigationRequested;

    public void NavigateTo(NavSection section) => NavigationRequested?.Invoke(section);
}
