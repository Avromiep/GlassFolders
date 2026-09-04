using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GlassFolders.Models;
using GlassFolders.Services;

namespace GlassFolders.Views;

public partial class ExpandedPanelWindow : Window
{
    private readonly FolderStore _store;
    private FolderModel _folder = null!;   // set per-open in OpenFor (one window is reused)
    private int _pageIndex;
    private int _blurFactor = 11;
    private bool _closeArmed;

    // ---- Reuse via continuous composition (fast + reliably flash-free) ----
    //
    // One window is created ONCE at login and kept alive, always shown and ON-SCREEN, but made
    // invisible by animating its CONTENT opacity (Root.Opacity) to 0 rather than hiding the window.
    // Because the window is never hidden/cloaked, DWM composes it continuously, so its invisible
    // (Opacity=0) state is always reliably painted. Showing a folder is then just animating
    // Root.Opacity 0->1 — a smooth ramp, never a "reveal" of a retained surface. That is the key:
    // cloak/uncloak (and Hide/Show, and move-on-screen) all expose whatever frame the surface last
    // retained, which under load is the PREVIOUS folder at full opacity = the flash. An opacity
    // animation on an always-composed window cannot do that. While invisible the window is made
    // click-through so desktop clicks beneath it pass through.
    private bool _realized;
    private bool _open;
    private System.Windows.Threading.DispatcherTimer? _heartbeat;

    // Live-capture bookkeeping: the screen rect the current frost background was grabbed for, and
    // when the panel was last hidden. On a rapid same-spot switch (just hidden), the old panel may
    // not have cleared the screen yet, so we reuse that last (clean) grab instead of re-grabbing
    // and catching the panel itself; when opening from a settled idle state the window is reliably
    // transparent, so we grab fresh (current desktop).
    private System.Drawing.Rectangle _bgRect;
    private int _lastHideMs = -100000;

    /// <summary>True while the panel is actually shown to the user.</summary>
    public bool IsOpen => _open;

    /// <summary>Name of the folder currently loaded (for the App's "same folder" reuse check).</summary>
    public string? CurrentFolderName => _folder?.Name;

    private IntPtr Hwnd => new System.Windows.Interop.WindowInteropHelper(this).Handle;

    /// <summary>
    /// Make the (always-on-screen) window click-through + non-activating while it's invisible, so
    /// the transparent panel never eats a desktop click; clear it when the panel is interactive.
    /// </summary>
    private void SetClickThrough(bool on)
    {
        try
        {
            int ex = NativeMethods.GetWindowLong(Hwnd, NativeMethods.GWL_EXSTYLE);
            if (on) ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE;
            else ex &= ~(NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE);
            NativeMethods.SetWindowLong(Hwnd, NativeMethods.GWL_EXSTYLE, ex);
        }
        catch { }
    }

    /// <summary>Screen point of the clicked folder icon (device px). The panel opens on this
    /// point's display, so it never lands on a different monitor than the folder.</summary>
    public System.Drawing.Point? AnchorPoint { get; set; }

    private static readonly Brush DotActive = new SolidColorBrush(Color.FromArgb(0xDD, 0x20, 0x24, 0x28));
    private static readonly Brush DotInactive = new SolidColorBrush(Color.FromArgb(0x55, 0x20, 0x24, 0x28));

    public ExpandedPanelWindow(FolderStore store)
    {
        _store = store;
        ShowActivated = false;   // the one-time warm-up Show must not steal focus
        InitializeComponent();

        DotActive.Freeze();
        DotInactive.Freeze();

        ItemsGrid.PreviewMouseLeftButtonDown += ItemsGrid_PreviewMouseLeftButtonDown;
        ItemsGrid.PreviewMouseMove += ItemsGrid_PreviewMouseMove;

        // Regaining focus cancels a pending debounced close (the transient blip passed).
        Activated += (_, _) => _deactivateTimer?.Stop();
    }

    /// <summary>
    /// Create the HWND and pay the layered-window's one-time render cost NOW, so later opens are
    /// just an opacity animation. The window stays shown on-screen but fully transparent
    /// (Root.Opacity=0) and click-through. Safe to call repeatedly; only the first call does work.
    /// </summary>
    public void EnsureWarm()
    {
        if (_realized) return;
        _realized = true;
        try
        {
            // Park it invisibly on the primary monitor (repositioned per open). It must be ON a
            // real monitor — not off-screen — so DWM composes it continuously and its Opacity=0
            // state stays reliably painted (that's what makes the reveal flash-free).
            var wa = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            Left = wa.Left + 80; Top = wa.Top + 80;
            Root.Opacity = 0;              // fully transparent = invisible + click-through
            Show();                        // pays the slow layered-window creation ONCE
            SetClickThrough(true);
            StartHeartbeat();
            Services.Diag.Log("panel warmed (shown transparent, ready)");
        }
        catch (Exception ex) { Services.Diag.Log($"panel warm failed: {ex.Message}"); }
    }

