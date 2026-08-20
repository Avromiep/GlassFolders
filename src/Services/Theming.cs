using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using static GlassFolders.NativeMethods;

namespace GlassFolders.Services;

/// <summary>Shared theme detection + Win11 glass chrome for all windows/dialogs.</summary>
public static class Theming
{
    public static bool IsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int i) return i == 0;
        }
        catch { }
        return false;
    }

    /// <summary>Applies acrylic backdrop + rounded corners + dark-mode caption. Call after the
    /// HWND exists (SourceInitialized).</summary>
    public static void ApplyGlass(Window w, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int d = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref d, sizeof(int));
            int backdrop = DWMSBT_TRANSIENTWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch { }
    }
}
