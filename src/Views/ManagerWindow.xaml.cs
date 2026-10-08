using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using GlassFolders;
using GlassFolders.Models;
using GlassFolders.Services;

namespace GlassFolders.Views;

public partial class ManagerWindow : Window
{
    private readonly FolderStore _store;
    private FolderModel? _current;
    private bool _loadingSettings;
    private bool _dark;
    private int _managerTab;   // which tab the preview shows/edits (when the folder is tabbed)
    private readonly List<Border> _tabChips = new();
    private readonly List<TextBox> _tabBoxes = new();
    private bool _gridView = true;

    private List<AppVM> _appVMs = new();
    private Point _dragStart;
    private AppVM? _dragItem;
    private Window? _dragGhost;             // floating real-icon that follows the cursor while reordering
    private int _appPage;
    private bool _frostCaught;
    private double _frostEscape;
    private const int FrostCapture = 2;   // catch the detent within +/- this
    private const int FrostRelease = 7;   // must push this far past to break free

    public ManagerWindow(FolderStore store)
    {
        _store = store;
        _dark = IsDarkTheme();
        InitializeComponent();
        ApplyTheme(_dark);
        BuildPositionGrid();
        BuildMonitorMap();
        BuildSortOptions();
        SourceInitialized += (_, _) => ApplyGlass();
        LoadWallpaper();
        ReloadFolders();
    }