    /// <summary>
    /// Show the panel for a folder. The window is already on-screen and composed; we set its
    /// content while transparent, then animate the CONTENT opacity in. No hide/cloak/reveal is
    /// involved, so a stale previous frame can never leak through.
    /// </summary>
    // The trail of parent folders when the user has navigated into nested subfolders, so the
    // title-bar back arrow can step back out one level at a time.
    private readonly List<string> _navStack = new();

    /// <summary>Open a folder as a fresh top-level view — resets any nested-navigation trail.</summary>
    public void OpenFor(FolderModel folder, System.Drawing.Point? anchor)
    {
        _navStack.Clear();
        OpenForInternal(folder, anchor, popIn: false);
    }

    /// <param name="popIn">When switching a folder that's already open (e.g. navigating into a
    /// nested subfolder), do a subtle scale pop so it reads as "going in" rather than a hard cut.</param>
    private void OpenForInternal(FolderModel folder, System.Drawing.Point? anchor, bool popIn)
    {
        EnsureWarm();
        _deactivateTimer?.Stop();
        bool wasVisible = _open;

        // Force the content invisible BEFORE we swap it in, and clear any held animation (a finished
        // WPF animation pins the property, so a direct set would be ignored). On an always-composed
        // window this Opacity=0 paints reliably within a frame — no retained full-opacity frame.
        Root.BeginAnimation(OpacityProperty, null);
        OpenScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        OpenScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (wasVisible)
        {
            // Switching folders while already visible (a taskbar switch, or nested in/out): keep it
            // fully shown and swap the content in place — old folder -> new folder in one composited
            // frame, NO animation. That's the smooth, instant nested navigation the user wants
            // (an animation here read as the folder "opening" again).
            Root.Opacity = 1; OpenScale.ScaleX = OpenScale.ScaleY = 1;
        }
        else
        {
            // Opening from hidden: start invisible, then fade the content in once it's set.
            Root.Opacity = 0; OpenScale.ScaleX = OpenScale.ScaleY = 0.97;
        }

        _folder = folder;
        AnchorPoint = anchor;
        _pageIndex = 0;
        TitleText.Text = folder.Name;
        EndEdit();                         // in case a rename box was left open on a prior folder
        ApplyFrostiness(folder.Frostiness);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        RenderPage();
        long tRender = sw.ElapsedMilliseconds;

        // Finalize size (SizeToContent settles here) then position. Nested navigation (popIn) keeps
        // the panel exactly where it is — drilling in/out of subfolders shouldn't move the window
        // (that was the "jump"); it's a pure in-place content swap, and the reused blur still matches.
        UpdateLayout();
        if (!popIn) PositionNearCursor();
        UpdateLayout();

        // Grab + blur the live desktop behind the panel. Skipped entirely on a true in-place switch
        // (window opaque). Otherwise CaptureAndBlurBackground decides between a fresh grab (settled
        // idle -> window transparent -> clean, current) and reusing the last grab (rapid same-spot
        // switch -> old panel may still be on screen -> avoid self-capture).
        if (!wasVisible) CaptureAndBlurBackground();
        long tCapture = sw.ElapsedMilliseconds - tRender;

        SetClickThrough(false);            // interactive now
        _open = true;

        // Opening the folder (from hidden) plays the open animation. An in-place switch (taskbar
        // switch or nested in/out) has no animation — the content just swaps.
        if (!wasVisible) PlayOpenAnimation();

        Activate();
        Focus();
        try { NativeMethods.BringWindowToTop(Hwnd); } catch { }
        try { NativeMethods.SetForegroundWindow(Hwnd); } catch { }
        ArmCloseAfter(450);
        UpdateBackButton();
        Services.Diag.Log($"panel '{folder.Name}' open-prep render={tRender}ms capture={tCapture}ms total={sw.ElapsedMilliseconds}ms switch={wasVisible} pos=({Left:0},{Top:0})");
    }

