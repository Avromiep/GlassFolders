using System.IO;
using GlassFolders.Models;

namespace GlassFolders.Services;

/// <summary>
/// Owns everything on disk: the per-folder directories of .lnk files, the order.txt
/// ordering sidecar, the generated composite .ico files, and the desktop shortcut that
/// launches the expanded panel. Everything stays browsable in Explorer.
/// </summary>
public sealed class FolderStore
{
    public string RootPath { get; }
    public string FoldersPath { get; }
    public string IconsPath { get; }

    public FolderStore(string? rootOverride = null)
    {
        RootPath = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlassFolders");
        FoldersPath = Path.Combine(RootPath, "Folders");
        IconsPath = Path.Combine(RootPath, "Icons");
        Directory.CreateDirectory(FoldersPath);
        Directory.CreateDirectory(IconsPath);
    }

    // ---- Folder lifecycle ----

    public IEnumerable<FolderModel> ListFolders()
    {
        foreach (var dir in Directory.EnumerateDirectories(FoldersPath))
            yield return LoadFolder(dir);
    }

    public FolderModel LoadFolder(string dir)
    {
        var name = Path.GetFileName(dir);
        var model = new FolderModel { Name = name, DirectoryPath = dir };

        var lnks = Directory.EnumerateFiles(dir, "*.lnk").ToList();
        var byName = lnks.ToDictionary(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

        // Apply saved order first, then append any new .lnk files not yet listed.
        var ordered = new List<string>();
        var orderFile = Path.Combine(dir, "order.txt");
        if (File.Exists(orderFile))
        {
            foreach (var line in File.ReadAllLines(orderFile))
            {
                var fn = line.Trim();
                if (byName.TryGetValue(fn, out var full))
                {
                    ordered.Add(full);
                    byName.Remove(fn);
                }
            }
        }
        ordered.AddRange(byName.Values.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

        foreach (var p in ordered)
            model.Items.Add(new ShortcutItem { LnkPath = p });

        LoadSettings(model);
        return model;
    }

    private static void LoadSettings(FolderModel model)
    {
        var file = Path.Combine(model.DirectoryPath, "settings.txt");
        if (!File.Exists(file)) return;
        foreach (var line in File.ReadAllLines(file))
        {
            var kv = line.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim().ToLowerInvariant();
            var val = kv[1].Trim();
            if (key == "frostiness" && int.TryParse(val, out var f))
                model.Frostiness = Math.Clamp(f, 0, 100);
            else if (key == "ondesktop" && bool.TryParse(val, out var b))
                model.OnDesktop = b;
            else if (key == "panelposition" && int.TryParse(val, out var pp))
                model.PanelPosition = Math.Clamp(pp, 0, 8);
        }
    }

    public void SaveSettings(FolderModel folder)
    {
        var file = Path.Combine(folder.DirectoryPath, "settings.txt");
        File.WriteAllLines(file, new[]
        {
            $"frostiness={folder.Frostiness}",
            $"ondesktop={folder.OnDesktop}",
            $"panelposition={folder.PanelPosition}",
        });
    }

    public FolderModel? FindByName(string name)
    {
        var dir = Path.Combine(FoldersPath, Sanitize(name));
        return Directory.Exists(dir) ? LoadFolder(dir) : null;
    }

    public FolderModel CreateFolder(string name)
    {
        var dir = Path.Combine(FoldersPath, Sanitize(name));
        Directory.CreateDirectory(dir);
        var model = LoadFolder(dir);
        RegenerateAndPublish(model);
        return model;
    }

    public void RenameFolder(FolderModel folder, string newName)
    {
        var newDir = Path.Combine(FoldersPath, Sanitize(newName));
        if (string.Equals(newDir, folder.DirectoryPath, StringComparison.OrdinalIgnoreCase))
            return;
        Directory.Move(folder.DirectoryPath, newDir);
        DesktopIntegration.RemoveDesktopShortcut(folder.Name);
        var moved = LoadFolder(newDir);
        RegenerateAndPublish(moved);
    }

    public void DeleteFolder(FolderModel folder)
    {
        DesktopIntegration.RemoveDesktopShortcut(folder.Name);
        try { Directory.Delete(folder.DirectoryPath, true); } catch { }
        var iconDir = Path.Combine(IconsPath, Path.GetFileName(folder.DirectoryPath));
        try { Directory.Delete(iconDir, true); } catch { }
    }

    // ---- Contents ----

    /// <summary>Adds a shortcut from a dropped file/exe/existing .lnk.</summary>
    public void AddShortcut(FolderModel folder, string sourcePath)
    {
        string baseName = Path.GetFileNameWithoutExtension(sourcePath);
        string destLnk = UniqueLnkPath(folder.DirectoryPath, baseName);

        if (Path.GetExtension(sourcePath).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            File.Copy(sourcePath, destLnk, overwrite: false);
        else
            ShellLink.Create(destLnk, sourcePath, description: baseName,
                workingDirectory: Path.GetDirectoryName(sourcePath));

        folder.Items.Add(new ShortcutItem { LnkPath = destLnk });
        SaveOrder(folder);
    }

    /// <summary>Adds a shortcut with an explicit display name pointing at a resolved target
    /// (used by folder import, where the .lnk should keep its original name).</summary>
    public void AddResolved(FolderModel folder, string targetPath, string displayName)
    {
        string destLnk = UniqueLnkPath(folder.DirectoryPath, displayName);
        ShellLink.Create(destLnk, targetPath, description: displayName,
            workingDirectory: Path.GetDirectoryName(targetPath));
        folder.Items.Add(new ShortcutItem { LnkPath = destLnk });
        SaveOrder(folder);
    }

    public void RemoveShortcut(FolderModel folder, ShortcutItem item)
    {
        try { File.Delete(item.LnkPath); } catch { }
        folder.Items.Remove(item);
        SaveOrder(folder);
    }

    public void Move(FolderModel folder, int from, int to)
    {
        if (from < 0 || from >= folder.Items.Count) return;
        to = Math.Clamp(to, 0, folder.Items.Count - 1);
        var item = folder.Items[from];
        folder.Items.RemoveAt(from);
        folder.Items.Insert(to, item);
        SaveOrder(folder);
    }

    public void SaveOrder(FolderModel folder)
    {
        var orderFile = Path.Combine(folder.DirectoryPath, "order.txt");
        File.WriteAllLines(orderFile, folder.Items.Select(i => Path.GetFileName(i.LnkPath)));
    }

    // ---- Icon + desktop publication ----

    /// <summary>Rebuilds the composite icon and (re)creates the desktop shortcut.</summary>
    public void RegenerateAndPublish(FolderModel folder)
    {
        var icoDir = Path.Combine(IconsPath, Path.GetFileName(folder.DirectoryPath));
        Directory.CreateDirectory(icoDir);

        // Versioned filename dodges Explorer's per-path icon cache.
        var icoPath = Path.Combine(icoDir, $"composite_{DateTime.Now.Ticks}.ico");
        bool built = IconComposer.BuildIcon(folder.FirstPagePaths(), icoPath);
        if (!built)
        {
            // Empty folder: still render the frosted panel with no icons.
            IconComposer.BuildIcon(Array.Empty<string>(), icoPath);
        }

        if (folder.OnDesktop)
            DesktopIntegration.PublishDesktopShortcut(folder, icoPath);
        else
            DesktopIntegration.RemoveDesktopShortcut(folder.Name);

        // Best-effort cleanup of older .ico versions.
        foreach (var old in Directory.EnumerateFiles(icoDir, "composite_*.ico"))
        {
            if (!string.Equals(old, icoPath, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(old); } catch { }
        }
    }

    // ---- helpers ----

    private static string UniqueLnkPath(string dir, string baseName)
    {
        baseName = Sanitize(baseName);
        string candidate = Path.Combine(dir, baseName + ".lnk");
        int n = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(dir, $"{baseName} ({n++}).lnk");
        return candidate;
    }

    public static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
