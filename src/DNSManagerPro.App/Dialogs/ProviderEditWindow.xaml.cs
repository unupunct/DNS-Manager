using System.Windows;

namespace DNSManagerPro.App.Dialogs;

public partial class ProviderEditWindow : Window
{
    public ProviderEditWindow()
    {
        InitializeComponent();
        DNSManagerPro.App.Services.DarkTitleBar.Attach(this);
    }
}