    private void FolderGlass_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Clip the glass content to the border's rounded corners (Border doesn't do this itself).
        FolderGlass.Clip = new RectangleGeometry(
            new Rect(0, 0, FolderGlass.ActualWidth, FolderGlass.ActualHeight), 20, 20);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_store, () => ReloadFolders(_current?.Name)) { Owner = this };
        win.ShowDialog();
    }

    // ---- Open-position 3x3 grid ----

    private readonly Border[] _posCells = new Border[9];

    private void BuildPositionGrid()
    {
        for (int i = 0; i < 9; i++)
        {
            int idx = i;
            var cell = new Border
            {
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Background = PosBrush(false),
                Cursor = Cursors.Hand,
            };
            cell.MouseLeftButtonUp += (_, _) => SetPosition(idx);
            _posCells[i] = cell;
            PositionGrid.Children.Add(cell);
        }
    }

    private Brush PosBrush(bool selected) => selected
        ? (Brush)Resources["Accent"]
        : new SolidColorBrush(Color.FromArgb(0x33, 0x88, 0x88, 0x88));

    private void SetPosition(int idx)
    {
        if (_current == null) return;
        _current.PanelPosition = idx;
        _store.SaveSettings(_current);
        UpdatePositionSelection();
    }

    private void UpdatePositionSelection()
    {
        int sel = _current?.PanelPosition ?? 4;
        for (int i = 0; i < 9; i++)
            _posCells[i].Background = PosBrush(i == sel);
    }

    // ---- Open-monitor map (mirrors the real display arrangement, like Windows' Identify) ----

    private readonly Dictionary<string, Border> _monitorCells = new(StringComparer.OrdinalIgnoreCase);
    private string? _autoMonitorDevice;   // which monitor the current folder's icon is on (for the auto hint)
    private int _monitorProbe;            // guards stale async lookups when switching folders fast

    /// <summary>Finds (off the UI thread) which monitor the current folder's desktop icon sits on,
    /// so "Same as folder" can show it as an outline. Null for taskbar-only folders.</summary>
    private void RefreshAutoMonitor()
    {
        _autoMonitorDevice = null;
        if (_current == null) return;
        string name = _current.Name;
        int token = ++_monitorProbe;
        System.Threading.Tasks.Task.Run(() => DesktopIcons.MonitorDeviceOf(name))
            .ContinueWith(r =>
            {
                if (token != _monitorProbe) return;   // a different folder is selected now
                _autoMonitorDevice = r.Result;
                UpdateMonitorSelection();
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void BuildMonitorMap()
    {
        MonitorMap.Children.Clear();
        _monitorCells.Clear();

        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length == 0) return;

        int minX = screens.Min(s => s.Bounds.Left), minY = screens.Min(s => s.Bounds.Top);
        int maxX = screens.Max(s => s.Bounds.Right), maxY = screens.Max(s => s.Bounds.Bottom);
        double vw = Math.Max(1, maxX - minX), vh = Math.Max(1, maxY - minY);

        const double pad = 3, gap = 2;
        double cw = MonitorMap.Width - 2 * pad, ch = MonitorMap.Height - 2 * pad;
        double scale = Math.Min(cw / vw, ch / vh);
        double ox = pad + (cw - vw * scale) / 2, oy = pad + (ch - vh * scale) / 2;

        // Windows' GDI device index (\\.\DISPLAYn) climbs over time as displays are docked/undocked
        // or drivers change — it can reach e.g. 241/242/243 and no longer matches the 1..N numbers
        // Settings/Identify shows. Re-number the screens 1..N by that index's order so the map reads
        // 1,2,3 like Windows does (relative order preserved).
        static int DeviceIndex(string name)
        {
            var d = new string(name.Where(char.IsDigit).ToArray());
            return int.TryParse(d, out var v) ? v : int.MaxValue;
        }
        var label = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int rank = 1;
        foreach (var s in screens.OrderBy(s => DeviceIndex(s.DeviceName)))
            label[s.DeviceName] = rank++;

        foreach (var s in screens)
        {
            var b = s.Bounds;
            string num = (label.TryGetValue(s.DeviceName, out var n) ? n : 0).ToString();
            var cell = new Border
            {
                Width = Math.Max(12, b.Width * scale - gap),
                Height = Math.Max(12, b.Height * scale - gap),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = $"Display {num} — {b.Width}×{b.Height}",
                Child = new TextBlock
                {
                    Text = num.Length > 0 ? num : "•",
                    Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            string dev = s.DeviceName, rect = $"{b.Left},{b.Top},{b.Width},{b.Height}";
            cell.MouseLeftButtonUp += (_, _) => SetMonitor(dev, rect);
            Canvas.SetLeft(cell, ox + (b.Left - minX) * scale);
            Canvas.SetTop(cell, oy + (b.Top - minY) * scale);
            MonitorMap.Children.Add(cell);
            _monitorCells[dev] = cell;
        }
    }

    private void SetMonitor(string device, string rect)
    {
        if (_current == null) return;
        _current.PanelMonitor = device;
        _current.PanelMonitorRect = rect;
        _store.SaveSettings(_current);
        UpdateMonitorSelection();
    }

    private void AutoMonitor_Click(object sender, MouseButtonEventArgs e)
    {
        if (_current == null) return;
        _current.PanelMonitor = null;
        _current.PanelMonitorRect = null;
        _store.SaveSettings(_current);
        UpdateMonitorSelection();
    }

    private void UpdateMonitorSelection()
    {
        bool auto = string.IsNullOrEmpty(_current?.PanelMonitor);
        var accent = (Brush)Resources["Accent"];
        var dim = new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88));
        var dimBorder = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        foreach (var (dev, cell) in _monitorCells)
        {
            // Explicit pick = solid accent FILL. "Same as folder" = accent OUTLINE on the monitor
            // the folder's icon is currently on (so you can see where it'll open) — no fill.
            bool selected = !auto && string.Equals(dev, _current!.PanelMonitor, StringComparison.OrdinalIgnoreCase);
            bool autoHere = auto && _autoMonitorDevice != null
                && string.Equals(dev, _autoMonitorDevice, StringComparison.OrdinalIgnoreCase);
            cell.Background = selected ? accent : dim;
            cell.BorderBrush = (selected || autoHere) ? accent : dimBorder;
            cell.BorderThickness = new Thickness(autoHere ? 2 : 1);
        }
        if (auto)
        {
            AutoMonitorButton.Background = accent;
            AutoMonitorText.Foreground = Brushes.White;
        }
        else
        {
            AutoMonitorButton.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x88, 0x88, 0x88));
            AutoMonitorText.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        }
    }

    // ---- Theme / glass ----

    private static bool IsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int i) return i == 0;
        }
        catch { }
        return false;
    }

    private void ApplyTheme(bool dark)
    {
        void B(string key, string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
            brush.Freeze();
            Resources[key] = brush;
        }

        if (!dark)
        {
            B("Fg", "#1B1E24"); B("FgDim", "#5C636E"); B("WinTint", "#33FFFFFF");
            B("CardBg", "#6EFFFFFF"); B("CardBorder", "#3CFFFFFF"); B("TileBg", "#4DFFFFFF");
            B("Hover", "#26FFFFFF"); B("Sel", "#3A4F7CF5"); B("CtrlBg", "#66FFFFFF");
            B("Accent", "#4F7CF5"); B("AccentText", "#FFFFFF");
        }
        else
        {
            B("Fg", "#F2F4F8"); B("FgDim", "#A7AEB9"); B("WinTint", "#18000000");
            B("CardBg", "#24FFFFFF"); B("CardBorder", "#24FFFFFF"); B("TileBg", "#1FFFFFFF");
            B("Hover", "#22FFFFFF"); B("Sel", "#4A6E9BFF"); B("CtrlBg", "#1CFFFFFF");
            B("Accent", "#6E9BFF"); B("AccentText", "#0B0E14");
        }
    }

    private void ApplyGlass()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = _dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int backdrop = NativeMethods.DWMSBT_TRANSIENTWINDOW;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            int corner = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch { }
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Folders ----

    private void ReloadFolders(string? selectName = null)
    {
        var vms = _store.ListFolders()
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FolderVM(f, MiniIcon(f)))
            .ToList();
        FolderList.ItemsSource = vms;

        FolderVM? pick = selectName != null
            ? vms.FirstOrDefault(v => string.Equals(v.Name, selectName, StringComparison.OrdinalIgnoreCase))
            : vms.FirstOrDefault();
        FolderList.SelectedItem = pick;
    }

    private static ImageSource? MiniIcon(FolderModel f)
    {
        try
        {
            using var bmp = IconComposer.RenderPreview(f.FirstPagePaths(), 40);
            return ImageHelper.ToImageSource(bmp);
        }
        catch { return null; }
    }

    /// <summary>Called by the app when a folder's contents changed outside the manager (e.g. files
    /// dropped on its desktop icon) so the open manager view doesn't go stale.</summary>
    public void NotifyFolderChanged(string name)
    {
        if (_current == null || !string.Equals(_current.Name, name, StringComparison.OrdinalIgnoreCase)) return;
        var reloaded = _store.FindByName(_current.Name);
        if (reloaded == null) return;
        _current = reloaded;
        RefreshApps();
        RefreshFolderMiniIcon();
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _current = (FolderList.SelectedItem as FolderVM)?.Model;
        // Always show the folder's current on-disk contents (it may have changed since the list
        // was built — e.g. files dropped on its desktop icon).
        if (_current != null) _current = _store.FindByName(_current.Name) ?? _current;
        ContentPane.IsEnabled = _current != null;

        if (_current == null)
        {
            FolderTitle.Text = "Select a folder";
            FolderCount.Text = "";
            _appVMs = new();
            AppList.ItemsSource = null;
            return;
        }

        FolderTitle.Text = _current.Name;
        FolderCount.Text = _current.Items.Count == 1 ? "1 app" : $"{_current.Items.Count} apps";
        SearchBox.Text = "";
        _appPage = 0;
        RefreshApps();
        PopulateSettings();
    }

    // ---- Apps ----

    private void RefreshApps()
    {
        if (_current == null) return;
        var list = ManagerList;
        _appVMs = list.Items
            .Select(i => new AppVM(i, ImageHelper.LoadIcon(i.LnkPath, 64)))
            .ToList();
        FolderCount.Text = list.Items.Count == 1 ? "1 item" : $"{list.Items.Count} items";
        ApplyAppView();
    }

    private List<AppVM> FilteredList()
    {
        string q = SearchBox.Text?.Trim() ?? "";
        return string.IsNullOrEmpty(q)
            ? _appVMs
            : _appVMs.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Shows the folder as a real glass folder: 3x3 grid paginated, with dots/arrows.</summary>
    private void ApplyAppView()
    {
        MainDots.Children.Clear();
        var list = FilteredList();

        if (_gridView)
        {
            int pages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)FolderModel.PageSize));
            _appPage = Math.Clamp(_appPage, 0, pages - 1);
            AppList.ItemsSource = list.Skip(_appPage * FolderModel.PageSize).Take(FolderModel.PageSize).ToList();

            bool multi = pages > 1;
            MainPrev.Visibility = MainNext.Visibility = multi ? Visibility.Visible : Visibility.Hidden;
            MainPrev.IsEnabled = _appPage > 0;
            MainNext.IsEnabled = _appPage < pages - 1;
            if (multi)
                for (int i = 0; i < pages; i++)
                    MainDots.Children.Add(new System.Windows.Shapes.Ellipse
                    {
                        Width = 6,
                        Height = 6,
                        Margin = new Thickness(3, 0, 3, 0),
                        Fill = new SolidColorBrush(i == _appPage
                            ? Color.FromArgb(0xDD, 0x15, 0x15, 0x15)
                            : Color.FromArgb(0x55, 0x15, 0x15, 0x15)),
                    });
        }
        else
        {
            AppList.ItemsSource = list;
            MainPrev.Visibility = MainNext.Visibility = Visibility.Hidden;
        }

        EmptyHint.Visibility = (_current?.Items.Count ?? 0) == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint != null)
            SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _appPage = 0;
        ApplyAppView();
    }

    private void ViewToggle_Click(object sender, RoutedEventArgs e)
    {
        _gridView = !_gridView;
        AppList.ItemTemplate = (DataTemplate)Resources[_gridView ? "GridTile" : "ListRow"];
        AppList.ItemsPanel = (ItemsPanelTemplate)Resources[_gridView ? "GridPanel" : "ListPanel"];
        ViewToggle.Content = _gridView ? "List view" : "Grid view";
        _appPage = 0;
        ApplyAppView();
    }

    private void MainPrev_Click(object sender, RoutedEventArgs e) { _appPage--; ApplyAppView(); }
    private void MainNext_Click(object sender, RoutedEventArgs e) { _appPage++; ApplyAppView(); }

    private void AppList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LaunchSelected();

    private void AppList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var vm = (e.OriginalSource as DependencyObject).FindDataContext<AppVM>();
        if (vm != null) AppList.SelectedItem = vm;
    }

    private void AppList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) RemoveSelected();
        else if (e.Key == Key.Enter) LaunchSelected();
    }

    private void AppOpen_Click(object sender, RoutedEventArgs e) => LaunchSelected();
    private void AppRemove_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    private void LaunchSelected()
    {
        if (AppList.SelectedItem is AppVM vm)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(vm.Item.LnkPath) { UseShellExecute = true }); }
            catch { }
    }

    private void RemoveSelected()
    {
        if (_current == null || AppList.SelectedItem is not AppVM vm) return;
        _store.RemoveShortcut(ManagerList, vm.Item);
        _store.RegenerateAndPublish(_current);
        RefreshApps();
        RefreshFolderMiniIcon();
    }

    // ---- Drag to reorder (only when not filtering) ----

    private void AppList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = (e.OriginalSource as DependencyObject).FindDataContext<AppVM>();
    }

    private void AppList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;
        if (!string.IsNullOrEmpty(SearchBox.Text)) return; // reorder disabled while searching
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        // Lift the real icon onto the cursor and show its slot as a gap; the other tiles reflow around
        // it live as it moves (iOS/Android style), instead of just a dotted rectangle.
        var dragged = _dragItem;
        dragged.Dragging = true;
        ShowDragGhost(dragged);
        AppList.GiveFeedback += Drag_GiveFeedback;
        AppList.QueryContinueDrag += Drag_QueryContinueDrag;
        try { DragDrop.DoDragDrop(AppList, "reorder", DragDropEffects.Move); } catch { }
        AppList.GiveFeedback -= Drag_GiveFeedback;
        AppList.QueryContinueDrag -= Drag_QueryContinueDrag;
        CloseDragGhost();
        StopPageDragTimer();
        dragged.Dragging = false;
        _dragItem = null;
    }

    /// <summary>Test hook: reproduce a drag-reorder of app <paramref name="from"/> onto slot
    /// <paramref name="to"/> (the live-reflow move + the drop commit) and return the resulting saved
    /// order, to prove insert-and-shift semantics and persistence.</summary>
    internal string TestReorder(string folderName, int from, int to)
    {
        ReloadFolders(folderName);
        if (_current == null || from < 0 || from >= _appVMs.Count || to < 0 || to >= _appVMs.Count)
            return "bad-index";
        _dragItem = _appVMs[from];
        var v = _appVMs[from]; _appVMs.RemoveAt(from); _appVMs.Insert(to, v); ApplyAppView();  // reflow
        var mlist = ManagerList;                                                                 // commit
        var ordered = _appVMs.Select(x => x.Item).ToList();
        mlist.Items.Clear();
        foreach (var it in ordered) mlist.Items.Add(it);
        _store.SaveOrder(mlist);
        _dragItem = null;
        var reloaded = new FolderStore(_store.RootPath).FindByName(folderName);
        return string.Join(",", (reloaded?.Items ?? new()).Select(i => i.DisplayName));
    }

    // Hide the OS drag cursor — the floating icon is the pointer now.
    private void Drag_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        e.UseDefaultCursors = false;
        Mouse.SetCursor(Cursors.None);
        e.Handled = true;
    }

    private void Drag_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (_dragGhost != null && NativeMethods.GetCursorPos(out var c)) MoveDragGhost(c.x, c.y);
    }

    private void ShowDragGhost(AppVM vm)
    {
        try
        {
            var icon = new Image
            {
                Source = vm.Icon,
                Width = 52, Height = 52,
                Stretch = Stretch.Uniform,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 11, ShadowDepth = 2, Opacity = 0.35 },
            };
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
                Width = 60, Height = 60,
                Content = new Grid { Children = { icon } },
            };
            _dragGhost.Show();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_dragGhost).Handle;
            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
            if (NativeMethods.GetCursorPos(out var c)) MoveDragGhost(c.x, c.y);
        }
        catch { _dragGhost = null; }
    }

    private void MoveDragGhost(int cursorX, int cursorY)
    {
        if (_dragGhost == null) return;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_dragGhost).Handle;
            var src = PresentationSource.FromVisual(_dragGhost);
            double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            int pxW = (int)Math.Round(_dragGhost.Width * scale), pxH = (int)Math.Round(_dragGhost.Height * scale);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                cursorX - pxW / 2, cursorY - pxH / 2, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
        catch { }
    }

    private void CloseDragGhost()
    {
        if (_dragGhost == null) return;
        try { _dragGhost.Close(); } catch { }
        _dragGhost = null;
    }

    /// <summary>Live reflow: while dragging over the grid, slide the dragged tile to the slot under
    /// the cursor so the others shift to open a gap (insert-and-shift, not swap). The order is
    /// applied to the view (_appVMs) now and committed to disk on drop.</summary>
    private void AppList_DragOver(object sender, DragEventArgs e)
    {
        if (_dragItem == null) { e.Handled = true; return; }
        var target = (e.OriginalSource as DependencyObject).FindDataContext<AppVM>();
        if (target == null || ReferenceEquals(target, _dragItem)) { e.Effects = DragDropEffects.Move; e.Handled = true; return; }

        int from = _appVMs.IndexOf(_dragItem);
        int to = _appVMs.IndexOf(target);
        if (from >= 0 && to >= 0 && from != to)
        {
            _appVMs.RemoveAt(from);
            _appVMs.Insert(to, _dragItem);
            ApplyAppView();   // re-render; the dragged VM keeps Dragging=true so its slot stays a gap
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    // ---- Drag a tile onto a page arrow to move it to another page ----

    private System.Windows.Threading.DispatcherTimer? _pageDragTimer;
    private int _pageDragDir;

    private void PageArrow_DragOver(object sender, DragEventArgs e)
    {
        if (_dragItem == null) return;   // only while reordering a tile
        int dir = (sender as FrameworkElement)?.Tag as string == "1" ? 1 : -1;
        e.Handled = true;
        if (_pageDragTimer != null && _pageDragDir == dir) return;   // already flipping this way
        StopPageDragTimer();
        _pageDragDir = dir;
        _pageDragTimer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(500) };
        _pageDragTimer.Tick += (_, _) =>
        {
            int pages = Math.Max(1, (int)Math.Ceiling(FilteredList().Count / (double)FolderModel.PageSize));
            int next = Math.Clamp(_appPage + _pageDragDir, 0, pages - 1);
            if (next == _appPage) { StopPageDragTimer(); return; }   // reached the first/last page
            _appPage = next;
            ApplyAppView();
            if (_pageDragTimer != null) _pageDragTimer.Interval = TimeSpan.FromMilliseconds(800);
        };
        _pageDragTimer.Start();
    }

    private void PageArrow_DragLeave(object sender, DragEventArgs e) => StopPageDragTimer();

    private void StopPageDragTimer()
    {
        _pageDragTimer?.Stop();
        _pageDragTimer = null;
        _pageDragDir = 0;
    }

    private void AppList_Drop(object sender, DragEventArgs e)
    {
        StopPageDragTimer();
        if (_current == null || _dragItem == null) return;

        // The live reflow already put _appVMs in the final order; write that order to the model and
        // persist it. (Reorder is disabled while searching, so _appVMs is the full, unfiltered list.)
        var mlist = ManagerList;
        var ordered = _appVMs.Select(v => v.Item).ToList();
        if (ordered.Count == mlist.Items.Count)
        {
            mlist.Items.Clear();
            foreach (var it in ordered) mlist.Items.Add(it);
            _store.SaveOrder(mlist);
            _store.RegenerateAndPublish(_current);   // order changes the first page/icon
            RefreshApps();
            RefreshFolderMiniIcon();
        }
        _dragItem = null;
        e.Handled = true;
    }

    private void RefreshFolderMiniIcon()
    {
        if (FolderList.SelectedItem is FolderVM vm && _current != null)
            vm.Icon = MiniIcon(_current); // FolderVM raises change
    }

    // ---- Folder ops ----

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var name = ModernDialogWindow.Prompt(this, "New folder", "Enter a name:", "Folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        var created = _store.CreateFolder(name);
        ReloadFolders(created.Name);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var name = ModernDialogWindow.Prompt(this, "Rename folder", "Enter a new name:", _current.Name);
        if (string.IsNullOrWhiteSpace(name) || name == _current.Name) return;
        try { _store.RenameFolder(_current, name); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Rename folder",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        ReloadFolders(FolderStore.Sanitize(name));
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (!ModernDialogWindow.Confirm(this, "Delete folder?",
                $"“{_current.Name}” and its desktop icon will be removed. This can’t be undone.",
                okText: "Delete", cancelText: "Cancel", danger: true))
            return;
        _store.DeleteFolder(_current);
        _current = null;
        ReloadFolders();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (_current.Items.Count == 0) return;
        if (!ModernDialogWindow.Confirm(this, "Clear this folder?",
                $"Remove all {_current.Items.Count} shortcut(s) from “{_current.Name}”? " +
                "The folder stays; your original files/apps are not deleted.",
                okText: "Clear", cancelText: "Cancel", danger: true))
            return;
        _store.ClearFolder(_current);
        _store.RegenerateAndPublish(_current);
        RefreshApps();
        RefreshFolderMiniIcon();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Add apps",
            Multiselect = true,
            Filter = "Programs & shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        AddFiles(dlg.FileNames);
    }

    /// <summary>Adds files to the current folder, reporting any that couldn't be added instead of
    /// swallowing the failure silently.</summary>
    private void AddFiles(IEnumerable<string> files)
    {
        if (_current == null) return;
        // Multi-file selections arrive in an arbitrary OS order; add them in the folder's chosen
        // sort order (name / date added / date modified).
        var ordered = FileSort.OrderPaths(files);
        var target = ManagerList;
        var failed = new List<string>();
        foreach (var f in ordered)
            try { _store.AddShortcut(target, f); }
            catch { failed.Add(System.IO.Path.GetFileName(f)); }
        _store.RegenerateAndPublish(_current);
        RefreshApps();
        RefreshFolderMiniIcon();
        if (failed.Count > 0)
            System.Windows.MessageBox.Show(this,
                "Couldn't add: " + string.Join(", ", failed), "Add apps",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }

    private void Apps_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
        else if (_dragItem != null) e.Effects = DragDropEffects.Move;
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Apps_Drop(object sender, DragEventArgs e)
    {
        if (_current == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        AddFiles((string[])e.Data.GetData(DataFormats.FileDrop));
    }

    // ---- Settings + live preview ----

    private void PopulateSettings()
    {
        _loadingSettings = true;
        if (_current != null)
        {
            _frostCaught = false;
            FrostSlider.Value = _current.Frostiness;
            UpdateFrostLabel(_current.Frostiness);
            OnDesktopCheck.IsChecked = _current.OnDesktop;
            FileListCheck.IsChecked = _current.View == FolderView.List;
            TabsCheck.IsChecked = _current.Tabbed;
            _managerTab = 0;
            BuildTabManager();
            UpdateTabSectionVisibility();   // "Use tabs" shows only for list folders
            UpdateSortSelection();

            UpdateGlass(_current.Frostiness);
            UpdatePositionSelection();
            UpdateMonitorSelection();
            RefreshAutoMonitor();   // async: outlines the folder's current monitor under "Same as folder"
        }
        _loadingSettings = false;
    }

    private void Frost_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int def = FolderModel.DefaultFrostiness;
        int v = (int)e.NewValue;

        // Harsh detent with hysteresis: it catches at the default and HOLDS there; you must
        // deliberately push ~FrostRelease past it (accumulated) before it breaks free.
        if (!_loadingSettings)
        {
            if (_frostCaught)
            {
                _frostEscape += v - def;
                if (Math.Abs(_frostEscape) <= FrostRelease)
                {
                    if (v != def) { FrostSlider.Value = def; return; } // stay stuck at the detent
                    // v == def -> fall through and apply the default
                }
                else
                {
                    _frostCaught = false;
                    int released = Math.Clamp(def + (int)Math.Round(_frostEscape), 0, 100);
                    if (Math.Abs(released - def) <= FrostCapture)
                        released = Math.Clamp(def + Math.Sign(_frostEscape) * (FrostCapture + 1), 0, 100);
                    if (v != released) { FrostSlider.Value = released; return; }
                }
            }
            else if (v != def && Math.Abs(v - def) <= FrostCapture)
            {
                _frostCaught = true;
                _frostEscape = 0;
                FrostSlider.Value = def; // snap into the detent
                return;
            }
        }

        UpdateFrostLabel(v);
        UpdateGlass(v);
        if (_loadingSettings || _current == null) return;
        _current.Frostiness = v;
        _store.SaveSettings(_current);
    }

    private void UpdateFrostLabel(int v)
    {
        if (FrostValueMain == null) return;
        if (v == FolderModel.DefaultFrostiness)
        {
            FrostValueMain.Text = "Default";
            FrostValueSub.Text = v.ToString();
            FrostValueSub.Visibility = Visibility.Visible;
        }
        else
        {
            FrostValueMain.Text = v.ToString();
            FrostValueSub.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Frostiness drives the glass folder's blur + tint, live.</summary>
    private void UpdateGlass(int frostiness)
    {
        if (MainBlurEffect == null || MainTint == null) return;
        int v = Math.Clamp(frostiness, 0, 100);
        // Same scale as the panel (55 == old ~20). Keep the live WPF blur modest for weak GPUs.
        MainTint.Opacity = FolderModel.TintOpacity(v);
        MainBlurEffect.Radius = v <= FolderModel.DefaultFrostiness
            ? 2 + (v / (double)FolderModel.DefaultFrostiness) * (8 - 2)
            : 8 + ((v - FolderModel.DefaultFrostiness) / (double)(100 - FolderModel.DefaultFrostiness)) * (20 - 8);
    }

    private void OnDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || _current == null) return;
        _current.OnDesktop = OnDesktopCheck.IsChecked == true;
        _store.SaveSettings(_current);
        _store.RegenerateAndPublish(_current);
    }

    private void FileList_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || _current == null) return;
        bool list = FileListCheck.IsChecked == true;
        _current.View = list ? FolderView.List : FolderView.Grid;
        _store.SaveSettings(_current);

        // Tabs only make sense in a list folder. Leaving list view collapses any tabs back into the
        // single folder, so the grid shows the items (they live in tab subfolders while tabbed).
        if (!list && _current.Tabbed)
        {
            _store.DisableTabs(_current);
            _current = _store.FindByName(_current.Name) ?? _current;
            TabsCheck.IsChecked = false;
            _managerTab = 0;
            BuildTabManager();
            RefreshApps();
            RefreshFolderMiniIcon();
        }
        UpdateTabSectionVisibility();
    }

    /// <summary>The "Use tabs" checkbox (and its tab-manager row) belong to list folders only — hide
    /// them entirely for grid folders rather than just disabling them.</summary>
    private void UpdateTabSectionVisibility()
    {
        bool isList = FileListCheck.IsChecked == true;
        TabsCheck.Visibility = isList ? Visibility.Visible : Visibility.Collapsed;
        if (!isList) TabManageRow.Visibility = Visibility.Collapsed;
    }

    private void Tabs_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || _current == null) return;
        if (TabsCheck.IsChecked == true) _store.EnableTabs(_current);
        else _store.DisableTabs(_current);
        _current = _store.FindByName(_current.Name) ?? _current;   // reload so Tabs populate
        _managerTab = 0;
        BuildTabManager();
        RefreshApps();
        RefreshFolderMiniIcon();
    }

    /// <summary>The list the manager's preview edits — the selected tab when the folder is tabbed,
    /// otherwise the folder itself.</summary>
    private FolderModel ManagerList =>
        _current != null && _current.Tabbed && _current.Tabs.Count > 0
            ? _current.Tabs[Math.Clamp(_managerTab, 0, _current.Tabs.Count - 1)].Folder
            : _current!;

    // ---- Tab manager (name / add / delete tabs) ----

    /// <summary>Rebuilds the tab chips under "Use tabs": each chip is an editable name + a delete
    /// button, plus an "Add tab" button. Shown only when the folder is tabbed. NOTE: a full rebuild
    /// recreates the name text boxes, so it runs ONLY on folder-select / tabs-toggle / add / delete —
    /// never while a name is being edited (switching tabs just recolors existing chips).</summary>
    private void BuildTabManager()
    {
        if (TabManageRow == null || TabChipsHost == null) return;

        _tabChips.Clear();
        _tabBoxes.Clear();
        TabChipsHost.Children.Clear();

        if (_current == null || !_current.Tabbed || _current.Tabs.Count == 0)
        {
            TabManageRow.Visibility = Visibility.Collapsed;
            return;
        }

        TabManageRow.Visibility = Visibility.Visible;
        _managerTab = Math.Clamp(_managerTab, 0, _current.Tabs.Count - 1);

        for (int i = 0; i < _current.Tabs.Count; i++)
            TabChipsHost.Children.Add(BuildTabChip(i));

        TabChipsHost.Children.Add(BuildAddTabButton());
        UpdateTabChipHighlight();
    }

    private UIElement BuildTabChip(int idx)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(9, 4, 6, 4),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };

        // Editable name — type to rename. Keeps WHATEVER you type: it commits on Enter AND on click-
        // away (LostKeyboardFocus). Selecting another tab never rebuilds this box, so edits survive.
        var name = new TextBox
        {
            Text = _current!.Tabs[idx].Name,
            MinWidth = 64,
            MaxWidth = 160,
            FontSize = 12.5,
            Foreground = (Brush)Resources["Fg"],
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CaretBrush = (Brush)Resources["Fg"],
        };
        void CommitName()
        {
            if (_current == null || idx >= _current.Tabs.Count) return;
            var t = name.Text.Trim();
            if (string.IsNullOrEmpty(t)) { name.Text = _current.Tabs[idx].Name; return; }
            if (t != _current.Tabs[idx].Name) _store.RenameTab(_current, idx, t);  // saves tabs.txt
        }
        // Clicking into a (possibly non-selected) tab's name selects it WITHOUT a rebuild, so the box
        // you just clicked stays alive and keyboard focus sticks.
        name.GotKeyboardFocus += (_, _) => SelectTab(idx, rebuild: false);
        name.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { CommitName(); Keyboard.ClearFocus(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Escape) { name.Text = _current!.Tabs[idx].Name; Keyboard.ClearFocus(); e.Handled = true; }
        };
        name.LostKeyboardFocus += (_, _) => CommitName();

        var del = new TextBlock
        {
            Text = "✕",
            FontSize = 11,
            Margin = new Thickness(6, 0, 2, 0),
            Foreground = (Brush)Resources["FgDim"],
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "Delete this tab",
        };
        del.MouseEnter += (_, _) => del.Foreground = (Brush)Resources["Accent"];
        del.MouseLeave += (_, _) => del.Foreground = (Brush)Resources["FgDim"];
        del.MouseLeftButtonUp += (_, e) => { e.Handled = true; DeleteManagerTab(idx); };

        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        inner.Children.Add(name);
        inner.Children.Add(del);
        chip.Child = inner;

        // Click the chip body (not the ✕, not the text box) selects the tab for the preview.
        chip.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, del) && e.OriginalSource is not TextBox)
                SelectTab(idx, rebuild: false);
        };

        _tabChips.Add(chip);
        _tabBoxes.Add(name);
        return chip;
    }

    private UIElement BuildAddTabButton()
    {
        var add = new Border
        {
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(11, 4, 11, 4),
            Background = Brushes.Transparent,
            BorderBrush = (Brush)Resources["CardBorder"],
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "+ Add tab",
                FontSize = 12.5,
                Foreground = (Brush)Resources["FgDim"],
                VerticalAlignment = VerticalAlignment.Center,
            },
            ToolTip = "Add a new tab",
        };
        add.MouseEnter += (_, _) => add.Background = (Brush)Resources["Hover"];
        add.MouseLeave += (_, _) => add.Background = Brushes.Transparent;
        add.MouseLeftButtonUp += (_, _) =>
        {
            if (_current == null) return;
            CommitPendingTabEdit();     // keep any name being typed before we rebuild
            _store.AddTab(_current, $"Tab {_current.Tabs.Count + 1}");
            _managerTab = _current.Tabs.Count - 1;   // select the new tab
            BuildTabManager();
            RefreshApps();
        };
        return add;
    }

    /// <summary>Recolors the chips for the current selection without recreating them (so editing a
    /// name is never interrupted).</summary>
    private void UpdateTabChipHighlight()
    {
        for (int i = 0; i < _tabChips.Count; i++)
        {
            bool sel = i == _managerTab;
            _tabChips[i].Background = sel ? (Brush)Resources["Sel"] : (Brush)Resources["CtrlBg"];
            _tabChips[i].BorderBrush = sel ? (Brush)Resources["Accent"] : (Brush)Resources["CardBorder"];
            _tabChips[i].BorderThickness = new Thickness(sel ? 1.5 : 1);
            if (i < _tabBoxes.Count)
                _tabBoxes[i].FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>If a tab name is mid-edit, flush it (its LostKeyboardFocus commit) before we rebuild.</summary>
    private void CommitPendingTabEdit()
    {
        if (Keyboard.FocusedElement is TextBox tb && _tabBoxes.Contains(tb))
            Keyboard.ClearFocus();
    }

    /// <summary>Test hook: select <paramref name="folderName"/>, type a new name into tab 0 and move
    /// focus AWAY (no Enter) — simulating the "type then click elsewhere" the user reported. Returns
    /// the saved name afterwards; it should equal <paramref name="typed"/>, proving click-away commits.</summary>
    internal string? TestEditFirstFolderTabName(string folderName, string typed)
    {
        ReloadFolders(folderName);   // selects it → PopulateSettings → BuildTabManager
        if (_tabBoxes.Count == 0 || _current == null) return null;
        var box = _tabBoxes[0];
        box.Focus();
        Keyboard.Focus(box);
        box.Text = typed;
        // Move focus to another control WITHOUT pressing Enter — fires LostKeyboardFocus (commit).
        TabsCheck.Focus();
        Keyboard.Focus(TabsCheck);
        return _current.Tabs.Count > 0 ? _current.Tabs[0].Name : null;
    }

    private void SelectTab(int idx, bool rebuild)
    {
        if (_current == null) return;
        int clamped = Math.Clamp(idx, 0, _current.Tabs.Count - 1);
        if (clamped == _managerTab && !rebuild) { UpdateTabChipHighlight(); return; }
        _managerTab = clamped;
        if (rebuild) BuildTabManager(); else UpdateTabChipHighlight();
        RefreshApps();
    }

    private void DeleteManagerTab(int idx)
    {
        if (_current == null || idx < 0 || idx >= _current.Tabs.Count) return;
        CommitPendingTabEdit();
        var tab = _current.Tabs[idx];
        if (tab.Folder.Items.Count > 0 &&
            !ModernDialogWindow.Confirm(this, "Delete tab",
                $"Delete the tab “{tab.Name}” and its {tab.Folder.Items.Count} item(s)?",
                okText: "Delete", danger: true))
            return;

        _store.RemoveTab(_current, idx);
        // RemoveTab turns tabs off entirely when the last one goes; reload so our model matches.
        _current = _store.FindByName(_current.Name) ?? _current;
        if (!_current.Tabbed) { _managerTab = 0; TabsCheck.IsChecked = false; }
        else _managerTab = Math.Clamp(_managerTab, 0, _current.Tabs.Count - 1);
        _store.RegenerateAndPublish(_current);   // tab 0 may have changed → refresh closed icon
        BuildTabManager();
        RefreshApps();
        RefreshFolderMiniIcon();
    }

    // ---- Sort dropdown (custom, themed) ----

    private static readonly string[] SortLabels =
        { "As I added them", "Name (A→Z)", "Name (Z→A)",
          "Date modified (newest)", "Date modified (oldest)",
          "Date added (newest)", "Date added (oldest)" };
    private readonly Border[] _sortRows = new Border[SortLabels.Length];

    private void BuildSortOptions()
    {
        // Near-solid dropdown background so it reads over the acrylic, themed light/dark.
        SortPopupPanel.Background = new SolidColorBrush(_dark
            ? Color.FromRgb(0x2A, 0x2E, 0x36) : Color.FromRgb(0xFF, 0xFF, 0xFF));

        SortOptions.Children.Clear();
        for (int i = 0; i < SortLabels.Length; i++)
        {
            int idx = i;
            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(11, 7, 11, 7),
                Margin = new Thickness(1),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = SortLabels[i],
                    FontSize = 12.5,
                    Foreground = (Brush)Resources["Fg"],
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            row.MouseEnter += (_, _) => row.Background = (Brush)Resources["Hover"];
            row.MouseLeave += (_, _) => row.Background =
                idx == (int)(_current?.Sort ?? FolderSort.Custom) ? (Brush)Resources["Sel"] : Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => { SelectSort(idx); SortPopup.IsOpen = false; };
            _sortRows[i] = row;
            SortOptions.Children.Add(row);
        }
    }

    private void SortButton_Click(object sender, MouseButtonEventArgs e)
    {
        if (_current == null) return;
        SortPopup.IsOpen = true;
    }

    private void SelectSort(int idx)
    {
        if (_current == null) return;
        _current.Sort = (FolderSort)Math.Clamp(idx, 0, SortLabels.Length - 1);
        _store.SaveSettings(_current);   // persisted to settings.txt -> survives reopen/reboot
        UpdateSortSelection();
    }

    private void UpdateSortSelection()
    {
        int sel = (int)(_current?.Sort ?? FolderSort.Custom);
        SortButtonText.Text = SortLabels[Math.Clamp(sel, 0, SortLabels.Length - 1)];
        for (int i = 0; i < _sortRows.Length; i++)
            if (_sortRows[i] != null)
                _sortRows[i].Background = i == sel ? (Brush)Resources["Sel"] : Brushes.Transparent;
    }

    private void LoadWallpaper()
    {
        try
        {
            var sb = new StringBuilder(512);
            string? path = null;
            if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETDESKWALLPAPER, 512, sb, 0)
                && File.Exists(sb.ToString()))
                path = sb.ToString();
            else
            {
                var trans = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
                if (File.Exists(trans)) path = trans;
            }
            if (path == null) return;

            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            MainBlur.Source = img;
        }
        catch { }
    }
}

