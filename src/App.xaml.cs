using System.IO;
using System.Windows;
using GlassFolders.Models;
using GlassFolders.Services;
using GlassFolders.Views;
using WinForms = System.Windows.Forms;

namespace GlassFolders;

public partial class App : Application
{
    public const string AppName = "Glass Folders";
    public const string AppVersion = "0.3.0";

    private SingleInstance _single = null!;
    private FolderStore _store = null!;
    private WinForms.NotifyIcon? _tray;
    private ManagerWindow? _manager;
    private DesktopClickWatcher? _clickWatcher;
    private readonly Dictionary<string, ExpandedPanelWindow> _openPanels = new(StringComparer.OrdinalIgnoreCase);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Stable app identity so our own windows group under one taskbar id, distinct from the
        // per-folder shortcut ids (which need to launch, not activate this process).
        try { NativeMethods.SetCurrentProcessExplicitAppUserModelID("Avromiep.GlassFolders"); } catch { }

        // Safety net: a stray UI-thread exception must never take down the whole tray app
        // (folders would silently stop working). Log it and keep running.
        DispatcherUnhandledException += (_, ex) =>
        {
            LogCrash("Dispatcher", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            LogCrash("AppDomain", ex.ExceptionObject as Exception);

        Diag.LogSession(AppVersion);

        if (e.Args.Length >= 1 && e.Args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.Run(e.Args.Length >= 2 ? e.Args[1] : ".");
            Shutdown();
            return;
        }

        if (e.Args.Length >= 1 && e.Args[0].Equals("--updatetest", StringComparison.OrdinalIgnoreCase))
        {
            var dir = e.Args.Length >= 2 ? e.Args[1] : System.IO.Path.GetTempPath();
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var sb = new System.Text.StringBuilder();
                try
                {
                    var r = await Services.UpdateService.CheckAsync("0.0.1"); // force "update available"
                    sb.AppendLine($"status={r.Status} latest={r.LatestVersion} setupUrl={r.SetupUrl}");
                    if (r.SetupUrl != null)
                    {
                        var p = new Progress<int>(_ => { });
                        var f = await Services.AppUpdater.DownloadAsync(r.SetupUrl, p, System.Threading.CancellationToken.None);
                        sb.AppendLine($"downloaded={f} bytes={new System.IO.FileInfo(f).Length}");
                    }
                }
                catch (Exception ex) { sb.AppendLine("ERROR: " + ex); }
                try { File.WriteAllText(System.IO.Path.Combine(dir, "updatetest.txt"), sb.ToString()); } catch { }
                Dispatcher.Invoke(Shutdown);
            });
            return;
        }

        if (e.Args.Length >= 1 && e.Args[0].Equals("--linktest", StringComparison.OrdinalIgnoreCase))
        {
            var dir = e.Args.Length >= 2 ? e.Args[1] : System.IO.Path.GetTempPath();
            var lnk = System.IO.Path.Combine(dir, "aumidtest.lnk");
            const string want = "Avromiep.GlassFolders.Folder.TestXYZ";
            try
            {
                Services.ShellLink.Create(lnk, targetPath: Environment.ProcessPath ?? "C:\\Windows\\explorer.exe",
                    arguments: "--open \"Test\"", appUserModelId: want);
                var got = Services.ShellLink.ReadAppUserModelId(lnk);
                File.WriteAllText(System.IO.Path.Combine(dir, "linktest.txt"),
                    $"want={want}\ngot={got}\nresult={(got == want ? "PASS" : "FAIL")}");
            }
            catch (Exception ex) { File.WriteAllText(System.IO.Path.Combine(dir, "linktest.txt"), "ERROR: " + ex); }
            Shutdown();
            return;
        }

        if (e.Args.Length >= 1 && e.Args[0].Equals("--makeicon", StringComparison.OrdinalIgnoreCase))
        {
            var outPath = e.Args.Length >= 2 ? e.Args[1] : "app.ico";
            try { Services.IconComposer.BuildAppIcon(System.IO.Path.GetFullPath(outPath)); } catch { }
            Shutdown();
            return;
        }

        if (e.Args.Length >= 1 && e.Args[0].Equals("--shot", StringComparison.OrdinalIgnoreCase))
        {
            StartShot(e.Args.Length >= 2 ? e.Args[1] : "panel.png");
            return; // shot mode drives its own shutdown
        }

