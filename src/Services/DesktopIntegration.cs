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
            workingDirectory: AppContext.BaseDirectory);

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

    /// <summary>Tell the shell a specific item changed, then flush the association cache.</summary>
    public static void RefreshIcon(string path)
    {
        IntPtr p = Marshal.StringToHGlobalUni(path);
        try
        {
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, p, IntPtr.Zero);
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }
}
