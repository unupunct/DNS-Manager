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
        sc.AddSingleton<IDnsLookupClient, DnsLookupClient>();
        sc.AddSingleton<IPingService, IcmpPingService>();
        sc.AddSingleton<IOsResolver, SystemResolver>();
        sc.AddSingleton<IDnsCacheInspector, PowerShellDnsCacheInspector>();
        sc.AddSingleton<IDiagnosticsRepository, DNSManagerPro.Data.SqliteDiagnosticsRepository>();
        sc.AddSingleton<IMonitorRepository, DNSManagerPro.Data.SqliteMonitorRepository>();
        sc.AddSingleton<ITimelineRepository, DNSManagerPro.Data.SqliteTimelineRepository>();
        sc.AddSingleton<DNSManagerPro.Core.Diagnostics.DiagnosticsWorkflow>();
        sc.AddSingleton(sp => new SettingsService(AppPaths.SettingsFile, sp.GetRequiredService<ILogger<SettingsService>>()));
        sc.AddSingleton<IStartupRegistration, StartupRegistration>();
        sc.AddSingleton(sp => new DNSManagerPro.Data.DatabaseMaintenance(
            sp.GetRequiredService<DNSManagerPro.Data.SqliteDatabase>(), AppPaths.Database, sp.GetRequiredService<ILogger<DNSManagerPro.Data.DatabaseMaintenance>>()));
        sc.AddSingleton<INotifier, TrayNotifier>();
        sc.AddSingleton<MonitorService>();
        sc.AddSingleton<SystemStateService>();
        sc.AddSingleton<INavigationService, NavigationService>();
        sc.AddTransient<DashboardViewModel>();
        sc.AddSingleton<ProvidersViewModel>();
        sc.AddSingleton<AdaptersViewModel>();
        sc.AddSingleton<BenchmarkViewModel>();
        sc.AddSingleton<ProfilesViewModel>();
        sc.AddSingleton<CustomDnsViewModel>();
        sc.AddSingleton<DiagnosticsViewModel>();
        sc.AddSingleton<HistoryViewModel>();
        sc.AddSingleton<SettingsViewModel>();
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
        _services.GetRequiredService<SettingsService>().Load();
        _services.GetRequiredService<MonitorService>().Start();
        _ = _services.GetRequiredService<BenchmarkSession>().LoadAsync();
        var settings = _services.GetRequiredService<SettingsService>().Current;
        var window = _services.GetRequiredService<MainWindow>();
        var minimized = settings.LaunchMinimized || e.Args.Contains(StartupRegistration.MinimizedArgument, StringComparer.Ordinal);
        var intent = e.Args.Where(a => a != StartupRegistration.MinimizedArgument).ToArray();
        if (intent.Length > 0)
        {
            minimized = false; // a user-requested action (for example after the UAC restart) must be visible
        }

        window.ShowStartup(minimized);
        if (intent.Length > 0)
        {
            var flow = _services.GetRequiredService<IApplyFlow>();
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(async () => await flow.HandleStartupArgsAsync(intent)));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}





