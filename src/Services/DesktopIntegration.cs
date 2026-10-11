using System.IO;
using System.Runtime.InteropServices;
using GlassFolders.Models;
using static GlassFolders.NativeMethods;

namespace GlassFolders.Services;

/// <summary>
/// Places/updates the closed-folder shortcut on the desktop and nudges Explorer to
/// re-read its icon without an Explorer restart.
/// </summary>
public static class DesktopIntegration
{
    /// <summary>Test-only override so lifecycle tests don't touch the real desktop.</summary>
    public static string? DesktopDirOverride { get; set; }

    private static string DesktopDir =>
        DesktopDirOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    private static string ExePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GlassFolders.exe");

    /// <summary>
    /// The fast launcher folder shortcuts point at: a tiny exe that just forwards "open" to the
    /// running tray and exits, so a taskbar-pin / double-click opens in ~150ms instead of spinning
    /// up the whole WPF app (~800ms). Falls back to the full exe if the launcher isn't present
    /// (e.g. the portable single-exe distribution).
    /// </summary>
    private static string LauncherPath
    {
        get
        {
            var gf = Path.Combine(AppContext.BaseDirectory, "GFOpen.exe");
            return File.Exists(gf) ? gf : ExePath;
        }
    }

    public static string DesktopLnkPathFor(string folderName) =>
        Path.Combine(DesktopDir, folderName + ".lnk");

    /// <summary>
    /// A per-folder AppUserModelID so a taskbar/Start pin of this shortcut is its own app and
    /// launches on click (opening the folder), rather than being coalesced with — and trying to
    /// activate — our windowless tray process. Must be &lt;=128 chars, no spaces.
    /// </summary>
    public static string AppUserModelIdFor(string folderName)
    {
        var slug = new string(folderName.Where(c => char.IsLetterOrDigit(c)).ToArray());
        if (slug.Length == 0) slug = "Folder";
        if (slug.Length > 80) slug = slug[..80];
        // Names that reduce to the same slug ("Work" vs "Work!") would otherwise share one taskbar
        // identity; append a short stable hash of the full name so distinct folders stay distinct.
        uint h = 2166136261u;
        foreach (char c in folderName) h = (h ^ c) * 16777619u;
        return $"Avromiep.GlassFolders.Folder.{slug}.{h:x8}";
    }

    /// <summary>
    /// Creates/updates the desktop .lnk. Its target is our own exe with `--open`, so a
    /// double-click pops the expanded panel instead of launching a program.
    /// </summary>
    public static void PublishDesktopShortcut(FolderModel folder, string icoPath)
    {
        var lnkPath = DesktopLnkPathFor(folder.Name);
        // Never overwrite a desktop shortcut that isn't ours (e.g. the user's own "Chrome" shortcut
        // when a folder named "Chrome" is created/regenerated). This is the safety net behind the
        // create/rename name checks — it holds no matter which path tries to publish.
        if (File.Exists(lnkPath) && !IsOurFolderShortcut(lnkPath)) return;
        CreateFolderShortcut(lnkPath, folder.Name, icoPath);
        RefreshIcon(lnkPath);
    }

    /// <summary>Writes a .lnk that opens the named folder — target is our fast launcher with
    /// `--open`, plus the per-folder AppUserModelID. Shared by the desktop shortcut and by
    /// nested-folder tiles (so an imported nested folder re-links to this machine's launcher).</summary>
    public static void CreateFolderShortcut(string lnkPath, string folderName, string? iconPath)
    {
        ShellLink.Create(
            lnkPath,
            targetPath: LauncherPath,   // tiny fast forwarder (falls back to the full exe)
            arguments: $"--open \"{folderName}\"",
            iconPath: iconPath,
            iconIndex: 0,
            description: $"Glass folder: {folderName}",
            workingDirectory: AppContext.BaseDirectory,
            appUserModelId: AppUserModelIdFor(folderName));
    }

    /// <summary>
    /// Creates/updates the desktop launcher that opens the manager (settings) window.
    /// Target is our exe with no folder argument, so it lands on the manager.
    /// </summary>
    public static void PublishManagerShortcut(string appName, string icoPath)
    {
        var lnkPath = Path.Combine(DesktopDir, appName + ".lnk");
        ShellLink.Create(
            lnkPath,
            targetPath: ExePath,
            arguments: null,
            iconPath: icoPath,
            iconIndex: 0,
            description: $"{appName} — create and manage your glass folders",
            workingDirectory: AppContext.BaseDirectory);
        RefreshIcon(lnkPath);
    }

