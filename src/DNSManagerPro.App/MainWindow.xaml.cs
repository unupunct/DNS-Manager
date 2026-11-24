using System.Windows;
using DNSManagerPro.App.Services;
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
        DarkTitleBar.Attach(this);
    }

    private void OnToggleSidebar(object sender, RoutedEventArgs e) => _vm.IsSidebarExpanded = !_vm.IsSidebarExpanded;
}
