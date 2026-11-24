using System.Windows;
using System.Windows.Controls;
using DNSManagerPro.App.ViewModels;

namespace DNSManagerPro.App.Views;

public partial class BenchmarkView : UserControl
{
    public BenchmarkView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is BenchmarkViewModel vm && !vm.IsRunning)
        {
            await vm.RefreshTargetsAsync();
        }
    }
}
