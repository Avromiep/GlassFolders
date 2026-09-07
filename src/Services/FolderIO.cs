using System.IO;
using System.Text.Json;
using GlassFolders.Models;

namespace GlassFolders.Services;

public sealed record ImportSummary(int Folders, int AppsAdded, int AppsSkipped);

/// <summary>
/// Exports folders to a portable JSON file and imports them on another machine, re-linking
/// each app that is actually installed there (by original target, else by Start Menu match),
/// preserving order and skipping apps that aren't present.
/// </summary>
public static class FolderIO
{
    private sealed class AppDto
    {
        public string name { get; set; } = "";
        public string? target { get; set; }
        public string? file { get; set; }
        // Preserve the launch arguments (e.g. Brave/Chrome --profile-directory="Profile 1")
        // and the custom icon (e.g. per-profile "Google Profile.ico") so imported shortcuts
        // keep their distinct identity instead of collapsing to the bare app + generic icon.
        public string? args { get; set; }
        public string? icon { get; set; }
        public int iconIndex { get; set; }
    }

    private sealed class FolderDto
    {
        public string name { get; set; } = "";
        public int frostiness { get; set; } = FolderModel.DefaultFrostiness;
        public bool onDesktop { get; set; } = true;
        public int panelPosition { get; set; } = 4;
        public List<AppDto> apps { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static void ExportAll(FolderStore store, string path)
    {
        var list = new List<FolderDto>();
        foreach (var f in store.ListFolders())
        {
            var dto = new FolderDto
            {
                name = f.Name,
                frostiness = f.Frostiness,
                onDesktop = f.OnDesktop,
                panelPosition = f.PanelPosition,
            };
            foreach (var item in f.Items)
            {
                var target = ShellLink.ResolveTarget(item.LnkPath);
                var (iconPath, iconIndex) = ShellLink.ReadIconLocation(item.LnkPath);
                dto.apps.Add(new AppDto
                {
                    name = item.DisplayName,
                    target = target,
                    file = target != null ? Path.GetFileName(target) : null,
                    args = ShellLink.ReadArguments(item.LnkPath),
                    // Tokenize per-user prefixes so an icon under this user's AppData still
                    // resolves under a different username on the importing machine.
                    icon = Tokenize(iconPath),
                    iconIndex = iconIndex,
                });
            }
            list.Add(dto);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(list, JsonOpts));
    }

    public static ImportSummary Import(FolderStore store, string path)
    {
        var list = JsonSerializer.Deserialize<List<FolderDto>>(File.ReadAllText(path)) ?? new();
        var (byFile, byName) = BuildStartMenuIndex();

        int folders = 0, added = 0, skipped = 0;
        foreach (var dto in list)
        {
            var folder = store.CreateFolder(UniqueName(store, dto.name));
            folder.Frostiness = Math.Clamp(dto.frostiness, 0, 100);
            folder.OnDesktop = dto.onDesktop;
            folder.PanelPosition = dto.panelPosition;
            store.SaveSettings(folder);

            foreach (var app in dto.apps)
            {
                var resolved = Resolve(app, byFile, byName);
                if (resolved != null)
                {
                    store.AddResolved(folder, resolved, app.name,
                        arguments: app.args, iconPath: Expand(app.icon), iconIndex: app.iconIndex);
                    added++;
                }
                else skipped++;
            }
            store.RegenerateAndPublish(folder);
            folders++;
        }
        return new(folders, added, skipped);
    }

    private static string? Resolve(AppDto app,
        Dictionary<string, string> byFile, Dictionary<string, string> byName)
    {
        if (!string.IsNullOrEmpty(app.target) && File.Exists(app.target)) return app.target;
        if (!string.IsNullOrEmpty(app.file) && byFile.TryGetValue(app.file, out var p)) return p;
        if (!string.IsNullOrEmpty(app.name) && byName.TryGetValue(app.name, out var q)) return q;
        return null;
    }

    // Known per-user / per-machine roots, longest first so the most specific prefix wins
    // (LocalApplicationData sits under UserProfile, so it must be tested before it).
    private static readonly (string token, Environment.SpecialFolder folder)[] PathTokens =
    {
        ("%LOCALAPPDATA%", Environment.SpecialFolder.LocalApplicationData),
        ("%APPDATA%",      Environment.SpecialFolder.ApplicationData),
        ("%ProgramFiles(x86)%", Environment.SpecialFolder.ProgramFilesX86),
        ("%PROGRAMFILES%", Environment.SpecialFolder.ProgramFiles),
        ("%USERPROFILE%",  Environment.SpecialFolder.UserProfile),
    };

    /// <summary>Replaces this machine's user/program roots in an absolute path with portable
    /// tokens (e.g. C:\Users\me\AppData\Local\… → %LOCALAPPDATA%\…) so the same icon resolves
    /// under a different username on the importing machine.</summary>
    private static string? Tokenize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        foreach (var (token, folder) in PathTokens)
        {
            var root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root) &&
                path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return token + path.Substring(root.Length);
        }
        return path;
    }

    /// <summary>Reverse of <see cref="Tokenize"/>: expands portable tokens back to this
    /// machine's absolute paths on import.</summary>
    private static string? Expand(string? path)
        => string.IsNullOrEmpty(path) ? path : Environment.ExpandEnvironmentVariables(path);

    private static string UniqueName(FolderStore store, string name)
    {
        var existing = store.ListFolders().Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(name)) return name;
        int n = 2;
        while (existing.Contains($"{name} ({n})")) n++;
        return $"{name} ({n})";
    }

    private static (Dictionary<string, string> byFile, Dictionary<string, string> byName) BuildStartMenuIndex()
    {
        var byFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            IEnumerable<string> lnks;
            try { lnks = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var lnk in lnks)
            {
                string? target;
                try { target = ShellLink.ResolveTarget(lnk); } catch { continue; }
                if (target == null || !File.Exists(target)) continue;
                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var fn = Path.GetFileName(target);
                byFile.TryAdd(fn, target);
                byName.TryAdd(Path.GetFileNameWithoutExtension(lnk), target);
            }
        }
        return (byFile, byName);
    }
}
