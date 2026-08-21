using System.IO;

namespace GlassFolders.Services;

/// <summary>Lightweight append-only trace log for diagnosing the click/open path.</summary>
public static class Diag
{
    private static readonly object Lock = new();

    public static bool Enabled { get; set; } =
        Environment.GetEnvironmentVariable("LF_DIAG") == "1";

    public static string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "liquidfolders-diag.log");

    public static void Log(string msg)
    {
        if (!Enabled) return;
        try
        {
            lock (Lock)
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
