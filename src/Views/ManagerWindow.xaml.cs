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
    private bool _gridView = true;

    private List<AppVM> _appVMs = new();
    private Point _dragStart;
    private AppVM? _dragItem;
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

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _current = (FolderList.SelectedItem as FolderVM)?.Model;
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
        _appVMs = _current.Items
            .Select(i => new AppVM(i, ImageHelper.LoadIcon(i.LnkPath, 64)))
            .ToList();
        FolderCount.Text = _current.Items.Count == 1 ? "1 app" : $"{_current.Items.Count} apps";
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
        _store.RemoveShortcut(_current, vm.Item);
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

        DragDrop.DoDragDrop(AppList, "reorder", DragDropEffects.Move);
        _dragItem = null;
    }

    private void AppList_Drop(object sender, DragEventArgs e)
    {
        if (_current == null || _dragItem == null) return;
        var target = (e.OriginalSource as DependencyObject).FindDataContext<AppVM>();

        int from = _current.Items.IndexOf(_dragItem.Item);
        int to = target != null ? _current.Items.IndexOf(target.Item) : _current.Items.Count - 1;
        if (from >= 0 && to >= 0 && from != to)
        {
            _store.Move(_current, from, to);
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
        _store.RenameFolder(_current, name);
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
        foreach (var f in dlg.FileNames) try { _store.AddShortcut(_current, f); } catch { }
        _store.RegenerateAndPublish(_current);
        RefreshApps();
        RefreshFolderMiniIcon();
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
        foreach (var f in (string[])e.Data.GetData(DataFormats.FileDrop))
            try { _store.AddShortcut(_current, f); } catch { }
        _store.RegenerateAndPublish(_current);
        RefreshApps();
        RefreshFolderMiniIcon();
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

            UpdateGlass(_current.Frostiness);
            UpdatePositionSelection();
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
        double f = Math.Clamp(frostiness, 0, 100) / 100.0;
        MainBlurEffect.Radius = 10 + f * 40;                     // strong, uniform blur
        MainTint.Opacity = Math.Clamp(0.18 + f * 1.10, 0, 1);   // matches the panel veil
    }

    private void OnDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || _current == null) return;
        _current.OnDesktop = OnDesktopCheck.IsChecked == true;
        _store.SaveSettings(_current);
        _store.RegenerateAndPublish(_current);
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
public sealed class AppVM
{
    public ShortcutItem Item { get; }
    public string Name => Item.DisplayName;
    public ImageSource? Icon { get; }
    public AppVM(ShortcutItem item, ImageSource? icon) { Item = item; Icon = icon; }
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
