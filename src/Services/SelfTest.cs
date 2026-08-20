using System.Drawing.Imaging;
using System.IO;
using GlassFolders.Services;

namespace GlassFolders;

/// <summary>
/// Headless verification of the icon pipeline: extracts real app icons, composites the
/// frosted closed-folder icon, and writes PNG previews + a real .ico for inspection.
/// Invoked with:  GlassFolders.exe --selftest &lt;outputDir&gt;
/// </summary>
internal static class SelfTest
{
    public static void Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var log = new List<string>();

        string[] candidates =
        {
            @"C:\Windows\System32\notepad.exe",
            @"C:\Windows\explorer.exe",
            @"C:\Windows\System32\cmd.exe",
            @"C:\Windows\System32\control.exe",
            @"C:\Windows\System32\Taskmgr.exe",
            @"C:\Windows\regedit.exe",
            @"C:\Windows\System32\mmc.exe",
            @"C:\Windows\System32\SnippingTool.exe",
            @"C:\Windows\System32\mspaint.exe",
            @"C:\Windows\System32\charmap.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        };

        var existing = candidates.Where(File.Exists).ToList();
        log.Add($"Found {existing.Count} real targets:");
        foreach (var p in existing) log.Add("  " + p);

        // Single-icon extraction sanity check (256px).
        var first = IconExtractor.GetIcon(existing[0], 256);
        if (first != null)
        {
            first.Save(Path.Combine(outDir, "extract_256.png"), ImageFormat.Png);
            log.Add($"Extracted sample icon: {first.Width}x{first.Height}");
            first.Dispose();
        }
        else log.Add("!! extraction returned null");

        // Composite previews for a full page (9) and a partial page (4).
        foreach (var (count, tag) in new[] { (9, "full9"), (4, "partial4"), (1, "single") })
        {
            var paths = existing.Take(count).ToList();
            foreach (var size in new[] { 256, 48 })
            {
                using var bmp = IconComposer.RenderPreview(paths, size);
                var file = Path.Combine(outDir, $"composite_{tag}_{size}.png");
                bmp.Save(file, ImageFormat.Png);
                log.Add($"Wrote {file}");
            }
        }

        // A real multi-res .ico.
        var ico = Path.Combine(outDir, "composite.ico");
        bool ok = IconComposer.BuildIcon(existing.Take(9).ToList(), ico);
        log.Add($"BuildIcon -> {ok}, {ico} ({(File.Exists(ico) ? new FileInfo(ico).Length : 0)} bytes)");

        // ---- Lifecycle test: store + desktop shortcut, routed to a temp desktop ----
        log.Add("");
        log.Add("=== lifecycle ===");
        var root = Path.Combine(outDir, "store");
        var fakeDesktop = Path.Combine(outDir, "desktop");
        Directory.CreateDirectory(fakeDesktop);
        DesktopIntegration.DesktopDirOverride = fakeDesktop;

        var store = new FolderStore(root);
        var folder = store.CreateFolder("Test Folder");

        // Add 11 shortcuts to force a second page (9 per page).
        foreach (var p in existing.Take(9)) store.AddShortcut(folder, p);
        foreach (var p in existing.Take(2)) store.AddShortcut(folder, p); // duplicates -> unique names
        store.RegenerateAndPublish(folder);

        log.Add($"Items: {folder.Items.Count}, Pages: {folder.PageCount} (expect 11, 2)");

        var lnk = DesktopIntegration.DesktopLnkPathFor(folder.Name);
        log.Add($"Desktop .lnk exists: {File.Exists(lnk)} -> {lnk}");
        var target = ShellLink.ResolveTarget(lnk);
        log.Add($"Desktop .lnk target: {target} (expect the GlassFolders exe)");

        var reloaded = store.FindByName("Test Folder");
        log.Add($"Reload preserves order/count: {reloaded?.Items.Count} items");

        // Remove first item, ensure icon regenerates and pages recompute.
        store.RemoveShortcut(folder, folder.Items[0]);
        store.RegenerateAndPublish(folder);
        log.Add($"After remove: {folder.Items.Count} items, {folder.PageCount} pages (expect 10, 2)");

        store.DeleteFolder(folder);
        log.Add($"After delete, desktop .lnk gone: {!File.Exists(lnk)}");

        File.WriteAllLines(Path.Combine(outDir, "selftest.log"), log);
    }
}
