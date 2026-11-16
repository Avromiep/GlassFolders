using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GlassFolders.Models;
using GlassFolders.Services;
using Microsoft.Win32;

namespace GlassFolders.Views;

/// <summary>
/// Searchable installed-apps picker that replaces the old file dialog for "Add apps". Lists Start
/// Menu shortcuts (this user + all users) and shell:AppsFolder (Store/UWP) with checkboxes; apps
/// already in the folder show checked and disabled. A Browse-for-a-file link remains as a fallback.
/// Enumeration runs off-thread; the dialog opens instantly and fills in. It never launches anything.
/// </summary>
public partial class AppPickerWindow : Window
{
    /// <summary>One list row. Icon fills in after the name (INotifyPropertyChanged).</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        public AppEntry Entry { get; }
        public string Name => Entry.Name;
        public bool AlreadyInFolder { get; }
        public bool CanToggle => !AlreadyInFolder;     // already-present rows are checked and locked
        public Action? Changed;

        private bool _checked;
        public bool IsChecked
        {
            get => _checked;
            set { if (_checked != value) { _checked = value; Raise(nameof(IsChecked)); Changed?.Invoke(); } }
        }

        private ImageSource? _icon;
        public ImageSource? Icon { get => _icon; set { _icon = value; Raise(nameof(Icon)); } }

        public Row(AppEntry e, bool already) { Entry = e; AlreadyInFolder = already; _checked = already; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
    }

    private readonly HashSet<string> _already;           // identity keys already in the target folder
    private readonly CancellationTokenSource _cts = new();
    private List<Row> _rows = new();
    private bool _ready;
    private static List<AppEntry>? _cache;               // process-wide, so a reopen shows instantly

    /// <summary>Set when the user confirms picker selections (newly checked rows only).</summary>
    public List<AppEntry> SelectedEntries { get; } = new();
    /// <summary>Set when the user picked files via Browse instead.</summary>
    public string[]? BrowsedFiles { get; private set; }

    public AppPickerWindow(FolderModel target)
    {
        InitializeComponent();
        bool dark = Theming.IsDark();
        ApplyTheme(dark);
        SourceInitialized += (_, _) => { try { Theming.ApplyGlass(this, dark); } catch { } };
        _already = BuildAlreadySet(target);
        Loaded += (_, _) => { SearchBox.Focus(); StartEnumeration(); };
        Closed += (_, _) => _cts.Cancel();
    }

    /// <summary>The target folder's current apps, keyed the same way as the picker (AUMID else target
    /// path + arguments), so matching rows can be shown already-added.</summary>
    private static HashSet<string> BuildAlreadySet(FolderModel folder)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var it in folder.Items)
        {
            try
            {
                var aumid = ShellLink.ReadAppUserModelId(it.LnkPath);
                var key = AppPickerRules.IdentityKey(
                    string.IsNullOrEmpty(aumid) ? null : aumid,
                    ShellLink.ResolveTarget(it.LnkPath),
                    ShellLink.ReadArguments(it.LnkPath));
                if (key != null) set.Add(key);
            }
            catch { }
        }
        return set;
    }

    private void StartEnumeration()
    {
        if (_cache != null) Bind(_cache);   // instant from a prior open; always refreshed below
        InstalledAppsEnumerator.Run(
            onReady: list => Dispatcher.BeginInvoke(() => { if (!_cts.IsCancellationRequested) { _cache = list; Bind(list); } }),
            onIcon: (e, img) => Dispatcher.BeginInvoke(() => { if (!_cts.IsCancellationRequested) SetIcon(e, img); }),
            onFailed: () => Dispatcher.BeginInvoke(() => { if (!_cts.IsCancellationRequested && !_ready) ShowFailure(); }),
            _cts.Token);

        // Hard cap: if enumeration hasn't produced a list in 15s, show the failure message but keep
        // Browse working.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += (_, _) => { timer.Stop(); if (!_ready && !_cts.IsCancellationRequested) ShowFailure(); };
        timer.Start();
    }

    private void ShowFailure()
    {
        StatusText.Text = "Couldn't load the installed apps. Use Browse for a file below.";
        StatusText.Visibility = Visibility.Visible;
    }

    private void Bind(List<AppEntry> list)
    {
        _ready = true;
        _rows = list.Select(e =>
        {
            var key = AppPickerRules.IdentityKey(e);
            var row = new Row(e, key != null && _already.Contains(key));
            row.Changed = UpdateAddButton;
            return row;
        }).ToList();

        if (list.Count == 0)
        {
            StatusText.Text = "No installed apps found. Use Browse for a file below.";
            StatusText.Visibility = Visibility.Visible;
        }
        else StatusText.Visibility = Visibility.Collapsed;

        ApplyFilter();
        UpdateAddButton();
    }

    private void SetIcon(AppEntry e, ImageSource img)
    {
        foreach (var r in _rows)
            if (ReferenceEquals(r.Entry, e)) { r.Icon = img; return; }
    }

    private void ApplyFilter()
    {
        var keep = new HashSet<AppEntry>(AppPickerRules.Filter(_rows.Select(r => r.Entry), SearchBox.Text));
        AppList.ItemsSource = _rows.Where(r => keep.Contains(r.Entry)).ToList();
    }

    private void UpdateAddButton() =>
        AddButton.IsEnabled = _rows.Any(r => r.IsChecked && !r.AlreadyInFolder);

    // ---- Browse dialog (path choice + DereferenceLinks are testable) ----

    /// <summary>The Add-apps Browse dialog, configured but not shown. Starts in the Start Menu Programs
    /// folder (never System32), and keeps a picked .lnk as the shortcut itself (DereferenceLinks=false).</summary>
    public static OpenFileDialog BuildBrowseDialog() => new()
    {
        Title = "Add apps",
        Multiselect = true,
        Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*",
        DereferenceLinks = false,
        InitialDirectory = BrowseInitialDirectory() ?? "",
    };

    public static string? BrowseInitialDirectory() => AppPickerRules.BrowseInitialDirectory(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        System.IO.Directory.Exists);

    // ---- Handlers ----

    private void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (SearchHint != null)
            SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_ready) ApplyFilter();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        SelectedEntries.Clear();
        SelectedEntries.AddRange(_rows.Where(r => r.IsChecked && !r.AlreadyInFolder).Select(r => r.Entry));
        if (SelectedEntries.Count == 0) return;   // nothing newly checked -> no-op
        DialogResult = true;
    }

    private void Browse_Click(object sender, MouseButtonEventArgs e)
    {
        var dlg = BuildBrowseDialog();
        if (dlg.ShowDialog(this) == true && dlg.FileNames.Length > 0)
        {
            BrowsedFiles = dlg.FileNames;
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }

    private void Card_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { }
    }

    private void ApplyTheme(bool dark)
    {
        void B(string key, string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
            brush.Freeze();
            Resources[key] = brush;
        }
        if (dark)
        {
            B("Fg", "#F2F4F8"); B("FgDim", "#A7AEB9"); B("CardBg", "#F21D2026");
            B("CardBorder", "#2EFFFFFF"); B("CtrlBg", "#242A33"); B("ListBg", "#1E232B");
            B("Accent", "#6E9BFF"); B("AccentText", "#0B0E14"); B("RowHover", "#1AFFFFFF");
        }
        // Light mode uses the XAML defaults.
    }
}