        if (e.Args.Length >= 1 && e.Args[0].Equals("--shotmgr", StringComparison.OrdinalIgnoreCase))
        {
            StartShotManager(e.Args.Length >= 2 ? e.Args[1] : "manager.png");
            return;
        }

        if (e.Args.Length >= 1 && e.Args[0].Equals("--opensettings", StringComparison.OrdinalIgnoreCase))
        {
            _store = new FolderStore();
            new ManagerWindow(_store).Show();
            new Views.SettingsWindow(_store, () => { }).Show();
            return; // stays alive for external screenshot
        }

        if (e.Args.Length >= 1 && e.Args[0].Equals("--shots", StringComparison.OrdinalIgnoreCase))
        {
            StartShots(e.Args.Length >= 2 ? e.Args[1] : ".");
            return; // clean marketing screenshots; drives its own shutdown
        }

        var (openName, filesToAdd) = ParseArgs(e.Args);

        _single = new SingleInstance();
        if (!_single.TryAcquire())
        {
            // Another instance owns the UI; forward this request and exit.
            SingleInstance.SendToPrimary(BuildMessage(openName, filesToAdd));
            Shutdown();
            return;
        }

        _store = new FolderStore();
        _single.StartServer(OnIpcMessage);
        KeepResident();         // don't get paged to disk while idle → first open "after a while" stays fast
        SetupTray();
        MaybeRegenerateIcons(); // only after an update — avoids the desktop "flash" on every login
        EnsureManagerShortcut(); // desktop launcher that opens the manager/settings window
        StartupManager.ApplyDefault(); // run at sign-in so the tray app is always there (single-click works)
        PrewarmIcons();          // warm the tile-icon cache so the first open is snappy too
        CheckForUpdatesInBackground(); // passive check → tray balloon if a newer version exists

        // Single-click on one of our desktop folder icons opens it (rest of desktop unchanged).
        _clickWatcher = new DesktopClickWatcher(
            isOurFolder: name => _store.FindByName(name) != null,
            // BeginInvoke (not Invoke) so a busy UI thread can never block the watcher's worker.
            // Pass the click point so the panel opens on the folder icon's display.
            open: (name, x, y) => Dispatcher.BeginInvoke(() => Dispatch(name, new List<string>(), new System.Drawing.Point(x, y))),
            // Close an open panel when a click lands outside it (doesn't rely on window focus).
            onClick: (x, y) => Dispatcher.BeginInvoke(() => CloseIfOutside(x, y)));
        _clickWatcher.Install();

