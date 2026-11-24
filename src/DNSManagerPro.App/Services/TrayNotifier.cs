using System.Windows;
using System.Windows.Forms;

namespace DNSManagerPro.App.Services;

public interface INotifier
{
    void Notify(string title, string message, bool isWarning = false);
}

/// <summary>Windows desktop notifications through a tray icon (balloon tips are shown as toast notifications on Windows 10/11).</summary>
public sealed class TrayNotifier : INotifier, IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayNotifier()
    {
        var exe = Environment.ProcessPath;
        _icon = new NotifyIcon
        {
            Icon = (exe is null ? null : System.Drawing.Icon.ExtractAssociatedIcon(exe)) ?? System.Drawing.SystemIcons.Application,
            Text = "DNS Manager Pro",
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowMainWindow();
        _icon.BalloonTipClicked += (_, _) => ShowMainWindow();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open DNS Manager Pro", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Exit", null, (_, _) => System.Windows.Application.Current.Shutdown());
        _icon.ContextMenuStrip = menu;
    }

    public void Notify(string title, string message, bool isWarning = false)
    {
        void Show() => _icon.ShowBalloonTip(6000, title, message, isWarning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess())
        {
            Show();
        }
        else
        {
            d.BeginInvoke(Show);
        }
    }

    private static void ShowMainWindow()
    {
        var w = System.Windows.Application.Current?.MainWindow;
        if (w is null)
        {
            return;
        }

        w.Show();
        if (w.WindowState == WindowState.Minimized)
        {
            w.WindowState = WindowState.Normal;
        }

        w.Activate();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
