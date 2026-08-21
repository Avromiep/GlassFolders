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
    private readonly FolderModel _folder;
    private int _pageIndex;
    private int _blurFactor = 11;
    private bool _closeArmed;

    /// <summary>Screen point of the clicked folder icon (device px). The panel opens on this
    /// point's display, so it never lands on a different monitor than the folder.</summary>
    public System.Drawing.Point? AnchorPoint { get; set; }

    private static readonly Brush DotActive = new SolidColorBrush(Color.FromArgb(0xDD, 0x20, 0x24, 0x28));
    private static readonly Brush DotInactive = new SolidColorBrush(Color.FromArgb(0x55, 0x20, 0x24, 0x28));

    public ExpandedPanelWindow(FolderStore store, FolderModel folder)
    {
        _store = store;
        _folder = folder;
        InitializeComponent();

        DotActive.Freeze();
        DotInactive.Freeze();
        TitleText.Text = folder.Name;
        ApplyFrostiness(folder.Frostiness);

        ItemsGrid.PreviewMouseLeftButtonDown += ItemsGrid_PreviewMouseLeftButtonDown;
        ItemsGrid.PreviewMouseMove += ItemsGrid_PreviewMouseMove;

        Loaded += OnLoaded;
    }

    // ---- Drag an app OUT of the folder to remove it (drop it outside the glass) ----

    private ShortcutItem? _dragOutItem;
    private Point _dragOutStart;
    private bool _dragOutActive;

    private void ItemsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragOutItem = FindItem(e.OriginalSource as DependencyObject);
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
        try { DragDrop.DoDragDrop(ItemsGrid, item, DragDropEffects.Move); } catch { }

        // Dropped outside the glass panel -> remove it from the folder (iOS "drag out").
        try
        {
            NativeMethods.GetCursorPos(out var c);
            var tl = Frost.PointToScreen(new Point(0, 0));
            var src = PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            double fw = Frost.ActualWidth * sx, fh = Frost.ActualHeight * sy;
            bool inside = c.x >= tl.X && c.x <= tl.X + fw && c.y >= tl.Y && c.y <= tl.Y + fh;
            if (!inside) RemoveItem(item);
        }
        catch { }

        _dragOutActive = false;
        _dragOutItem = null;
    }

    private static ShortcutItem? FindItem(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is Button b && b.Tag is ShortcutItem si) return si;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>Maps 0..100 frostiness to tint opacity and blur strength.</summary>
    private void ApplyFrostiness(int frostiness)
    {
        double f = Math.Clamp(frostiness, 0, 100) / 100.0;
        // Uniform white veil: effective opacity == this value (brush is solid white).
        // Opaque enough at the default that the wallpaper's bright-center/dark-edge variation
        // no longer reads through — so the frost looks even everywhere.
        TintLayer.Opacity = Math.Clamp(0.18 + f * 1.10, 0, 1); // 0.18 clear .. 1.0; 55 -> ~0.79
        _blurFactor = (int)Math.Round(10 + f * 28);            // 10 .. 38 (strong, uniform smear)
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RenderPage();

        // Finalize size (SizeToContent settles here) BEFORE positioning, so the bottom/center
        // anchors use the real height — otherwise the bottom row spills off-screen.
        UpdateLayout();
        PositionNearCursor();
        UpdateLayout();

        // Grab the wallpaper behind the panel (window is still Opacity=0, so the grab is clean).
        CaptureAndBlurBackground();

        PlayOpenAnimation();

        // Best-effort bring-to-front (no AttachThreadInput — that couples our input queue with
        // Explorer's and leaves the desktop's focus/redraw glitched, needing an icon "flash" to
        // recover). The panel is Topmost so it's visible regardless; closing is handled by the
        // click-watcher detecting a click outside it, so we don't depend on true foreground.
        Activate();
        Focus();
        try { NativeMethods.BringWindowToTop(new System.Windows.Interop.WindowInteropHelper(this).Handle); }
        catch { }
        try { NativeMethods.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle); }
        catch { }

        // Grace period: ignore any Deactivated that fires right as we open (the race that made
        // folders "open and vanish"). After this, focus-loss also closes it, as a complement to
        // the click-outside close.
        var arm = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450),
        };
        arm.Tick += (_, _) => { arm.Stop(); _closeArmed = true; };
        arm.Start();

        Services.Diag.Log($"panel '{_folder.Name}' loaded pos=({Left:0},{Top:0}) size={ActualWidth:0}x{ActualHeight:0} IsActive={IsActive} Topmost={Topmost}");
    }

    /// <summary>
    /// Grabs the screen region behind the glass panel, blurs it, and paints it as the panel
    /// background. The rounded Border clips it, so there is no square blur behind the corners.
    /// </summary>
    private void CaptureAndBlurBackground()
    {
        try
        {
            var topLeft = Frost.PointToScreen(new Point(0, 0)); // device pixels
            var src = PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            int w = (int)Math.Round(Frost.ActualWidth * sx);
            int h = (int)Math.Round(Frost.ActualHeight * sy);
            if (w <= 0 || h <= 0) return;

            // Capture a padded region AROUND the panel so the blur near the panel's edges has
            // real neighbours to mix with — otherwise the edges look less frosted than the
            // centre (a blur has fewer samples at an image edge). Then crop back to the panel.
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

            Frost.Background = new ImageBrush(ImageHelper.ToImageSource(cropped)) { Stretch = Stretch.Fill };
        }
        catch { /* leave the transparent background; tint + rim still read as glass */ }
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
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = new DoubleAnimation(0.9, 1.0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
        OpenScale.CenterX = ActualWidth / 2;
        OpenScale.CenterY = ActualHeight / 2;
        OpenScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        OpenScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        // Fade the whole (layered) window in from the transparent state used during capture.
        BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(150)));
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
            Source = ImageHelper.LoadIcon(item.LnkPath, 64),
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
            case Key.Escape: SafeClose(); break;
            case Key.Left: ChangePage(-1); break;
            case Key.Right: ChangePage(+1); break;
        }
    }

    // ---- Actions ----

    private void Launch(ShortcutItem item)
    {
        try
        {
            Services.Diag.Log($"panel launch '{item.DisplayName}' -> {item.LnkPath}");
            Process.Start(new ProcessStartInfo(item.LnkPath) { UseShellExecute = true });
            SafeClose();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't launch {item.DisplayName}.\n\n{ex.Message}",
                "Glass Folders", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        Services.Diag.Log($"panel '{_folder.Name}' Deactivated armed={_closeArmed} suppress={SuppressAutoClose} closing={_closing}");
        // Don't self-close during the opening grace period (avoids the "opens and vanishes" race).
        if (!SuppressAutoClose && _closeArmed) SafeClose();
    }

    private bool _closing;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    /// <summary>Closes once. Calling Close() again while a window is already closing throws
    /// (which previously crashed the whole app when launching an app deactivated the panel
    /// mid-close).</summary>
    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); } catch { }
    }
}
