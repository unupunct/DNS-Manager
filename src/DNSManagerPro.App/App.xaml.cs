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
        sc.AddSingleton(new DNSManagerPro.Data.SqliteDatabase(AppPaths.Database));
        sc.AddSingleton<DNSManagerPro.Data.SqliteProviderRepository>();
        sc.AddSingleton<IProviderRepository>(sp => sp.GetRequiredService<DNSManagerPro.Data.SqliteProviderRepository>());
        sc.AddSingleton<IAdapterStore>(sp => sp.GetRequiredService<DNSManagerPro.Data.SqliteProviderRepository>());
        sc.AddSingleton<IDialogService, DialogService>();
        sc.AddSingleton<IProcessRunner, ProcessRunner>();
        sc.AddSingleton<IDnsConfigurator, NetshDnsConfigurator>();
        sc.AddSingleton<IDnsHistoryRepository, DNSManagerPro.Data.SqliteHistoryRepository>();
        sc.AddSingleton<DNSManagerPro.Core.Configuration.DnsChangeService>();
        sc.AddSingleton<IElevationService, ElevationService>();
        sc.AddSingleton<IApplyFlow, ApplyFlow>();
        sc.AddSingleton<IDnsQueryClient, DnsQueryClient>();
        sc.AddSingleton<IBenchmarkRepository, DNSManagerPro.Data.SqliteBenchmarkRepository>();
        sc.AddSingleton<DNSManagerPro.Core.Benchmark.BenchmarkRunner>();
        sc.AddSingleton<BenchmarkSession>();
        sc.AddSingleton<IProfileRepository, DNSManagerPro.Data.SqliteProfileRepository>();
        sc.AddSingleton<IEncryptedDnsService, NetshEncryptedDnsService>();
        sc.AddSingleton<ProfileService>();
        sc.AddSingleton<SystemStateService>();
        sc.AddSingleton<INavigationService, NavigationService>();
        sc.AddTransient<DashboardViewModel>();
        sc.AddSingleton<ProvidersViewModel>();
        sc.AddSingleton<AdaptersViewModel>();
        sc.AddSingleton<BenchmarkViewModel>();
        sc.AddSingleton<ProfilesViewModel>();
        sc.AddSingleton<CustomDnsViewModel>();
        sc.AddSingleton<MainViewModel>();
        sc.AddSingleton<MainWindow>();
        _services = sc.BuildServiceProvider();

        var log = _services.GetRequiredService<ILogger<App>>();
        try
        {
            _services.GetRequiredService<DNSManagerPro.Data.SqliteDatabase>().Migrate();
            _services.GetRequiredService<DNSManagerPro.Data.SqliteProviderRepository>().SeedAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Database initialisation failed");
            MessageBox.Show("The local database could not be opened:\n\n" + ex.Message, "DNS Manager Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
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
        _ = _services.GetRequiredService<BenchmarkSession>().LoadAsync();
        _services.GetRequiredService<MainWindow>().Show();
        if (e.Args.Length > 0)
        {
            var flow = _services.GetRequiredService<IApplyFlow>();
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(async () => await flow.HandleStartupArgsAsync(e.Args)));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}




