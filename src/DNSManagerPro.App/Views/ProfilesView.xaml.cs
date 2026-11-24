using System.Windows;
using System.Windows.Controls;
using DNSManagerPro.App.ViewModels;

namespace DNSManagerPro.App.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProfilesViewModel vm)
        {
            await vm.LoadAsync();
        }
    }
}
