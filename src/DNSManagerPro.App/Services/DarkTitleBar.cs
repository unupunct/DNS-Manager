using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DNSManagerPro.App.Services;

/// <summary>Gives a window the dark title bar (Windows 10 1809+; caption colour on Windows 11).</summary>
internal static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Attach(Window window) =>
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var dark = 1;
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            var caption = 0x001C1410; // COLORREF (BGR) of #10141C; Windows 11 only, ignored elsewhere
            _ = DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        };
}
