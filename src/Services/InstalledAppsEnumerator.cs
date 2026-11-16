using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using static GlassFolders.NativeMethods;

namespace GlassFolders.Services;

/// <summary>
/// Builds the installed-apps list for the picker: Start Menu shortcuts (current user + all users)
/// and the shell:AppsFolder virtual folder (Store/UWP + legacy tiles). Read-only — it never writes
/// under the Start Menu/ProgramData, never runs or resolves a target, and never logs names/paths.
/// All shell/COM work runs on ONE dedicated background STA thread (see <see cref="Run"/>); COM
/// objects are released in finally and PIDLs are freed.
/// </summary>
public static class InstalledAppsEnumerator
{
    private const int IconSize = 32;   // small list glyphs; displayed at 24
    private const int PerRootCap = 5000;

    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MaxRecursionDepth = 8,
    };

    /// <summary>Enumerate names first, then fill icons — all on a fresh STA thread. Callbacks are
    /// raised from that thread; the caller marshals to the UI and may cancel via <paramref name="ct"/>.
    /// Icons are frozen before handoff. On a hard failure <paramref name="onFailed"/> is raised.</summary>
    public static void Run(
        Action<List<AppEntry>> onReady,
        Action<AppEntry, ImageSource> onIcon,
        Action onFailed,
        CancellationToken ct)
    {
        var t = new Thread(() =>
        {
            try
            {
                var all = new List<AppEntry>();
                foreach (var root in StartMenuRoots())
                {
                    if (ct.IsCancellationRequested) return;
                    all.AddRange(GatherStartMenu(root));
                }
                if (!ct.IsCancellationRequested)
                    all.AddRange(GatherAppsFolder(ct));
                if (ct.IsCancellationRequested) return;

                var merged = AppPickerRules.Merge(all)
                    .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                onReady(merged);

                foreach (var e in merged)
                {
                    if (ct.IsCancellationRequested) return;
                    var img = LoadIcon(e);
                    if (img != null) onIcon(e, img);
                }
            }
            catch
            {
                if (!ct.IsCancellationRequested) { try { onFailed(); } catch { } }
            }
        })
        { IsBackground = true, Name = "GlassFolders.AppEnum" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private static IEnumerable<string> StartMenuRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
    }

    /// <summary>Walk one Start Menu root for *.lnk only (read-only; no Resolve, no run, no hash).
    /// Skips our own folder shortcuts, Uninstall entries and broken LOCAL targets; keeps UNC targets
    /// without probing them. Public for --fixtest against a synthetic tree.</summary>
    public static List<AppEntry> GatherStartMenu(string? root)
    {
        var entries = new List<AppEntry>();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return entries;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*.lnk", WalkOptions); }
        catch { return entries; }

        int seen = 0;
        foreach (var lnk in files)
        {
            if (seen >= PerRootCap) break;
            // EnumerateFiles' "*.lnk" can match longer extensions on some filesystems; require it exactly.
            if (!Path.GetExtension(lnk).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
            seen++;

            string name = Path.GetFileNameWithoutExtension(lnk);
            if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;

            string? target = ShellLink.ResolveTarget(lnk);   // GetPath-based; never calls Resolve
            string? args = ShellLink.ReadArguments(lnk);
            string? aumid = ShellLink.ReadAppUserModelId(lnk);
            if (string.IsNullOrEmpty(aumid)) aumid = null;

            if (IsOurFolderLnk(aumid, target, args)) continue;
            // Unreadable/broken shortcut (no target and no app id) — nothing to identify or launch.
            if (string.IsNullOrEmpty(target) && aumid == null) continue;

            if (!string.IsNullOrEmpty(target) && !IsUncOrNetwork(target))
            {
                // Local target that no longer exists -> broken shortcut, skip it. Never probe UNC.
                bool missing;
                try { missing = !File.Exists(target); } catch { missing = false; }
                if (missing) continue;
            }

            entries.Add(new AppEntry(name, AppSource.StartMenu, aumid, target, args, lnk));
        }
        return entries;
    }

    private static List<AppEntry> GatherAppsFolder(CancellationToken ct)
    {
        var entries = new List<AppEntry>();
        object? appsObj = null;
        try
        {
            var iid = IID_IShellItem;
            SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iid, out appsObj);
        }
        catch { return entries; }

        var apps = (IShellItem)appsObj!;
        object? enumObj = null;
        try
        {
            apps.BindToHandler(IntPtr.Zero, BHID_EnumItems, IID_IEnumShellItems, out enumObj);
            var en = (IEnumShellItems)enumObj;
            while (!ct.IsCancellationRequested && en.Next(1, out var item, out var fetched) == 0 && fetched == 1)
            {
                try
                {
                    string aumid = DisplayName(item, SIGDN_PARENTRELATIVEPARSING);
                    if (string.IsNullOrEmpty(aumid)) continue;  // no identity -> can't add reliably
                    if (aumid.StartsWith("Avromiep.GlassFolders.Folder.", StringComparison.OrdinalIgnoreCase)) continue;
                    // Only Store/UWP apps come from AppsFolder. Classic desktop tiles here just
                    // duplicate the Start Menu shortcut (different identity, so they wouldn't dedupe) —
                    // and the Start Menu copy is the one that adds a real working .lnk with its icon.
                    if (!AppPickerRules.IsPackagedAumid(aumid)) continue;
                    string name = DisplayName(item, SIGDN_NORMALDISPLAY);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                    entries.Add(new AppEntry(name, AppSource.AppsFolder, aumid));
                }
                finally { if (item != null) Marshal.ReleaseComObject(item); }
            }
        }
        catch { /* best effort — whatever we gathered stands */ }
        finally
        {
            if (enumObj != null) Marshal.ReleaseComObject(enumObj);
            Marshal.ReleaseComObject(apps);
        }
        return entries;
    }

    private static string DisplayName(IShellItem item, uint sigdn)
    {
        IntPtr p = IntPtr.Zero;
        try
        {
            item.GetDisplayName(sigdn, out p);
            return (p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null) ?? "";
        }
        catch { return ""; }
        finally { if (p != IntPtr.Zero) CoTaskMemFree(p); }
    }

    private static ImageSource? LoadIcon(AppEntry e)
    {
        try
        {
            // Fast single extraction (no under-fill probing) — fine for small list glyphs.
            string? src = e.Source == AppSource.StartMenu ? e.LnkPath
                        : e.Aumid != null ? "shell:AppsFolder\\" + e.Aumid
                        : null;
            if (string.IsNullOrEmpty(src)) return null;
            using var bmp = IconExtractor.GetIconFast(src, IconSize);
            if (bmp != null) return ImageHelper.ToImageSource(bmp);   // frozen -> thread-safe
        }
        catch { }
        return null;
    }

    /// <summary>A UNC path (we must not probe it with File.Exists per the read-only rule).</summary>
    private static bool IsUncOrNetwork(string target) =>
        target.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>One of our own folder shortcuts, from values already read (no extra .lnk load).</summary>
    private static bool IsOurFolderLnk(string? aumid, string? target, string? args)
    {
        if (aumid != null && aumid.StartsWith("Avromiep.GlassFolders.Folder.", StringComparison.OrdinalIgnoreCase))
            return true;
        var exe = target != null ? Path.GetFileName(target) : null;
        if (exe != null &&
            (exe.Equals("GFOpen.exe", StringComparison.OrdinalIgnoreCase) ||
             exe.Equals("GlassFolders.exe", StringComparison.OrdinalIgnoreCase) ||
             exe.Equals("LiquidFolders.exe", StringComparison.OrdinalIgnoreCase)))
            return (args ?? "").Contains("--open", StringComparison.OrdinalIgnoreCase);
        return false;
    }
}
