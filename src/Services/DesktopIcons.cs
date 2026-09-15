using System.Windows.Automation;
using static GlassFolders.NativeMethods;

namespace GlassFolders.Services;

/// <summary>
/// Best-effort lookup of which monitor a desktop icon currently sits on. Used by the manager to
/// show, when a folder is set to open on "the same monitor as the folder", which monitor that is
/// right now. Returns null when it can't tell (e.g. a taskbar-only folder with no desktop icon).
/// </summary>
public static class DesktopIcons
{
    /// <summary>The device name (e.g. \\.\DISPLAY2) of the monitor holding the desktop icon named
    /// <paramref name="iconName"/>, or null if not found. Call off the UI thread — it walks the
    /// desktop's icon list via UI Automation.</summary>
    public static string? MonitorDeviceOf(string iconName)
    {
        try
        {
            var list = FindDesktopListView();
            if (list == IntPtr.Zero) return null;
            var el = AutomationElement.FromHandle(list);
            var items = el.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            foreach (AutomationElement it in items)
            {
                string name;
                System.Windows.Rect r;
                try { name = it.Current.Name; r = it.Current.BoundingRectangle; }
                catch { continue; }
                if (!string.Equals(name, iconName, StringComparison.OrdinalIgnoreCase)) continue;
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return null;
                var center = new System.Drawing.Point(
                    (int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                return System.Windows.Forms.Screen.FromPoint(center).DeviceName;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Handle of the desktop's SysListView32 ("FolderView"), across the normal Progman
    /// layout and the WorkerW/slideshow layout.</summary>
    private static IntPtr FindDesktopListView()
    {
        var progman = FindWindow("Progman", null);
        var def = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (def == IntPtr.Zero)
        {
            IntPtr worker = IntPtr.Zero;
            do
            {
                worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
                if (worker == IntPtr.Zero) break;
                def = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            } while (def == IntPtr.Zero);
        }
        return def == IntPtr.Zero ? IntPtr.Zero
            : FindWindowEx(def, IntPtr.Zero, "SysListView32", "FolderView");
    }
}
