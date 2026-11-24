using System.Windows;

namespace DNSManagerPro.App.Dialogs;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string message, string confirmText, bool showCancel = true)
    {
        InitializeComponent();
        DNSManagerPro.App.Services.DarkTitleBar.Attach(this);
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = confirmText;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}


