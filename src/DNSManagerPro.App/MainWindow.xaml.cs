using System.Windows;
using DNSManagerPro.App.Services;
using DNSManagerPro.App.ViewModels;
using DNSManagerPro.Infrastructure;

namespace DNSManagerPro.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly SettingsService _settings;

    public MainWindow(MainViewModel vm, SettingsService settings)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        DataContext = vm;
        DarkTitleBar.Attach(this);
    }

    /// <summary>Shows the window, minimized (to the taskbar or the tray, per settings) when requested.</summary>
    public void ShowStartup(bool minimized)
    {
        if (minimized)
        {
            WindowState = WindowState.Minimized;
        }

        Show();
        HideIfTrayMinimized();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        HideIfTrayMinimized();
    }

    private void HideIfTrayMinimized()
    {
        if (WindowState == WindowState.Minimized && _settings.Current.MinimizeToTray)
        {
            Hide(); // the tray icon (double-click or menu) brings it back
        }
    }

    private void OnToggleSidebar(object sender, RoutedEventArgs e) => _vm.IsSidebarExpanded = !_vm.IsSidebarExpanded;
}
