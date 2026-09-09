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
        // Uniquify so creating "Folder" twice (or two names that sanitize to the same directory)
        // gives a distinct "Folder (2)" instead of silently reusing the existing one.
        var baseName = Sanitize(name);
        var dir = Path.Combine(FoldersPath, baseName);
        for (int n = 2; Directory.Exists(dir); n++)
            dir = Path.Combine(FoldersPath, $"{baseName} ({n})");
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
        // Renaming onto an existing folder would throw a raw IO error; give a clear one instead.
        if (Directory.Exists(newDir))
            throw new InvalidOperationException($"A folder named “{Sanitize(newName)}” already exists.");
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
    /// (used by folder import, where the .lnk should keep its original name). Optional
    /// <paramref name="arguments"/> and <paramref name="iconPath"/> are preserved so that
    /// e.g. per-profile browser shortcuts keep both their profile switch and their distinct
    /// icon after an export/import round-trip.</summary>
    public void AddResolved(FolderModel folder, string targetPath, string displayName,
        string? arguments = null, string? iconPath = null, int iconIndex = 0)
    {
        string destLnk = UniqueLnkPath(folder.DirectoryPath, displayName);
        // Only pass an icon the target machine can actually resolve; otherwise let the shell
        // fall back to the target's own icon rather than baking in a dead path.
        string? icon = !string.IsNullOrEmpty(iconPath) && File.Exists(iconPath) ? iconPath : null;
        ShellLink.Create(destLnk, targetPath, arguments: arguments,
            iconPath: icon, iconIndex: icon != null ? iconIndex : 0,
            description: displayName, workingDirectory: Path.GetDirectoryName(targetPath));
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
        name ??= "";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.', ' ');   // Windows ignores trailing dots/spaces on dir names
        // Never let a name resolve to the parent ("..") or the folders dir itself ("", ".") —
        // otherwise create/delete would operate on %LOCALAPPDATA%\GlassFolders and could wipe
        // every folder. Anything that collapses to empty/dots becomes a safe placeholder.
        if (name.Length == 0 || name == "." || name == "..") name = "_";
        return name;
    }

    /// <summary>Extracts the folder name from a `--open "Name"` (or `--open Name`) argument.</summary>
    private static string? ParseOpenName(string? args)
    {
        if (string.IsNullOrEmpty(args)) return null;
        int i = args.IndexOf("--open", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = args[(i + "--open".Length)..].TrimStart();
        if (rest.StartsWith('"'))
        {
            int end = rest.IndexOf('"', 1);
            return end > 1 ? rest[1..end] : null;
        }
        int sp = rest.IndexOf(' ');
        rest = sp < 0 ? rest : rest[..sp];
        return string.IsNullOrWhiteSpace(rest) ? null : rest;
    }

    /// <summary>If this .lnk is a nested-folder tile (targets our own launcher with `--open "X"`),
    /// returns the child folder name X; otherwise null. Used so export/import can carry nested
    /// folders as a folder reference instead of a machine-specific path to GFOpen.exe.</summary>
    public static string? NestedFolderName(string lnkPath)
    {
        var name = ParseOpenName(ShellLink.ReadArguments(lnkPath));
        if (name == null) return null;
        var target = ShellLink.ResolveTarget(lnkPath);
        var basefn = target != null ? Path.GetFileName(target) : "";
        return basefn.Equals("GFOpen.exe", StringComparison.OrdinalIgnoreCase)
            || basefn.Equals("GlassFolders.exe", StringComparison.OrdinalIgnoreCase)
            ? name : null;
    }

    /// <summary>Adds a tile that opens another (nested) folder by name — recreated against THIS
    /// machine's launcher, so it survives an export/import to a different PC.</summary>
    public void AddNestedFolderRef(FolderModel folder, string childName, string displayName)
    {
        string destLnk = UniqueLnkPath(folder.DirectoryPath, displayName);
        DesktopIntegration.CreateFolderShortcut(destLnk, childName, iconPath: null);
        folder.Items.Add(new ShortcutItem { LnkPath = destLnk });
        SaveOrder(folder);
    }
}
