using System.Threading;
using System.Windows.Automation;
using static GlassFolders.NativeMethods;

namespace GlassFolders.Services;

/// <summary>
/// Turns a *single* left-click on one of our folder's desktop icons into an "open", while
/// leaving the rest of the desktop on its normal double-click behavior.
///
/// It POLLS the mouse-button state on a background thread rather than installing a global
/// mouse hook. A low-level hook sits in the path of every mouse event, so under load (e.g.
/// while a launched app is starting) it starves and lags the entire system's cursor. Polling
/// never touches the input stream, so it can't add any latency. A drag (moved far / held
/// long) is ignored so icons can still be repositioned.
/// </summary>
public sealed class DesktopClickWatcher : IDisposable
{
    private readonly Func<string, bool> _isOurFolder;
    private readonly Action<string, int, int> _open;
    private readonly Action<int, int>? _onClick;

    private Thread? _thread;
    private volatile bool _stop;
    private readonly Dictionary<string, long> _lastOpen = new(StringComparer.OrdinalIgnoreCase);

    // ---- Taskbar-pin fast path ----
    // A pinned folder normally opens by launching the tiny GFOpen forwarder (~150ms). But this
    // already-running watcher sees every click, so it can open a pinned-folder click in-process —
    // exactly like a desktop icon click (~70ms), with no launch. We identify the pin via its UI
    // Automation button (AutomationId contains the folder's AppUserModelID) and hit-test the click
    // against a cache of pin rectangles, refreshed while the cursor is over the taskbar so the click
    // itself needs no (slow) UIA call. If the cache misses, GFOpen still opens it — graceful fallback.
    private volatile (string name, System.Windows.Rect rect)[] _pins = Array.Empty<(string, System.Windows.Rect)>();
    private long _pinsAt;
    private volatile bool _refreshingPins;
    private RECT _trayRect;
    private long _trayRectAt;

    public DesktopClickWatcher(Func<string, bool> isOurFolder, Action<string, int, int> open,
        Action<int, int>? onClick = null)
    {
        _isOurFolder = isOurFolder;
        _open = open;
        _onClick = onClick;
    }

    public void Install()
    {
        _thread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "GlassFolders.ClickWatcher",
            // High priority so a desktop redraw (e.g. after an app closes) can't starve the
            // poll and make it miss a click. The thread sleeps ~99% of the time, so this is cheap.
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();

        // Warm up UI Automation off-thread: the very first FromPoint in a process pays a one-time
        // COM init cost (can be 100ms+), which would otherwise land on the user's first click.
        // Also prime the taskbar location + folder-pin cache so the first pin click is fast too.
        Task.Run(() =>
        {
            try { _ = AutomationElement.FromPoint(new System.Windows.Point(0, 0)); } catch { }
            try { UpdateTrayRect(); _trayRectAt = Environment.TickCount64; } catch { }
            try { RefreshTaskbarPins(); _pinsAt = Environment.TickCount64; } catch { }
        });
    }

    private void PollLoop()
    {
        bool wasDown = false;
        POINT downPt = default;
        long downTime = 0;

        while (!_stop)
        {
            bool down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

            if (down && !wasDown)
            {
                GetCursorPos(out downPt);
                downTime = Environment.TickCount64;
            }
            else if (!down && wasDown)
            {
                GetCursorPos(out var upPt);
                long dt = Environment.TickCount64 - downTime;
                int dx = upPt.x - downPt.x, dy = upPt.y - downPt.y;
                // A click, not a drag: barely moved and quick.
                if (dx * dx + dy * dy <= 36 && dt < 700)
                {
                    // A pinned-folder taskbar click opens in-process (fast); otherwise treat it as a
                    // desktop/anywhere click (dismiss an open panel if outside it, then try icons).
                    if (!TryTaskbarPinClick(upPt))
                    {
                        _onClick?.Invoke(upPt.x, upPt.y);
                        EvaluateClick(upPt);
                    }
                }
            }
            else if (!down)
            {
                // Idle: while the cursor is over the taskbar, keep the folder-pin cache fresh so a
                // click is an instant hit-test rather than a ~60ms UIA enumeration.
                GetCursorPos(out var cur);
                MaybeRefreshTaskbarPins(cur);
            }

            wasDown = down;
            Thread.Sleep(8);
        }
    }

