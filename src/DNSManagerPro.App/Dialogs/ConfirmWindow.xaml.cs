using System.Windows;

namespace DNSManagerPro.App.Dialogs;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string message, string confirmText)
    {
        InitializeComponent();
        DNSManagerPro.App.Services.DarkTitleBar.Attach(this);
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = confirmText;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}

