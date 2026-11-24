using System.Windows;
using DNSManagerPro.App.Dialogs;
using DNSManagerPro.Core.Providers;

namespace DNSManagerPro.App.Services;

public interface IDialogService
{
    /// <summary>Shows the provider editor. Returns the saved provider, or null when cancelled.</summary>
    DnsProvider? EditProvider(DnsProvider? existing);

    bool Confirm(string title, string message, string confirmText = "Confirm");

    void Notify(string title, string message);

    /// <summary>Shows the preview/apply dialog. True when a change was applied successfully.</summary>
    bool ShowApply(ApplyDnsViewModel viewModel);

    /// <summary>Asks where to save a file. Returns null when cancelled.</summary>
    string? PickSaveFile(string title, string filter, string defaultFileName);

    /// <summary>Asks which file to open. Returns null when cancelled.</summary>
    string? PickOpenFile(string title, string filter);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current.MainWindow;

    public DnsProvider? EditProvider(DnsProvider? existing)
    {
        var vm = new ProviderEditViewModel(existing);
        var win = new ProviderEditWindow { DataContext = vm, Owner = Owner };
        vm.CloseRequested += ok => win.DialogResult = ok;
        return win.ShowDialog() == true ? vm.Result : null;
    }

    public bool Confirm(string title, string message, string confirmText = "Confirm")
    {
        var win = new ConfirmWindow(title, message, confirmText) { Owner = Owner };
        return win.ShowDialog() == true;
    }

    public void Notify(string title, string message) =>
        new ConfirmWindow(title, message, "OK", showCancel: false) { Owner = Owner }.ShowDialog();

    public string? PickOpenFile(string title, string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultFileName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName, OverwritePrompt = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public bool ShowApply(ApplyDnsViewModel viewModel)
    {
        var win = new ApplyDnsWindow { DataContext = viewModel, Owner = Owner };
        viewModel.CloseRequested += ok =>
        {
            win.DialogResult = ok;
        };
        return win.ShowDialog() == true;
    }
}


