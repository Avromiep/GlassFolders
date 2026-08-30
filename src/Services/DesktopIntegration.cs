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
        if (slug.Length > 90) slug = slug[..90];
        return "Avromiep.GlassFolders.Folder." + slug;
    }

    /// <summary>
    /// Creates/updates the desktop .lnk. Its target is our own exe with `--open`, so a
    /// double-click pops the expanded panel instead of launching a program.
    /// </summary>
    public static void PublishDesktopShortcut(FolderModel folder, string icoPath)
    {
        var lnkPath = DesktopLnkPathFor(folder.Name);
        ShellLink.Create(
            lnkPath,
            targetPath: ExePath,
            arguments: $"--open \"{folder.Name}\"",
            iconPath: icoPath,
            iconIndex: 0,
            description: $"Glass folder: {folder.Name}",
            workingDirectory: AppContext.BaseDirectory,
            appUserModelId: AppUserModelIdFor(folder.Name));

        RefreshIcon(lnkPath);
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
        try { if (File.Exists(lnkPath)) File.Delete(lnkPath); } catch { }
        RefreshIcon(lnkPath);
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