        Dispatch(openName, filesToAdd);
    }

    /// <summary>
    /// Opens the panel over a throwaway demo folder, waits for it to render/composite,
    /// captures the on-screen window (glass over the real wallpaper) to a PNG, then exits.
    /// </summary>
    private void StartShot(string outPng)
    {
      try
      {
        var demoApps = new[]
        {
            @"C:\Windows\System32\notepad.exe",
            @"C:\Windows\explorer.exe",
            @"C:\Windows\System32\cmd.exe",
            @"C:\Windows\System32\control.exe",
            @"C:\Windows\System32\Taskmgr.exe",
            @"C:\Windows\regedit.exe",
            @"C:\Windows\System32\charmap.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        }.Where(File.Exists).ToArray();

        var tempRoot = Path.Combine(Path.GetTempPath(), "gf-shot-" + Guid.NewGuid().ToString("N"));
        DesktopIntegration.DesktopDirOverride = Path.Combine(tempRoot, "desktop");
        Directory.CreateDirectory(DesktopIntegration.DesktopDirOverride);

        var store = new FolderStore(tempRoot);
        var folder = store.CreateFolder("Glass Demo");
        foreach (var a in demoApps) store.AddShortcut(folder, a);

        File.WriteAllText(outPng + ".count.log",
            $"demoApps={demoApps.Length} items={folder.Items.Count} pages={folder.PageCount}");

        var win = new ExpandedPanelWindow(store, folder) { SuppressAutoClose = true };
        win.Show();

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { CaptureWindow(win, outPng); }
            catch (Exception ex) { File.WriteAllText(outPng + ".log", "capture: " + ex); }
            win.Close();
            try { Directory.Delete(tempRoot, true); } catch { }
            Shutdown();
        };
        timer.Start();
      }
      catch (Exception ex)
      {
        File.WriteAllText(outPng + ".start.log", "start: " + ex);
        Shutdown();
      }
    }

    private void StartShotManager(string outPng)
    {
        try
        {
            var demoApps = new[]
            {
                @"C:\Windows\System32\notepad.exe", @"C:\Windows\explorer.exe",
                @"C:\Windows\System32\cmd.exe", @"C:\Windows\System32\control.exe",
                @"C:\Windows\System32\Taskmgr.exe", @"C:\Windows\regedit.exe",
                @"C:\Windows\System32\charmap.exe", @"C:\Windows\System32\mspaint.exe",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            }.Where(File.Exists).ToArray();

            var tempRoot = Path.Combine(Path.GetTempPath(), "gf-mgr-" + Guid.NewGuid().ToString("N"));
            DesktopIntegration.DesktopDirOverride = Path.Combine(tempRoot, "desktop");
            Directory.CreateDirectory(DesktopIntegration.DesktopDirOverride);
            var store = new FolderStore(tempRoot);

            foreach (var (name, take) in new[] { ("Utilities", 8), ("Creative", 3), ("Web", 2) })
            {
                var f = store.CreateFolder(name);
                foreach (var a in demoApps.Take(take)) store.AddShortcut(f, a);
                store.RegenerateAndPublish(f);
            }

            var win = new ManagerWindow(store);
            win.Show();

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { CaptureWindow(win, outPng); }
                catch (Exception ex) { File.WriteAllText(outPng + ".log", ex.ToString()); }
                win.Close();
                try { Directory.Delete(tempRoot, true); } catch { }
                Shutdown();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            File.WriteAllText(outPng + ".start.log", ex.ToString());
            Shutdown();
        }
    }

    /// <summary>
    /// Produces clean marketing screenshots: stages a full-screen stock-wallpaper backdrop
    /// (so no personal desktop icons appear) with demo folders of stock Microsoft apps, then
    /// captures the manager and an open folder panel over it.
    /// </summary>
    private async void StartShots(string outDir)
    {
        try
        {
            Directory.CreateDirectory(outDir);

            var stock = new (string name, string path)[]
            {
                ("Notepad",       @"C:\Windows\System32\notepad.exe"),
                ("Paint",         @"C:\Windows\System32\mspaint.exe"),
                ("File Explorer", @"C:\Windows\explorer.exe"),
                ("Command Prompt",@"C:\Windows\System32\cmd.exe"),
                ("Control Panel", @"C:\Windows\System32\control.exe"),
                ("Task Manager",  @"C:\Windows\System32\Taskmgr.exe"),
                ("Registry Editor",@"C:\Windows\regedit.exe"),
                ("Character Map", @"C:\Windows\System32\charmap.exe"),
                ("Snipping Tool", @"C:\Windows\System32\SnippingTool.exe"),
                ("Edge",          @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
            }.Where(a => File.Exists(a.path)).ToArray();

            var tempRoot = Path.Combine(Path.GetTempPath(), "gf-shots-" + Guid.NewGuid().ToString("N"));
            DesktopIntegration.DesktopDirOverride = Path.Combine(tempRoot, "desktop");
            Directory.CreateDirectory(DesktopIntegration.DesktopDirOverride);
            var store = new FolderStore(tempRoot);

            var tools = store.CreateFolder("Essentials");
            foreach (var a in stock.Take(9)) store.AddResolved(tools, a.path, a.name);
            store.RegenerateAndPublish(tools);

            var media = store.CreateFolder("Everyday");
            foreach (var a in stock.Take(4)) store.AddResolved(media, a.path, a.name);
            store.RegenerateAndPublish(media);

            var backdrop = MakeBackdrop();
            backdrop.Show();
            await Task.Delay(200);

            // Manager over the clean backdrop.
            var mgr = new ManagerWindow(store);
            mgr.Show();
            mgr.Left = 70; mgr.Top = 46; // keep it on the primary monitor, over the backdrop
            mgr.Activate();
            await Task.Delay(1000);
            CaptureWindowClean(mgr, Path.Combine(outDir, "manager.png"));
            mgr.Close();
            await Task.Delay(200);

            // Open folder panel over the backdrop (put the cursor on primary so it opens there).
            var pb = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            System.Windows.Forms.Cursor.Position =
                new System.Drawing.Point(pb.X + pb.Width / 2, pb.Y + pb.Height / 2);

            var panel = new ExpandedPanelWindow(store, store.FindByName("Essentials")!)
            { SuppressAutoClose = true };
            panel.Show();
            panel.Activate();
            await Task.Delay(1000);
            CaptureWindowClean(panel, Path.Combine(outDir, "panel.png"));
            panel.Close();

            backdrop.Close();
            try { Directory.Delete(tempRoot, true); } catch { }
            Shutdown();
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(outDir, "shots-error.log"), ex.ToString()); } catch { }
            Shutdown();
        }
    }

    /// <summary>Captures the whole primary screen (reliable) and crops to the window's rect.</summary>
    private static void CaptureWindowClean(Window win, string outPng)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
        NativeMethods.GetWindowRect(hwnd, out var r);
        var pb = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;

        using var full = new System.Drawing.Bitmap(pb.Width, pb.Height);
        using (var g = System.Drawing.Graphics.FromImage(full))
            g.CopyFromScreen(pb.X, pb.Y, 0, 0, pb.Size);

        int cx = Math.Clamp(r.Left - pb.X, 0, pb.Width - 1);
        int cy = Math.Clamp(r.Top - pb.Y, 0, pb.Height - 1);
        int cw = Math.Clamp(r.Right - r.Left, 1, pb.Width - cx);
        int ch = Math.Clamp(r.Bottom - r.Top, 1, pb.Height - cy);

        using var crop = new System.Drawing.Bitmap(cw, ch);
        using (var g2 = System.Drawing.Graphics.FromImage(crop))
            g2.DrawImage(full, new System.Drawing.Rectangle(0, 0, cw, ch),
                new System.Drawing.Rectangle(cx, cy, cw, ch), System.Drawing.GraphicsUnit.Pixel);
        crop.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static Window MakeBackdrop()
    {
        System.Windows.Media.ImageSource? wall = null;
        foreach (var p in new[]
        {
            @"C:\Windows\Web\Wallpaper\Windows\img0.jpg",
            @"C:\Windows\Web\Wallpaper\Windows\img19.jpg",
        })
        {
            if (File.Exists(p))
            {
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit(); img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(p); img.EndInit(); img.Freeze();
                wall = img; break;
            }
        }

        var content = wall != null
            ? (System.Windows.Media.Brush)new System.Windows.Media.ImageBrush(wall)
                { Stretch = System.Windows.Media.Stretch.UniformToFill }
            : new System.Windows.Media.LinearGradientBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1B3A6B"),
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0B1220"),
                45);

        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = content,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 8, Top = 8, Width = 200, Height = 150,
            Topmost = false,
        };
        w.Loaded += (_, _) => w.WindowState = WindowState.Maximized; // fills the primary monitor
        return w;
    }

    private static void CaptureWindow(Window win, string outPng)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
        NativeMethods.GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        File.WriteAllText(outPng + ".log", $"hwnd={hwnd} rect=({r.Left},{r.Top},{r.Right},{r.Bottom}) size={w}x{h}");
        if (w <= 0 || h <= 0) return;

        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
        bmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>Ensures the desktop launcher shortcut for the manager exists.</summary>
    private void EnsureManagerShortcut()
    {
        try
        {
            // Extract the current embedded app icon to a VERSION-STAMPED file and point the launcher
            // there. Pointing at the exe (a path that never changes) let Explorer keep showing a
            // stale cached icon after the art changed; a new icon-location path each version forces
            // Explorer to re-read it. The bytes come from the embedded /Assets/app.ico, so it can't
            // go stale.
            var fallbackExe = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "GlassFolders.exe");
            string launcherIcon = fallbackExe;
            try
            {
                System.IO.Directory.CreateDirectory(_store.IconsPath);
                var versioned = System.IO.Path.Combine(_store.IconsPath, $"app-{AppVersion}.ico");
                if (!File.Exists(versioned))
                {
                    var res = GetResourceStream(new Uri("/Assets/app.ico", UriKind.Relative));
                    if (res != null)
                    {
                        using var fs = File.Create(versioned);
                        res.Stream.CopyTo(fs);
                        res.Stream.Dispose();
                    }
                }
                if (File.Exists(versioned)) launcherIcon = versioned;
                // Drop older icon files (the stale gear "app.ico" and previous versions).
                foreach (var old in System.IO.Directory.EnumerateFiles(_store.IconsPath, "app*.ico"))
                    if (!string.Equals(old, launcherIcon, StringComparison.OrdinalIgnoreCase))
                        try { File.Delete(old); } catch { }
            }
            catch { }
            DesktopIntegration.PublishManagerShortcut(AppName, launcherIcon);

            // Remove the pre-rename launcher ("Liquid Folders.lnk") if it's still on the desktop.
            try
            {
                var legacy = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Liquid Folders.lnk");
                if (File.Exists(legacy)) File.Delete(legacy);
            }
            catch { }
        }
        catch { }
    }

    /// <summary>
    /// Ask Windows to keep a minimum working set resident so it doesn't page us out to disk while
    /// the app sits idle in the tray. Without this, the first folder open after a long idle stretch
    /// has to fault all our pages (WPF, JIT'd code, icons) back in — the "slow the first time, fast
    /// after" behavior. It only sets a floor; the OS reclaims the rest normally.
    /// </summary>
    private static void KeepResident()
    {
        try
        {
            NativeMethods.SetProcessWorkingSetSizeEx(
                NativeMethods.GetCurrentProcess(),
                (IntPtr)(80L * 1024 * 1024),    // keep ~80 MB resident (the hot open path)
                (IntPtr)(1024L * 1024 * 1024),  // max is not enforced (flag below), just a ceiling
                NativeMethods.QUOTA_LIMITS_HARDWS_MIN_ENABLE | NativeMethods.QUOTA_LIMITS_HARDWS_MAX_DISABLE);
        }
        catch { }
    }

    /// <summary>Passive startup update check; on a newer release, nudge via a tray balloon.</summary>
    private void CheckForUpdatesInBackground()
    {
        Task.Run(async () =>
        {
            try
            {
                var r = await UpdateService.CheckAsync(AppVersion);
                if (r.Status == UpdateStatus.UpdateAvailable)
                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            _tray?.ShowBalloonTip(6000, "Update available",
                                $"Glass Folders {r.LatestVersion} is ready. Open Glass Folders → Settings to install.",
                                WinForms.ToolTipIcon.Info);
                        }
                        catch { }
                    });
            }
            catch { }
        });
    }

    /// <summary>Warms the tile-icon cache (size 64) for every folder's first page, off the UI
    /// thread, so opening a folder doesn't pay for shell icon extraction on the hot path.</summary>
    private void PrewarmIcons()
    {
        Task.Run(() =>
        {
            try
            {
                foreach (var f in _store.ListFolders())
                    ImageHelper.Prewarm(f.FirstPagePaths(), 64);
            }
            catch { }
        });
    }

    /// <summary>
    /// Regenerate desktop icons only when the app version changed since the last run (i.e. right
    /// after an update, where an extractor/compositor fix might need to take effect). On ordinary
    /// logins this is skipped, so re-publishing shortcuts doesn't flash the desktop icons.
    /// </summary>
    private void MaybeRegenerateIcons()
    {
        try
        {
            var marker = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GlassFolders", "last-version");
            var last = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
            if (last == AppVersion) return;             // same version -> icons are already current
            RegenerateAllIcons();
            try { File.WriteAllText(marker, AppVersion); } catch { }
        }
        catch { RegenerateAllIcons(); }
    }

    /// <summary>Rebuilds every folder's composite icon so desktop icons stay in sync.</summary>
    private void RegenerateAllIcons()
    {
        Task.Run(() =>
        {
            foreach (var f in _store.ListFolders())
                try { _store.RegenerateAndPublish(f); } catch { }
        });
    }

    /// <summary>
    /// Parses "--open &lt;name&gt;" plus any trailing file paths. Dropping files onto the
    /// desktop shortcut appends those paths to the shortcut's own arguments, which is how
    /// "drag apps onto the closed folder" reaches us.
    /// </summary>
    private static (string? openName, List<string> files) ParseArgs(string[] args)
    {
        string? openName = null;
        var files = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--open", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                openName = args[++i];
            else if (File.Exists(args[i]) || Directory.Exists(args[i]))
                files.Add(args[i]);
        }
        return (openName, files);
    }

    private static string BuildMessage(string? openName, List<string> files)
    {
        if (openName == null) return "MANAGER";
        // Line-delimited: OPEN \n name \n file \n file ...
        var lines = new List<string> { "OPEN", openName };
        lines.AddRange(files);
        return string.Join("\n", lines);
    }

    private void OnIpcMessage(string message)
    {
        var lines = message.Split('\n');
        Dispatcher.Invoke(() =>
        {
            if (lines.Length >= 2 && lines[0] == "OPEN")
                Dispatch(lines[1], lines.Skip(2).Where(l => l.Length > 0).ToList());
            else
                ShowManager();
        });
    }

    private void Dispatch(string? openName, List<string> filesToAdd, System.Drawing.Point? anchor = null)
    {
        if (openName == null) { ShowManager(); return; }

        var folder = _store.FindByName(openName);
        if (folder == null) { ShowManager(); return; }

        if (filesToAdd.Count > 0)
        {
            // Dropping apps onto the closed folder icon: add them and refresh the icon, but
            // DON'T open the panel (the folder shouldn't "launch" just because you dropped on it).
            foreach (var f in filesToAdd)
                try { _store.AddShortcut(folder, f); } catch { }
            _store.RegenerateAndPublish(folder);
            return;
        }

        OpenPanel(folder, forceReopen: false, anchor);
    }

    private static void LogCrash(string source, Exception? ex)
    {
        Diag.Log($"UNHANDLED ({source}): {ex}");
        try
        {
            File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "glassfolders-crash.log"),
                $"{DateTime.Now:u} [{source}] {ex}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Closes any open panel whose window rectangle doesn't contain the click point.</summary>
    private void CloseIfOutside(int x, int y)
    {
        if (_openPanels.Count == 0) return;
        foreach (var win in _openPanels.Values.ToList())
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                if (hwnd == IntPtr.Zero) continue;
                NativeMethods.GetWindowRect(hwnd, out var r);
                bool inside = x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
                Diag.Log($"closeIfOutside ({x},{y}) rect=({r.Left},{r.Top},{r.Right},{r.Bottom}) inside={inside}");
                if (!inside) win.Close();
            }
            catch { }
        }
    }

    /// <summary>Opens the folder's panel, or focuses it if already open (prevents duplicates
    /// from a single-click + double-click both firing).</summary>
    private void OpenPanel(FolderModel folder, bool forceReopen, System.Drawing.Point? anchor = null)
    {
        if (_openPanels.TryGetValue(folder.Name, out var existing))
        {
            Diag.Log($"openPanel '{folder.Name}' existing loaded={existing.IsLoaded} visible={existing.IsVisible} closing={existing.IsClosing} forceReopen={forceReopen}");
            // Only reuse a genuinely open/visible panel; otherwise it's a stale reference
            // (a panel that closed, is mid-close, or never finished opening) — drop it and open fresh.
            if (!forceReopen && existing.IsLoaded && existing.IsVisible && !existing.IsClosing)
            {
                // Already open (e.g. the second click of a double-click just launched a fresh
                // instance that forwarded here) — keep it open and bring it back to front rather
                // than letting the focus churn dismiss it.
                try { existing.ReassertOpen(); } catch { }
                return;
            }
            try { existing.Close(); } catch { }
            _openPanels.Remove(folder.Name);
        }

        Diag.Log($"openPanel create '{folder.Name}' (open panels was {_openPanels.Count})");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var win = new ExpandedPanelWindow(_store, folder) { AnchorPoint = anchor };
        long tCtor = sw.ElapsedMilliseconds;
        _openPanels[folder.Name] = win;
        win.Closed += (_, _) =>
        {
            if (_openPanels.TryGetValue(folder.Name, out var w) && ReferenceEquals(w, win))
                _openPanels.Remove(folder.Name);
            Diag.Log($"panel '{folder.Name}' Closed event");
        };
        win.Show();
        Diag.Log($"openPanel show '{folder.Name}' ctor={tCtor}ms show={sw.ElapsedMilliseconds - tCtor}ms");
    }

    private void ShowManager()
    {
        if (_manager == null)
        {
            _manager = new ManagerWindow(_store);
            _manager.Closed += (_, _) => _manager = null;
            _manager.Show();
        }
        else
        {
            if (_manager.WindowState == WindowState.Minimized)
                _manager.WindowState = WindowState.Normal;
            _manager.Activate();
        }
    }

    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon() ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = AppName,
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add($"Open {AppName}", null, (_, _) => Dispatcher.Invoke(ShowManager));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowManager);
    }

    /// <summary>The frost app icon, taken from the exe's own embedded icon (ApplicationIcon).</summary>
    private static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location;
            return exe != null ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null;
        }
        catch { return null; }
    }

    private void ExitApp()
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _clickWatcher?.Dispose();
        _single.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnExit(e);
    }
}
