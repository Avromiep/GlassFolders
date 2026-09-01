using System.IO;
using Microsoft.Win32;

namespace GlassFolders.Services;

/// <summary>
/// Runs Glass Folders automatically when the user signs in (an HKCU Run entry). Without this the
/// tray app is gone after a reboot/sign-out until manually relaunched, so single-click silently
/// stops working "after a while". On by default; a marker file records an explicit opt-out.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Glass Folders";

    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GlassFolders", "autostart-off");

    /// <summary>True unless the user has explicitly turned auto-start off.</summary>
    public static bool IsEnabled => !File.Exists(MarkerPath);

    /// <summary>Reflect the current setting into the Run key on startup (idempotent).</summary>
    public static void ApplyDefault()
    {
        if (IsEnabled) Register(); else Unregister();
    }

    public static void SetEnabled(bool on)
    {
        try
        {
            if (on)
            {
                if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
                Register();
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                File.WriteAllText(MarkerPath, "off");
                Unregister();
            }
        }
        catch { }
    }

    private static void Register()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            // --startup => start silently to the tray (don't pop the manager window on every login).
            k?.SetValue(ValueName, "\"" + exe + "\" --startup");
        }
        catch { }
    }

    private static void Unregister()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            k?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }
}