    /// <summary>Show the title-bar back arrow only when we're inside a nested folder,
    /// and label it with the folder we'd return to (e.g. "Back to test").</summary>
    private void UpdateBackButton()
    {
        if (_navStack.Count > 0)
        {
            BackButton.Visibility = Visibility.Visible;
            BackButton.ToolTip = $"Back to {_navStack[^1]}";
        }
        else
        {
            BackButton.Visibility = Visibility.Collapsed;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_navStack.Count == 0) return;
        var parentName = _navStack[^1];
        _navStack.RemoveAt(_navStack.Count - 1);
        var parent = _store.FindByName(parentName);
        if (parent != null)
        {
            Services.Diag.Log($"panel back: '{_folder?.Name}' -> '{parent.Name}'");
            OpenForInternal(parent, AnchorPoint, popIn: true);
        }
        else UpdateBackButton();
    }

    /// <summary>Hide the panel but keep the window alive, on-screen and warm for the next open.</summary>
    public void HidePanel()
    {
        if (!_realized || !_open) return;
        _open = false;
        _closeArmed = false;
        _navStack.Clear();   // closing resets the nested-navigation trail
        _armTimer?.Stop();
        _deactivateTimer?.Stop();
        SetClickThrough(true);            // stop intercepting clicks immediately (fade is invisible to input)
        _lastHideMs = Environment.TickCount;

        // Close animation: fade + shrink the content out (mirrors the open). The window stays on
        // screen and composed, so this is just a content-opacity animation — no reveal, no flash.
        OpenScale.CenterX = ActualWidth / 2;
        OpenScale.CenterY = ActualHeight / 2;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var scale = new DoubleAnimation(1.0, 0.97, TimeSpan.FromMilliseconds(55)) { EasingFunction = ease };
        OpenScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        OpenScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        var fade = new DoubleAnimation(Root.Opacity, 0.0, TimeSpan.FromMilliseconds(55));
        fade.Completed += (_, _) => { if (!_open) { Root.BeginAnimation(OpacityProperty, null); Root.Opacity = 0; } };
        Root.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Keep our memory pages hot and the desktop cache current so opens stay fast.</summary>
    private void StartHeartbeat()
    {
        _heartbeat = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromSeconds(45) };
        _heartbeat.Tick += (_, _) =>
        {
            if (_open) return;   // no need while the user is looking at it
            try
            {
                NativeMethods.SetProcessWorkingSetSizeEx(
                    NativeMethods.GetCurrentProcess(),
                    (IntPtr)(80L * 1024 * 1024), (IntPtr)(1024L * 1024 * 1024),
                    NativeMethods.QUOTA_LIMITS_HARDWS_MIN_ENABLE | NativeMethods.QUOTA_LIMITS_HARDWS_MAX_DISABLE);
            }
            catch { }
        };
        _heartbeat.Start();
    }

    // ---- Drag an app OUT of the folder to remove it (drop it outside the glass) ----
    //
    // The app physically leaves the folder (dropped outside => removed). While dragging, the
    // source tile is hidden so its slot goes empty (iOS style) and a floating "ghost" — the app's
    // own icon with a red minus badge — rides under the cursor, replacing Windows' red no-drop
    // circle. So there's only ever one visible icon: the one under the cursor.

    private ShortcutItem? _dragOutItem;
    private Button? _dragOutButton;
    private Point _dragOutStart;
    private bool _dragOutActive;
    private Window? _dragGhost;
    private UIElement? _dragGhostBadge;