    private void EvaluateClick(POINT pt)
    {
        // UI Automation FromPoint can be slow; never run it on the polling thread.
        Task.Run(() =>
        {
            var _detectSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var p = new System.Windows.Point(pt.x, pt.y);
                string? matched = null;
                bool sawFolderList = false; // did FromPoint ever resolve the desktop icon List?

                // Right after an app closes, Explorer redraws the desktop ("flash") and for a
                // moment FromPoint returns null or the desktop container instead of the icon.
                // Retry briefly while the result is inconclusive; bail out immediately on a
                // decisive non-folder hit (another icon or a real window) so we don't waste time.
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    AutomationElement? el;
                    try { el = AutomationElement.FromPoint(p); } catch { el = null; }

                    if (el != null)
                    {
                        var ct = el.Current.ControlType;
                        var name = el.Current.Name;

                        if (ct == ControlType.ListItem)
                        {
                            if (!string.IsNullOrEmpty(name) && _isOurFolder(name))
                            {
                                matched = name;
                                Diag.Log($"click ({pt.x},{pt.y}) attempt {attempt}: MATCH '{name}'");
                                break;
                            }
                            Diag.Log($"click ({pt.x},{pt.y}) attempt {attempt}: ListItem '{name}' (not ours) -> stop");
                            return; // a different desktop icon — done
                        }

                        // Only the desktop background/containers are worth retrying through the
                        // redraw. A real window/control means the click wasn't on the desktop.
                        if (ct == ControlType.List) sawFolderList = true; // FromPoint sees the desktop list
                        bool desktopish = ct == ControlType.List || ct == ControlType.Pane
                            || ct == ControlType.Group || ct == ControlType.Custom;
                        if (!desktopish)
                        {
                            Diag.Log($"click ({pt.x},{pt.y}) attempt {attempt}: {ct.ProgrammaticName} '{name}' -> stop");
                            return;
                        }
                        Diag.Log($"click ({pt.x},{pt.y}) attempt {attempt}: {ct.ProgrammaticName} '{name}' -> retry");
                    }
                    else
                    {
                        Diag.Log($"click ({pt.x},{pt.y}) attempt {attempt}: null -> retry");
                    }

                    Thread.Sleep(55);
                }

                if (matched is null)
                {
                    // FromPoint didn't resolve to one of our folders. On some machines (notably
                    // multi-monitor + mixed DPI) UI Automation's FromPoint mis-maps the coordinates
                    // and returns a blank Pane even though the desktop icon list is perfectly normal
                    // (Win32 finds SysListView32/FolderView fine). Fall back to anchoring on that
                    // list by window handle (reliable) and hit-testing each icon's BoundingRectangle.
                    Diag.Log("  " + DescribeWindowAt(pt));
                    // Only bother with the Win32-anchored fallback when FromPoint couldn't even see
                    // the desktop List — i.e. it's mis-mapping. If it saw the list, the user just
                    // clicked empty space between icons, so there's nothing to open.
                    if (sawFolderList) return;
                    matched = TryMatchViaListView(pt);
                    if (matched is null) return;
                    Diag.Log($"  fallback MATCH '{matched}' via desktop listview");
                }

                long now = Environment.TickCount64;
                lock (_lastOpen)
                {
                    if (_lastOpen.TryGetValue(matched, out var last) && now - last < 300)
                    { Diag.Log($"  dedup-skip {matched}"); return; }
                    _lastOpen[matched] = now;
                }
                Diag.Log($"  -> open {matched} at ({pt.x},{pt.y}) detect={_detectSw.ElapsedMilliseconds}ms");
                _open(matched, pt.x, pt.y); // anchor the panel to the clicked icon's screen
            }
            catch { }
        });
    }

    /// <summary>
    /// Fallback icon detection that doesn't use UIA FromPoint: get the desktop icon list by the
    /// window handle Win32 reports under the point, then find the ListItem whose bounding box
    /// contains the (physical) click point. Logs enough to diagnose if the rects are in an
    /// unexpected coordinate space.
    /// </summary>
    private string? TryMatchViaListView(POINT pt)
    {
        try
        {
            var h = WindowFromPoint(pt);
            var cls = ClassOf(h);
            if (cls != "SysListView32")
            {
                Diag.Log($"  listview-fallback: not a desktop list (class='{cls}')");
                return null;
            }

            var listEl = AutomationElement.FromHandle(h);
            if (listEl == null) return null;
            var items = listEl.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            var p = new System.Windows.Point(pt.x, pt.y);

            foreach (AutomationElement it in items)
            {
                System.Windows.Rect r;
                string name;
                try { r = it.Current.BoundingRectangle; name = it.Current.Name; } catch { continue; }
                if (r.Contains(p))
                {
                    Diag.Log($"  listview-fallback: hit '{name}' rect=({r.X:0},{r.Y:0},{r.Width:0}x{r.Height:0}) of {items.Count} items");
                    return !string.IsNullOrEmpty(name) && _isOurFolder(name) ? name : null;
                }
            }

            // No rect contained the point — dump a few so we can see the coordinate space vs the click.
            Diag.Log($"  listview-fallback: no rect contained ({pt.x},{pt.y}) among {items.Count} items; sample:");
            int shown = 0;
            foreach (AutomationElement it in items)
            {
                if (shown++ >= 4) break;
                try { var r = it.Current.BoundingRectangle; Diag.Log($"    '{it.Current.Name}' rect=({r.X:0},{r.Y:0},{r.Width:0}x{r.Height:0})"); }
                catch { }
            }
            return null;
        }
        catch (Exception ex) { Diag.Log("  listview-fallback error: " + ex.Message); return null; }
    }

    /// <summary>Names the actual window under a screen point (and its top-level root), via Win32.
    /// Used to diagnose why a desktop click can't see our folder icons.</summary>
    private static string DescribeWindowAt(POINT pt)
    {
        try
        {
            var h = WindowFromPoint(pt);
            var root = GetAncestor(h, GA_ROOT);
            return $"under-cursor: hwnd={h} class='{ClassOf(h)}' title='{TextOf(h)}'"
                 + $" | root class='{ClassOf(root)}' title='{TextOf(root)}'";
        }
        catch (Exception ex) { return "under-cursor: describe failed: " + ex.Message; }
    }

    private static string ClassOf(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        var sb = new System.Text.StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string TextOf(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        var sb = new System.Text.StringBuilder(256);
        GetWindowTextW(h, sb, sb.Capacity);
        var s = sb.ToString();
        return s.Length > 60 ? s[..60] + "…" : s;
    }

    /// <summary>
    /// If the click landed on a pinned folder's taskbar button (per the cached rects), open it
    /// in-process and return true. The pin ALSO launches GFOpen, which forwards the same open — the
    /// app dedups it (already open -> reassert), so there's no double-open.
    /// </summary>
    private bool TryTaskbarPinClick(POINT pt)
    {
        try
        {
            var tr = _trayRect;
            bool onTaskbar = tr.Right > tr.Left && pt.x >= tr.Left && pt.x < tr.Right
                                                && pt.y >= tr.Top && pt.y < tr.Bottom;
            if (!onTaskbar) return false;

            var pins = _pins;
            if (pins.Length == 0) return false;
            var p = new System.Windows.Point(pt.x, pt.y);
            foreach (var (name, rect) in pins)
            {
                if (!rect.Contains(p) || !_isOurFolder(name)) continue;
                long now = Environment.TickCount64;
                lock (_lastOpen)
                {
                    if (_lastOpen.TryGetValue(name, out var last) && now - last < 300) return true;
                    _lastOpen[name] = now;
                }
                Diag.Log($"taskbar-pin click ({pt.x},{pt.y}) -> open '{name}' (in-process)");
                _open(name, pt.x, pt.y);
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>While the cursor is over the taskbar, refresh the folder-pin rect cache off-thread
    /// (throttled) so a pin click is an instant hit-test.</summary>
    private void MaybeRefreshTaskbarPins(POINT cur)
    {
        long now = Environment.TickCount64;
        if (now - _trayRectAt > 4000) { UpdateTrayRect(); _trayRectAt = now; }

        var tr = _trayRect;
        bool overTaskbar = tr.Right > tr.Left && cur.x >= tr.Left && cur.x < tr.Right
                                              && cur.y >= tr.Top && cur.y < tr.Bottom;
        if (!overTaskbar || _refreshingPins || now - _pinsAt < 600) return;

        _refreshingPins = true;
        Task.Run(() =>
        {
            try { RefreshTaskbarPins(); }
            catch { }
            finally { _pinsAt = Environment.TickCount64; _refreshingPins = false; }
        });
    }

    private void UpdateTrayRect()
    {
        try
        {
            var h = FindWindow("Shell_TrayWnd", null);
            if (h != IntPtr.Zero && GetWindowRect(h, out var r)) _trayRect = r;
        }
        catch { }
    }

    /// <summary>Enumerate the taskbar's buttons and cache the rects of any that are our folder pins
    /// (their UIA AutomationId embeds the folder's AppUserModelID, "…GlassFolders.Folder.&lt;slug&gt;").</summary>
    private void RefreshTaskbarPins()
    {
        var h = FindWindow("Shell_TrayWnd", null);
        if (h == IntPtr.Zero) return;
        var tray = AutomationElement.FromHandle(h);
        if (tray == null) return;

        var btns = tray.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        var list = new List<(string, System.Windows.Rect)>();
        foreach (AutomationElement b in btns)
        {
            string aid, name; System.Windows.Rect r;
            try { aid = b.Current.AutomationId; name = b.Current.Name; r = b.Current.BoundingRectangle; }
            catch { continue; }
            if (string.IsNullOrEmpty(aid) ||
                aid.IndexOf("GlassFolders.Folder.", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (string.IsNullOrEmpty(name) || r.Width <= 0 || r.Height <= 0) continue;
            list.Add((name, r));
        }
        _pins = list.ToArray();
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(500);
    }
}
