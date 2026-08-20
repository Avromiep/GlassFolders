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

        Loaded += OnLoaded;
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

        // Reliably bring to the front. The tray process isn't the foreground process, so a
        // plain SetForegroundWindow usually loses to the foreground lock; AttachThreadInput
        // gets past it. Without this the window can immediately deactivate and self-close.
        Activate();
        Focus();
        ForceForeground();

        // Grace period: ignore any Deactivated that fires right as we open (the race that made
        // folders "open and vanish"). After this, clicking the desktop closes normally.
        var arm = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450),
        };
        arm.Tick += (_, _) => { arm.Stop(); _closeArmed = true; };
        arm.Start();
    }

    private void ForceForeground()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            IntPtr fg = NativeMethods.GetForegroundWindow();
            uint fgThread = NativeMethods.GetWindowThreadProcessId(fg, out _);
            uint myThread = NativeMethods.GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != myThread
                && NativeMethods.AttachThreadInput(myThread, fgThread, true);
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
            if (attached) NativeMethods.AttachThreadInput(myThread, fgThread, false);
        }
        catch { }
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

            using var shot = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(shot))
                g.CopyFromScreen((int)topLeft.X, (int)topLeft.Y, 0, 0, new System.Drawing.Size(w, h));

            using var blurred = DownUpBlur(shot, _blurFactor);
            Frost.Background = new ImageBrush(ImageHelper.ToImageSource(blurred)) { Stretch = Stretch.Fill };
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
        var mouse = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(mouse);
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

        // Keep the 3x3 shape stable by padding empty cells on the last page.
        int shown = _folder.Page(_pageIndex).Count();
        for (int i = shown; i < FolderModel.PageSize; i++)
            ItemsGrid.Items.Add(new Border { Width = 0, Height = 0 });

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
            case Key.Escape: Close(); break;
            case Key.Left: ChangePage(-1); break;
            case Key.Right: ChangePage(+1); break;
        }
    }

    // ---- Actions ----

    private void Launch(ShortcutItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.LnkPath) { UseShellExecute = true });
            Close();
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
        // Don't self-close during the opening grace period (avoids the "opens and vanishes" race).
        if (!SuppressAutoClose && _closeArmed) Close();
    }
}
