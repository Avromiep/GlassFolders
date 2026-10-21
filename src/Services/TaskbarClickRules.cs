namespace GlassFolders.Services;

/// <summary>
/// Pure decision logic for the taskbar-pin fast path — no Win32, no state, so it is directly
/// testable. A cached pin rectangle hit is NOT enough to open a folder: the window actually under
/// the cursor (at both mouse-down and mouse-up) must be a taskbar. That way a click inside a
/// full-screen app that merely overlaps where a pin sits underneath (e.g. a maximised RDP / mstsc
/// window covering the taskbar) belongs to that app and never pops our folder open over it.
/// </summary>
public static class TaskbarClickRules
{
    /// <summary>True for the primary taskbar (Shell_TrayWnd) and each extra monitor's taskbar
    /// (Shell_SecondaryTrayWnd).</summary>
    public static bool IsTaskbarRootClass(string? cls) =>
        cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    /// <summary>Open a pinned folder only when the click hit our pin's cached rect AND the window
    /// under the cursor was the taskbar at both press and release. Pressing Win to raise the taskbar
    /// over a full-screen app and clicking the pin still opens — the root under the cursor is then the
    /// taskbar, which is the authority.</summary>
    public static bool ShouldOpenPin(string? rootClassAtDown, string? rootClassAtUp, bool pinRectHit, bool isOurFolder)
        => pinRectHit && isOurFolder
           && IsTaskbarRootClass(rootClassAtDown) && IsTaskbarRootClass(rootClassAtUp);
}
