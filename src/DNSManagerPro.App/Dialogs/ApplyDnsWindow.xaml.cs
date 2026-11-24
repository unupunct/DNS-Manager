using System.Windows;
using DNSManagerPro.App.Services;

namespace DNSManagerPro.App.Dialogs;

public partial class ApplyDnsWindow : Window
{
    public ApplyDnsWindow()
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
    }
}
