using System.Windows;
using DNSManagerPro.App.Dialogs;
using DNSManagerPro.Core.Providers;

namespace DNSManagerPro.App.Services;

public interface IDialogService
{
    /// <summary>Shows the provider editor. Returns the saved provider, or null when cancelled.</summary>
    DnsProvider? EditProvider(DnsProvider? existing);

    bool Confirm(string title, string message, string confirmText = "Confirm");
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current.MainWindow;

    public DnsProvider? EditProvider(DnsProvider? existing)
    {
        var vm = new ProviderEditViewModel(existing);
        var win = new ProviderEditWindow { DataContext = vm, Owner = Owner };
        vm.CloseRequested += ok =>
        {
            win.DialogResult = ok;
        };
        return win.ShowDialog() == true ? vm.Result : null;
    }

    public bool Confirm(string title, string message, string confirmText = "Confirm")
    {
        var win = new ConfirmWindow(title, message, confirmText) { Owner = Owner };
        return win.ShowDialog() == true;
    }
}
