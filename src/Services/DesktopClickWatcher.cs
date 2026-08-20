using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using static GlassFolders.NativeMethods;

namespace GlassFolders.Services;

/// <summary>
/// Global low-level mouse hook that turns a *single* left-click on one of our folder's
/// desktop icons into an "open" — while leaving the rest of the desktop on its normal
/// double-click behavior. Windows has no per-icon single-click setting, so we watch clicks
/// and use UI Automation to identify what was clicked.
///
/// The hook runs on its OWN dedicated thread with a message loop. A low-level hook is
/// serviced by its installing thread's message pump — if it lived on the UI thread, any
/// heavy UI work (e.g. rendering the glass manager) would stall it and lag the *entire
/// system's* mouse. A drag (moved far / held long) is ignored so icons can still be moved.
/// </summary>
public sealed class DesktopClickWatcher : IDisposable
{
    private readonly Func<string, bool> _isOurFolder;
    private readonly Action<string> _open;
    private readonly LowLevelMouseProc _proc; // kept alive to avoid GC of the callback
    private IntPtr _hook;

    private Thread? _thread;
    private uint _threadId;

    private POINT _downPt;
    private uint _downTime;
    private readonly Dictionary<string, long> _lastOpen = new(StringComparer.OrdinalIgnoreCase);

    public DesktopClickWatcher(Func<string, bool> isOurFolder, Action<string> open)
    {
        _isOurFolder = isOurFolder;
        _open = open;
        _proc = HookProc;
    }

    public void Install()
    {
        _thread = new Thread(HookThread)
        {
            IsBackground = true,
            Name = "GlassFolders.MouseHook",
        };
        _thread.Start();
    }

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();
        try
        {
            using var proc = Process.GetCurrentProcess();
            using var mod = proc.MainModule!;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(mod.ModuleName), 0);
        }
        catch { return; /* single-click is a nice-to-have; double-click always works */ }

        // Pump messages so the hook is always serviced on THIS thread, never the UI thread.
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == WM_LBUTTONDOWN)
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                _downPt = s.pt;
                _downTime = s.time;
            }
            else if (msg == WM_LBUTTONUP)
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int dx = s.pt.x - _downPt.x, dy = s.pt.y - _downPt.y;
                uint dt = s.time - _downTime;
                // Treat as a click only if it barely moved and was quick (not a drag).
                if (dx * dx + dy * dy <= 36 && dt < 700)
                    EvaluateClick(s.pt);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void EvaluateClick(POINT pt)
    {
        // UI Automation FromPoint can be slow; never run it on the hook thread.
        Task.Run(() =>
        {
            try
            {
                var p = new System.Windows.Point(pt.x, pt.y);
                var el = AutomationElement.FromPoint(p);
                // On a busy system FromPoint can momentarily return nothing; one quick retry.
                if (el is null) { System.Threading.Thread.Sleep(30); el = AutomationElement.FromPoint(p); }
                if (el is null) return;
                if (el.Current.ControlType != ControlType.ListItem) return; // desktop icons are list items

                string name = el.Current.Name;
                if (string.IsNullOrEmpty(name) || !_isOurFolder(name)) return;

                // De-dupe only the same physical double-click; keep it short so a deliberate
                // re-click right after closing the folder still opens it.
                long now = Environment.TickCount64;
                lock (_lastOpen)
                {
                    if (_lastOpen.TryGetValue(name, out var last) && now - last < 300) return;
                    _lastOpen[name] = now;
                }
                _open(name);
            }
            catch { }
        });
    }

    public void Dispose()
    {
        // Ask the hook thread's message loop to exit; it unhooks on the way out.
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }
}
