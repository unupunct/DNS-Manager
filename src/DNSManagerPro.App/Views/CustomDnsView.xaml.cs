using System.Windows;
using System.Windows.Controls;
using DNSManagerPro.App.ViewModels;

namespace DNSManagerPro.App.Views;

public partial class CustomDnsView : UserControl
{
    public CustomDnsView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is CustomDnsViewModel vm)
        {
            await vm.OnShownAsync();
        }
    }
}
