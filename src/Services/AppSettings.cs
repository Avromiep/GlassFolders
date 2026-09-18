using System.IO;
using System.Linq;

namespace GlassFolders.Services;

/// <summary>Small GLOBAL app preferences (not per-folder), stored as key=value in
/// <c>%LOCALAPPDATA%\GlassFolders\appsettings.txt</c>.</summary>
public static class AppSettings
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassFolders");
    private static string FilePath => Path.Combine(Dir, "appsettings.txt");

    /// <summary>File-list folder look: true = plain (modern, follows light/dark), false = frosted
    /// glass (the default). Only affects List-view folders; app grids are always frosted.</summary>
    public static bool PlainFileList
    {
        get => string.Equals(Read("filelist"), "plain", StringComparison.OrdinalIgnoreCase);
        set => Write("filelist", value ? "plain" : "frosted");
    }

    private static string? Read(string key)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var kv = line.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return kv[1].Trim();
            }
        }
        catch { }
        return null;
    }

    private static void Write(string key, string value)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath).ToList() : new List<string>();
            bool set = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var kv = lines[i].Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"{key}={value}";
                    set = true;
                    break;
                }
            }
            if (!set) lines.Add($"{key}={value}");
            File.WriteAllLines(FilePath, lines);
        }
        catch { }
    }
}
