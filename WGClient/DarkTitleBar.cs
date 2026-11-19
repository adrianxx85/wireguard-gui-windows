using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WGClient;

static class DarkTitleBar
{
    /// <summary>Dunkle Titelleiste (Windows 10 20H1+/11). Aufruf in OnSourceInitialized.</summary>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var dark = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
