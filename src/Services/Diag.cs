using System.IO;

namespace GlassFolders.Services;

/// <summary>
/// Lightweight append-only trace log for diagnosing the click/open path. On by default now
/// (it only writes on clicks/open/close, never in a hot loop) and stored in the app's own
/// data folder so it survives across runs and is easy to find. Users can grab a copy from
/// Settings → Diagnostics. Set env LF_DIAG=0 to turn it off.
/// </summary>
public static class Diag
{
    private static readonly object Lock = new();
    private const long MaxBytes = 2 * 1024 * 1024; // rotate past ~2 MB so it can't grow forever

    public static bool Enabled { get; set; } =
        Environment.GetEnvironmentVariable("LF_DIAG") != "0";

    /// <summary>%LOCALAPPDATA%\GlassFolders\logs\glassfolders-diag.log</summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GlassFolders", "logs", "glassfolders-diag.log");

    public static void Log(string msg)
    {
        if (!Enabled) return;
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                RotateIfLarge();
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }

    /// <summary>Writes a session banner so each run is easy to tell apart in the log.</summary>
    public static void LogSession(string version)
    {
        Log($"===== session start  v{version}  {Environment.MachineName}  " +
            $"{Environment.OSVersion.VersionString}  ({System.Windows.Forms.SystemInformation.MonitorCount} monitor(s)) =====");
    }

    /// <summary>Copies the current log to <paramref name="destPath"/>. Returns false if there's nothing to copy.</summary>
    public static bool SaveCopyTo(string destPath)
    {
        try
        {
            lock (Lock)
            {
                if (!File.Exists(Path)) return false;
                File.Copy(Path, destPath, overwrite: true);
                return true;
            }
        }
        catch { return false; }
    }

    private static void RotateIfLarge()
    {
        try
        {
            var fi = new FileInfo(Path);
            if (fi.Exists && fi.Length > MaxBytes)
            {
                var prev = Path + ".prev";
                if (File.Exists(prev)) File.Delete(prev);
                File.Move(Path, prev); // keep one previous generation
            }
        }
        catch { }
    }
}
