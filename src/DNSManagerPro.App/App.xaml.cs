using System.Windows;
using DNSManagerPro.App.Services;
using DNSManagerPro.App.ViewModels;
using DNSManagerPro.Core.Abstractions;
using DNSManagerPro.Infrastructure;
using DNSManagerPro.Networking;
using DNSManagerPro.Windows.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureCreated();

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(new FileLoggerProvider(AppPaths.Logs)));
        sc.AddSingleton<INetworkAdapterService, WindowsNetworkAdapterService>();
        sc.AddSingleton<IPrivilegeService, WindowsPrivilegeService>();
        sc.AddSingleton<IDnsCacheService, WindowsDnsCacheService>();
        sc.AddSingleton<IDnsProbe, UdpDnsProbe>();
        sc.AddSingleton<SystemStateService>();
        sc.AddSingleton<INavigationService, NavigationService>();
        sc.AddTransient<DashboardViewModel>();
        sc.AddSingleton<MainViewModel>();
        sc.AddSingleton<MainWindow>();
        _services = sc.BuildServiceProvider();

        var log = _services.GetRequiredService<ILogger<App>>();
        DispatcherUnhandledException += (_, args) =>
        {
            log.LogError(args.Exception, "Unhandled UI exception");
            MessageBox.Show(
                "An unexpected error occurred and was logged:\n\n" + args.Exception.Message,
                "DNS Manager Pro",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        log.LogInformation("DNS Manager Pro starting (elevated: {Elevated})", _services.GetRequiredService<IPrivilegeService>().IsElevated);
        _services.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
