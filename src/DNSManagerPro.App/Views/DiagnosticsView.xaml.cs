using System.Windows;
using System.Windows.Controls;
using DNSManagerPro.App.ViewModels;

namespace DNSManagerPro.App.Views;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiagnosticsViewModel vm)
        {
            await vm.OnShownAsync();
        }
    }
}
