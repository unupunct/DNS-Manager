using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DNSManagerPro.App.ViewModels;

namespace DNSManagerPro.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
    }

    private void OnToggleSidebar(object sender, RoutedEventArgs e) => _vm.IsSidebarExpanded = !_vm.IsSidebarExpanded;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dark = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var caption = 0x001C1410; // COLORREF (BGR) of #10141C; Windows 11 only, ignored elsewhere
        _ = DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
    }
}