/// <summary>Sidebar folder row.</summary>
public sealed class FolderVM : System.ComponentModel.INotifyPropertyChanged
{
    public FolderModel Model { get; }
    public string Name => Model.Name;

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); }
    }

    public FolderVM(FolderModel model, ImageSource? icon) { Model = model; _icon = icon; }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>App tile / row.</summary>
public sealed class AppVM : System.ComponentModel.INotifyPropertyChanged
{
    public ShortcutItem Item { get; }
    public string Name => Item.DisplayName;
    public ImageSource? Icon { get; }
    public AppVM(ShortcutItem item, ImageSource? icon) { Item = item; Icon = icon; }

    // True while this tile is the one being dragged — the template hides it so its slot is a gap.
    private bool _dragging;
    public bool Dragging
    {
        get => _dragging;
        set { if (_dragging != value) { _dragging = value; PropertyChanged?.Invoke(this, new(nameof(Dragging))); } }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

internal static class VisualTreeExtensions
{
    public static T? FindDataContext<T>(this DependencyObject? source) where T : class
    {
        while (source != null)
        {
            if (source is FrameworkElement fe && fe.DataContext is T t) return t;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return null;
    }
}

/// <summary>Minimal modal text prompt (avoids a VB dependency).</summary>
internal static class Prompt
{
    public static string? Show(Window owner, string message, string title, string initial)
    {
        var win = new Window
        {
            Title = title,
            Width = 360,
            Height = 165,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
        };
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(label, 0);
        var box = new TextBox { Text = initial, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRow(box, 1);
        box.SelectAll();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var ok = new Button { Content = "OK", Width = 72, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 72, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);

        string? result = null;
        ok.Click += (_, _) => { result = box.Text.Trim(); win.DialogResult = true; };

        grid.Children.Add(label);
        grid.Children.Add(box);
        grid.Children.Add(buttons);
        win.Content = grid;
        box.Focus();

        return win.ShowDialog() == true ? result : null;
    }
}