    public static void RemoveDesktopShortcut(string folderName)
    {
        var lnkPath = DesktopLnkPathFor(folderName);
        try
        {
            // Don't delete a desktop shortcut that isn't ours — a folder whose name happens to match
            // one of the user's own shortcuts must never take it down (on delete / hide / icon regen).
            if (!File.Exists(lnkPath)) { NotifyDeleted(lnkPath); return; }  // clear any stale/phantom icon
            if (!IsOurFolderShortcut(lnkPath)) return;
            File.Delete(lnkPath);
            // Tell the shell the item was DELETED so Explorer drops the icon immediately. A plain
            // "update" notification can leave a stale/phantom icon behind on the desktop.
            NotifyDeleted(lnkPath);
        }
        catch { }
    }

    /// <summary>Renames the desktop .lnk in place (old name → new name) and tells the shell it was
    /// RENAMED — Explorer keeps a renamed item exactly where it sat. This is what preserves the
    /// folder's on-desktop position across a rename; deleting the old + creating a new .lnk instead
    /// makes Explorer re-place the icon at the default slot (top-left of the primary monitor).
    /// Returns true if an existing desktop .lnk was moved (so the caller overwrites it in place
    /// rather than creating a fresh one).</summary>
    public static bool RenameDesktopShortcut(string oldName, string newName)
    {
        var oldPath = DesktopLnkPathFor(oldName);
        var newPath = DesktopLnkPathFor(newName);
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return File.Exists(newPath);
        if (!File.Exists(oldPath)) return false;    // nothing on the desktop to move (e.g. hidden folder)
        // Never clobber a desktop .lnk that isn't ours (e.g. the user's own "Chrome" shortcut when a
        // folder is renamed to "Chrome"). The rename is refused upstream in that case; guard here too.
        if (File.Exists(newPath) && !IsOurFolderShortcut(newPath)) return false;
        try
        {
            if (File.Exists(newPath)) File.Delete(newPath);
            File.Move(oldPath, newPath);
            NotifyRenamed(oldPath, newPath);
            return true;
        }
        catch { return false; }
    }

    /// <summary>True if the .lnk at <paramref name="path"/> is one of our folder shortcuts (carries our
    /// per-folder AppUserModelID), as opposed to an unrelated desktop shortcut the user created.</summary>
    public static bool IsOurFolderShortcut(string path)
    {
        try
        {
            if (ShellLink.ReadAppUserModelId(path)?
                .StartsWith("Avromiep.GlassFolders.Folder.", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            // Older shortcuts (created before we set the AUMID) still point at our launcher with
            // --open, so recognise those too rather than treating them as foreign.
            var target = ShellLink.ResolveTarget(path);
            var exe = target != null ? Path.GetFileName(target) : null;
            if (exe != null &&
                (exe.Equals("GFOpen.exe", StringComparison.OrdinalIgnoreCase) ||
                 exe.Equals("GlassFolders.exe", StringComparison.OrdinalIgnoreCase) ||
                 exe.Equals("LiquidFolders.exe", StringComparison.OrdinalIgnoreCase)))
                return (ShellLink.ReadArguments(path) ?? "").Contains("--open", StringComparison.OrdinalIgnoreCase);
            return false;
        }
        catch { return false; }
    }

    /// <summary>Notify the shell that <paramref name="oldPath"/> became <paramref name="newPath"/>.</summary>
    private static void NotifyRenamed(string oldPath, string newPath)
    {
        IntPtr a = Marshal.StringToHGlobalUni(oldPath), b = Marshal.StringToHGlobalUni(newPath);
        try { SHChangeNotify(SHCNE_RENAMEITEM, SHCNF_PATHW | SHCNF_FLUSH, a, b); }
        finally { Marshal.FreeHGlobal(a); Marshal.FreeHGlobal(b); }
    }

    /// <summary>Notify the shell that <paramref name="path"/> was deleted (removes a stale icon).</summary>
    public static void NotifyDeleted(string path)
    {
        IntPtr p = Marshal.StringToHGlobalUni(path);
        try { SHChangeNotify(SHCNE_DELETE, SHCNF_PATHW | SHCNF_FLUSH, p, IntPtr.Zero); }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Tell the shell that this specific item changed so Explorer repaints just its icon.
    /// We deliberately do NOT fire the global SHCNE_ASSOCCHANGED here — that repaints every icon on
    /// the desktop (the jarring "flash" on startup when several folders refresh at once).</summary>
    public static void RefreshIcon(string path)
    {
        IntPtr p = Marshal.StringToHGlobalUni(path);
        try
        {
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, p, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }
}