    private void ItemsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragOutButton = FindButton(e.OriginalSource as DependencyObject);
        _dragOutItem = _dragOutButton?.Tag as ShortcutItem;
        _dragOutStart = e.GetPosition(this);
        _dragOutActive = false;
    }

    private void ItemsGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragOutActive || _dragOutItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragOutStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragOutStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragOutActive = true;
        var item = _dragOutItem;
        var sourceButton = _dragOutButton;

        // Don't let the panel self-close while a topmost ghost briefly appears mid-drag.
        bool priorSuppress = SuppressAutoClose;
        SuppressAutoClose = true;

        // Empty the source slot and lift the icon onto the cursor.
        if (sourceButton != null) sourceButton.Visibility = Visibility.Hidden;
        ShowGhost(item);
        ItemsGrid.GiveFeedback += Drag_GiveFeedback;
        ItemsGrid.QueryContinueDrag += Drag_QueryContinueDrag;

        try { DragDrop.DoDragDrop(ItemsGrid, item, DragDropEffects.Move); } catch { }

        ItemsGrid.GiveFeedback -= Drag_GiveFeedback;
        ItemsGrid.QueryContinueDrag -= Drag_QueryContinueDrag;
        CloseGhost();

        // Dropped outside the glass panel -> remove it from the folder (iOS "drag out").
        // Dropped back inside -> restore the tile (nothing removed).
        bool removed = false;
        try
        {
            NativeMethods.GetCursorPos(out var c);
            var tl = Frost.PointToScreen(new Point(0, 0));
            var src = PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            double fw = Frost.ActualWidth * sx, fh = Frost.ActualHeight * sy;
            bool inside = c.x >= tl.X && c.x <= tl.X + fw && c.y >= tl.Y && c.y <= tl.Y + fh;
            if (!inside) { RemoveItem(item); removed = true; }
        }
        catch { }

        if (!removed && sourceButton != null) sourceButton.Visibility = Visibility.Visible;

        SuppressAutoClose = priorSuppress;
        _dragOutActive = false;
        _dragOutItem = null;
        _dragOutButton = null;
    }

    private void Drag_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        // Hide the OS drag cursor (incl. the red no-drop circle) — the ghost is the pointer now.
        e.UseDefaultCursors = false;
        Mouse.SetCursor(Cursors.None);
        e.Handled = true;
    }

    private void Drag_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (_dragGhost == null || !NativeMethods.GetCursorPos(out var c)) return;
        MoveGhost(c.x, c.y);
        // The minus badge only means "let go here to remove" — show it once the cursor leaves
        // the glass. Inside the folder the drag reads as a plain rearrange, so keep it hidden.
        if (_dragGhostBadge != null)
            _dragGhostBadge.Visibility = CursorInsideFrost(c.x, c.y)
                ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>True if the given screen point (device px) is over the glass panel.</summary>
    private bool CursorInsideFrost(int px, int py)
    {
        try
        {
            var tl = Frost.PointToScreen(new Point(0, 0));
            var src = PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            double fw = Frost.ActualWidth * sx, fh = Frost.ActualHeight * sy;
            return px >= tl.X && px <= tl.X + fw && py >= tl.Y && py <= tl.Y + fh;
        }
        catch { return false; }
    }

    /// <summary>Creates the floating icon-with-minus-badge that follows the cursor during a drag-out.</summary>
    private void ShowGhost(ShortcutItem item)
    {
        try
        {
            var icon = new System.Windows.Controls.Image
            {
                Source = ImageHelper.LoadIcon(item.LnkPath, 64),
                Width = 50,
                Height = 50,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 11,
                    ShadowDepth = 2,
                    Opacity = 0.35,
                },
            };

            var badge = new Grid
            {
                Width = 22,
                Height = 22,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(1, 1, 0, 0),
                // Starts hidden: the drag begins inside the folder. It appears when the cursor
                // crosses outside the glass (see Drag_QueryContinueDrag).
                Visibility = Visibility.Collapsed,
            };
            badge.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x3A, 0x35)),
                Stroke = Brushes.White,
                StrokeThickness = 1.6,
            });
            badge.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 10,
                Height = 2.6,
                RadiusX = 1.3,
                RadiusY = 1.3,
                Fill = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var root = new Grid { Width = 62, Height = 62 };
            root.Children.Add(icon);
            root.Children.Add(badge);
            _dragGhostBadge = badge;

            _dragGhost = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                IsHitTestVisible = false,
                SizeToContent = SizeToContent.Manual,
                Width = 62,
                Height = 62,
                Content = root,
            };
            _dragGhost.Show();

            var hwnd = new System.Windows.Interop.WindowInteropHelper(_dragGhost).Handle;
            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);

            if (NativeMethods.GetCursorPos(out var c)) MoveGhost(c.x, c.y);
        }
        catch { _dragGhost = null; }
    }

    private void MoveGhost(int cursorX, int cursorY)
    {
        if (_dragGhost == null) return;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_dragGhost).Handle;
            var src = PresentationSource.FromVisual(_dragGhost);
            double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            int pxW = (int)Math.Round(_dragGhost.Width * scale);
            int pxH = (int)Math.Round(_dragGhost.Height * scale);
            // Centre the icon on the cursor, lifted slightly up so the badge stays clear of it.
            int x = cursorX - pxW / 2;
            int y = cursorY - pxH / 2 - (int)Math.Round(6 * scale);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
        catch { }
    }

    private void CloseGhost()
    {
        _dragGhostBadge = null;
        if (_dragGhost == null) return;
        try { _dragGhost.Close(); } catch { }
        _dragGhost = null;
    }

    private static Button? FindButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is Button b && b.Tag is ShortcutItem) return b;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>Maps 0..100 frostiness to tint opacity and blur strength.</summary>
    private void ApplyFrostiness(int frostiness)
    {
        int v = Math.Clamp(frostiness, 0, 100);
        // Uniform white veil (solid brush) using the shared scale: 55 == old ~20 (light, faded).
        TintLayer.Opacity = FolderModel.TintOpacity(v);
        double blur = v <= FolderModel.DefaultFrostiness
            ? 4 + (v / (double)FolderModel.DefaultFrostiness) * (16 - 4)
            : 16 + ((v - FolderModel.DefaultFrostiness) / (double)(100 - FolderModel.DefaultFrostiness)) * (34 - 16);
        _blurFactor = (int)Math.Round(blur);
    }

    /// <summary>
    /// Grabs the LIVE screen region behind the glass panel, blurs it, and paints it as the panel
    /// background — the real desktop right now, so the frost always matches what's behind it. This
    /// only runs while the window is fully transparent (Root.Opacity=0), so the grab reads straight
    /// through it and never captures the panel's own pixels. The rounded Border clips the result.
    /// </summary>
    private void CaptureAndBlurBackground()
    {
        try
        {
            var topLeft = Frost.PointToScreen(new Point(0, 0)); // device pixels
            var src = PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            int w = (int)Math.Round(Frost.ActualWidth * sx);
            int h = (int)Math.Round(Frost.ActualHeight * sx);
            if (w <= 0 || h <= 0) return;

            // Rapid same-spot switch (just hidden): the old panel may still be composited on screen,
            // so a fresh grab would catch it. The desktop behind is unchanged over such a short gap,
            // so keep the last (clean) grab. A settled open (>150ms since hide) means the window has
            // reliably composited to transparent, so we fall through and grab the CURRENT desktop.
            var newRect = new System.Drawing.Rectangle((int)topLeft.X, (int)topLeft.Y, w, h);
            bool recentHide = Environment.TickCount - _lastHideMs < 150;
            bool sameSpot = !_bgRect.IsEmpty
                && Math.Abs(newRect.X - _bgRect.X) < 4 && Math.Abs(newRect.Y - _bgRect.Y) < 4
                && Math.Abs(newRect.Width - _bgRect.Width) < 4 && Math.Abs(newRect.Height - _bgRect.Height) < 4;
            if (recentHide && sameSpot && Frost.Background != null) return;

            // Capture a padded region AROUND the panel so the blur near the edges has real
            // neighbours to mix with — otherwise the edges look less frosted than the centre.
            int pad = (int)Math.Round(70 * sx);
            int cx = (int)topLeft.X - pad, cy = (int)topLeft.Y - pad;
            int cw = w + pad * 2, ch = h + pad * 2;

            using var shot = new System.Drawing.Bitmap(cw, ch, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(shot))
                g.CopyFromScreen(cx, cy, 0, 0, new System.Drawing.Size(cw, ch));

            using var blurredBig = DownUpBlur(shot, _blurFactor);
            using var cropped = new System.Drawing.Bitmap(w, h);
            using (var g2 = System.Drawing.Graphics.FromImage(cropped))
                g2.DrawImage(blurredBig, new System.Drawing.Rectangle(0, 0, w, h),
                    new System.Drawing.Rectangle(pad, pad, w, h), System.Drawing.GraphicsUnit.Pixel);

            Frost.Background = new ImageBrush(FastBitmapSource(cropped)) { Stretch = Stretch.Fill };
            _bgRect = newRect;
        }
        catch { /* leave the transparent background; tint + rim still read as glass */ }
    }

    /// <summary>Fast GDI bitmap -> WPF BitmapSource without a PNG encode (opaque images only).</summary>
    private static System.Windows.Media.Imaging.BitmapSource FastBitmapSource(System.Drawing.Bitmap bmp)
    {
        IntPtr h = bmp.GetHbitmap();
        try
        {
            var s = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                h, IntPtr.Zero, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            s.Freeze();
            return s;
        }
        finally { NativeMethods.DeleteObject(h); }
    }

    /// <summary>
    /// Smooth, uniform blur: repeatedly halve (proper box averaging, no undersampling) down
    /// to ~1/factor, then scale back up. Even from center to edges.
    /// </summary>
    private static System.Drawing.Bitmap DownUpBlur(System.Drawing.Bitmap srcBmp, int factor)
    {
        int targetW = Math.Max(2, srcBmp.Width / Math.Max(2, factor));

        System.Drawing.Bitmap cur = srcBmp;
        bool ownCur = false;
        while (cur.Width / 2 > targetW)
        {
            int nw = Math.Max(2, cur.Width / 2), nh = Math.Max(2, cur.Height / 2);
            var next = new System.Drawing.Bitmap(nw, nh);
            using (var g = System.Drawing.Graphics.FromImage(next))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(cur, 0, 0, nw, nh);
            }
            if (ownCur) cur.Dispose();
            cur = next;
            ownCur = true;
        }

        int fw = targetW;
        int fh = Math.Max(2, (int)Math.Round((double)targetW / srcBmp.Width * srcBmp.Height));
        var small = new System.Drawing.Bitmap(fw, fh);
        using (var g = System.Drawing.Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(cur, 0, 0, fw, fh);
        }
        if (ownCur) cur.Dispose();

        var big = new System.Drawing.Bitmap(srcBmp.Width, srcBmp.Height);
        using (var gg = System.Drawing.Graphics.FromImage(big))
        {
            gg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            gg.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            gg.DrawImage(small, new System.Drawing.Rectangle(0, 0, big.Width, big.Height));
        }
        small.Dispose();
        return big;
    }

    // ---- Visuals ----

    private void PositionNearCursor()
    {
        // Anchor to the clicked folder icon's screen (falls back to the cursor for the
        // double-click path). This guarantees the panel opens on the folder's own display.
        var probe = AnchorPoint ?? System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(probe);
        var wa = screen.WorkingArea;

        // WPF units vs device pixels: approximate with the window's DPI scale.
        var src = PresentationSource.FromVisual(this);
        double sx = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        double sy = src?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

        double waL = wa.Left * sx, waT = wa.Top * sy;
        double waW = wa.Width * sx, waH = wa.Height * sy;

        // Place at the chosen 3x3 grid anchor on the screen the icon was clicked from.
        // The window carries a transparent shadow margin (Root Margin) around the visible
        // glass, so offset by it to make edge anchors actually hug the screen edges.
        int idx = Math.Clamp(_folder.PanelPosition, 0, 8);
        int col = idx % 3, row = idx / 3;
        const double shadow = 30; // must match the Root grid Margin in XAML
        const double gap = 8;     // visible gap between the glass and the screen edge
        double waR = waL + waW, waB = waT + waH;

        Left = col == 0 ? waL + gap - shadow
             : col == 2 ? waR - gap + shadow - ActualWidth
             : waL + (waW - ActualWidth) / 2;
        Top = row == 0 ? waT + gap - shadow
            : row == 2 ? waB - gap + shadow - ActualHeight
            : waT + (waH - ActualHeight) / 2;
    }

    private void PlayOpenAnimation()
    {
        // Snappy pop-in: a subtle, fast scale + quick fade so the folder appears near-instantly
        // (the previous durations made the no-flash open feel sluggish).
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = new DoubleAnimation(0.97, 1.0, TimeSpan.FromMilliseconds(45)) { EasingFunction = ease };
        OpenScale.CenterX = ActualWidth / 2;
        OpenScale.CenterY = ActualHeight / 2;
        OpenScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        OpenScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        // Fade the CONTENT in (not the window opacity — the window stays opaque; the reveal is the
        // DWM uncloak). Starting from Root.Opacity=0 guarantees no full-opacity first-frame flash.
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(38)));
    }

    // ---- Paging ----

    private void RenderPage()
    {
        _pageIndex = Math.Clamp(_pageIndex, 0, _folder.PageCount - 1);
        ItemsGrid.Items.Clear();

        foreach (var item in _folder.Page(_pageIndex))
            ItemsGrid.Items.Add(BuildTile(item));

        // Pad empty cells at a real tile's size so the panel keeps a full 3x3 footprint even
        // when the folder (or last page) isn't full — an empty folder still opens full size.
        int shown = _folder.Page(_pageIndex).Count();
        for (int i = shown; i < FolderModel.PageSize; i++)
            ItemsGrid.Items.Add(new Border { Width = 120, Height = 104 });

        bool multi = _folder.PageCount > 1;
        PrevButton.IsEnabled = multi && _pageIndex > 0;
        NextButton.IsEnabled = multi && _pageIndex < _folder.PageCount - 1;

        Dots.Items.Clear();
        if (multi)
            for (int i = 0; i < _folder.PageCount; i++)
                Dots.Items.Add(i == _pageIndex ? DotActive : DotInactive);
    }

    private UIElement BuildTile(ShortcutItem item)
    {
        var image = new System.Windows.Controls.Image
        {
            Width = 60,
            Height = 60,
            Source = IconForItem(item),
            Stretch = Stretch.Uniform,
        };
        var label = new TextBlock
        {
            Text = item.DisplayName,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x1D)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 118,
            Margin = new Thickness(0, 5, 0, 0),
            MaxHeight = 30,
            TextTrimming = TextTrimming.CharacterEllipsis,
            // White halo keeps labels legible over clear glass on any wallpaper.
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.White,
                BlurRadius = 6,
                ShadowDepth = 0,
                Opacity = 0.85,
            },
        };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(image);
        stack.Children.Add(label);

        var btn = new Button
        {
            Style = (Style)Resources["AppTile"],
            Content = stack,
            ToolTip = item.DisplayName,
            Tag = item, // used by drag-out to identify which app is being dragged
        };
        btn.Click += (_, _) => Launch(item);

        var remove = new MenuItem { Header = "Remove from folder" };
        remove.Click += (_, _) => RemoveItem(item);
        btn.ContextMenu = new ContextMenu();
        btn.ContextMenu.Items.Add(remove);

        return btn;
    }

    private void ChangePage(int delta)
    {
        int next = Math.Clamp(_pageIndex + delta, 0, _folder.PageCount - 1);
        if (next == _pageIndex) return;
        _pageIndex = next;
        RenderPage();
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => ChangePage(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => ChangePage(+1);

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
        => ChangePage(e.Delta > 0 ? -1 : +1);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: HidePanel(); break;
            case Key.Left: ChangePage(-1); break;
            case Key.Right: ChangePage(+1); break;
        }
    }

    // ---- Rename by clicking the title ----

    private void TitleText_Click(object sender, MouseButtonEventArgs e)
    {
        TitleEdit.Text = _folder.Name;
        TitleText.Visibility = Visibility.Collapsed;
        TitleEdit.Visibility = Visibility.Visible;
        TitleEdit.Focus();
        TitleEdit.SelectAll();
    }

    private void TitleEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitRename(); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndEdit(); e.Handled = true; } // cancel; don't close panel
    }

    private void TitleEdit_LostFocus(object sender, RoutedEventArgs e) => CommitRename();

    private void EndEdit()
    {
        TitleEdit.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
    }

    private void CommitRename()
    {
        if (TitleEdit.Visibility != Visibility.Visible) return;
        var newName = TitleEdit.Text?.Trim();
        EndEdit();
        if (string.IsNullOrWhiteSpace(newName) || newName == _folder.Name) return;
        try
        {
            _store.RenameFolder(_folder, newName);
            var reloaded = _store.FindByName(FolderStore.Sanitize(newName));
            if (reloaded != null) _folder = reloaded; // dir moved, so items' paths changed
            TitleText.Text = _folder.Name;
            RenderPage();
        }
        catch { }
    }

    // ---- Actions ----

    private void Launch(ShortcutItem item)
    {
        // A folder nested inside this one navigates IN PLACE (this reused window switches to it),
        // instead of launching its shortcut — which would close this panel then reopen the other
        // one (the close/reopen flicker). Only if it isn't one of our folders do we launch normally.
        if (TryOpenNestedFolder(item)) return;

        try
        {
            Services.Diag.Log($"panel launch '{item.DisplayName}' -> {item.LnkPath}");
            Process.Start(new ProcessStartInfo(item.LnkPath) { UseShellExecute = true });
            HidePanel();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't launch {item.DisplayName}.\n\n{ex.Message}",
                "Glass Folders", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>If the tile is one of our folders, switch this panel into that folder in place and
    /// return true — instead of launching it (which would close/reopen).</summary>
    private bool TryOpenNestedFolder(ShortcutItem item)
    {
        try
        {
            var name = NestedFolderNameOf(item.LnkPath);
            if (name == null) return false;
            var folder = _store.FindByName(name);
            if (folder == null) return false;
            if (string.Equals(folder.Name, _folder?.Name, StringComparison.OrdinalIgnoreCase))
                return true; // it's this same folder — ignore, don't relaunch

            Services.Diag.Log($"panel nest: '{_folder?.Name}' -> '{folder.Name}' (in-place)");
            if (_folder != null) _navStack.Add(_folder.Name); // remember the parent for Back
            OpenForInternal(folder, AnchorPoint, popIn: true);
            return true;
        }
        catch { return false; }
    }

    // lnk path -> the Glass Folders folder it opens (null = an ordinary shortcut). Cached by
    // path+mtime so we read each .lnk at most once (folder detection touches COM).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long mtime, string? folder)> _nestedCache = new();

    /// <summary>The folder a tile's .lnk opens if it's one of ours (targets our exe with an
    /// `--open "&lt;name&gt;"` arg), else null. Result is cached per .lnk.</summary>
    private static string? NestedFolderNameOf(string lnkPath)
    {
        try
        {
            long mtime = System.IO.File.GetLastWriteTimeUtc(lnkPath).Ticks;
            if (_nestedCache.TryGetValue(lnkPath, out var c) && c.mtime == mtime) return c.folder;

            string? folder = null;
            var name = ParseOpenName(ShellLink.ReadArguments(lnkPath));
            if (name != null)
            {
                var target = System.IO.Path.GetFileName(ShellLink.ResolveTarget(lnkPath) ?? "");
                if (target.Equals("GFOpen.exe", StringComparison.OrdinalIgnoreCase) ||
                    target.Equals("GlassFolders.exe", StringComparison.OrdinalIgnoreCase))
                    folder = name;
            }
            _nestedCache[lnkPath] = (mtime, folder);
            return folder;
        }
        catch { return null; }
    }

    /// <summary>Tile icon: for a nested folder, render its LIVE closed-folder composite (so it
    /// matches the desktop icon and never goes stale when composites are regenerated); otherwise
    /// the shortcut's own icon.</summary>
    private System.Windows.Media.ImageSource? IconForItem(ShortcutItem item)
    {
        var name = NestedFolderNameOf(item.LnkPath);
        if (name != null && _store.FindByName(name) is FolderModel nf)
        {
            try { return ImageHelper.ToImageSource(IconComposer.RenderPreview(nf.FirstPagePaths(), 64)); }
            catch { }
        }
        return ImageHelper.LoadIcon(item.LnkPath, 64);
    }

    /// <summary>Extracts the folder name from a `--open "Name"` (or `--open Name`) argument string.</summary>
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
        return rest.Length == 0 ? null : rest;
    }

    private void RemoveItem(ShortcutItem item)
    {
        _store.RemoveShortcut(_folder, item);
        _store.RegenerateAndPublish(_folder);   // closed icon reflects first page
        if (_pageIndex >= _folder.PageCount) _pageIndex = _folder.PageCount - 1;
        RenderPage();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        foreach (var f in files)
        {
            try { _store.AddShortcut(_folder, f); } catch { }
        }
        _store.RegenerateAndPublish(_folder);
        RenderPage();
    }

    /// <summary>Test/screenshot mode: keep the panel open when it loses focus.</summary>
    public bool SuppressAutoClose { get; set; }

    private System.Windows.Threading.DispatcherTimer? _armTimer;
    private System.Windows.Threading.DispatcherTimer? _deactivateTimer;

    /// <summary>(Re)start the opening grace period during which a Deactivated is ignored.</summary>
    private void ArmCloseAfter(int ms)
    {
        _closeArmed = false;
        _armTimer?.Stop();
        _armTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _armTimer.Tick += (_, _) => { _armTimer?.Stop(); _closeArmed = true; };
        _armTimer.Start();
    }

    /// <summary>
    /// The user tapped the folder again while it's open (e.g. a double-click, whose second click
    /// launches a fresh instance that steals focus). Keep it open: cancel any pending close, extend
    /// the grace period so the focus churn can't dismiss it, and bring it back to front.
    /// </summary>
    public void ReassertOpen()
    {
        _deactivateTimer?.Stop();
        ArmCloseAfter(600);
        try
        {
            Activate();
            Focus();
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            NativeMethods.BringWindowToTop(h);
            NativeMethods.SetForegroundWindow(h);
        }
        catch { }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (SuppressAutoClose || !_closeArmed || !_open) return;
        Services.Diag.Log($"panel '{_folder.Name}' Deactivated armed={_closeArmed}");

        // Debounce: a transient focus blip — launching a second instance from a double-click, a
        // toast/notification, a background window flashing up — shouldn't dismiss the folder. Only
        // hide if we're still not the active window a beat later. A genuine app-switch stays gone;
        // clicking elsewhere is handled separately by the click-outside watcher.
        _deactivateTimer?.Stop();
        _deactivateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _deactivateTimer.Tick += (_, _) =>
        {
            _deactivateTimer?.Stop();
            if (!IsActive && !SuppressAutoClose && _open) HidePanel();
        };
        _deactivateTimer.Start();
    }
}
