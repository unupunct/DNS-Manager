using System.Windows;
using System.Windows.Controls;
using DNSManagerPro.App.ViewModels;

namespace DNSManagerPro.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is HistoryViewModel vm)
        {
            await vm.LoadAsync();
        }
    }
}
