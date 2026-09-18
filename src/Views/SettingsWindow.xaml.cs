using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using GlassFolders.Services;

namespace GlassFolders.Views;

public partial class SettingsWindow : Window
{
    private readonly FolderStore _store;
    private readonly Action _onFoldersChanged;
    private string? _downloadUrl;
    private string? _setupUrl;
    private string? _setupSha256;
    private bool _loaded;

    public SettingsWindow(FolderStore store, Action onFoldersChanged)
    {
        _store = store;
        _onFoldersChanged = onFoldersChanged;
        InitializeComponent();
        ApplyTheme(Theming.IsDark());
        SourceInitialized += (_, _) => Theming.ApplyGlass(this, Theming.IsDark());
        VersionText.Text = $"Version {App.AppVersion}";
        StartupCheck.IsChecked = StartupManager.IsEnabled;
        UpdateAppearanceUI();
        _loaded = true;
    }

    // ---- File folder appearance (frosted vs plain, with live preview) ----

    private void ChooseFrosted(object sender, System.Windows.Input.MouseButtonEventArgs e)
    { AppSettings.PlainFileList = false; UpdateAppearanceUI(); }

    private void ChoosePlain(object sender, System.Windows.Input.MouseButtonEventArgs e)
    { AppSettings.PlainFileList = true; UpdateAppearanceUI(); }

    private void UpdateAppearanceUI()
    {
        bool plain = AppSettings.PlainFileList;
        var accent = (Brush)Resources["Accent"];
        var accentText = (Brush)Resources["AccentText"];
        var ctrl = (Brush)Resources["CtrlBg"];
        var fg = (Brush)Resources["Fg"];
        FrostedChip.Background = plain ? ctrl : accent;
        PlainChip.Background = plain ? accent : ctrl;
        FrostedChipText.Foreground = plain ? fg : accentText;
        PlainChipText.Foreground = plain ? accentText : fg;
        BuildAppearancePreview(plain);
    }

    private void BuildAppearancePreview(bool plain)
    {
        bool dark = Theming.IsDark();
        var wall = WallpaperBrush();

        var host = new Grid
        {
            Background = wall ?? (Brush)new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#3B4252")!,
                (Color)ColorConverter.ConvertFromString("#20242C")!, 45),
        };

        var panel = new Border
        {
            Width = 300,
            Height = 150,
            CornerRadius = new CornerRadius(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            Clip = new RectangleGeometry(new Rect(0, 0, 300, 150), 16, 16),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.3, Color = Colors.Black },
        };
        var inner = new Grid();
        Brush fg;
        if (plain)
        {
            panel.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#1E2024" : "#F7F8FA")!);
            panel.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#3A3D42" : "#E2E5EA")!);
            fg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#F2F5F7" : "#15181D")!);
        }
        else
        {
            if (wall?.ImageSource != null)
                inner.Children.Add(new Image
                {
                    Source = wall.ImageSource,
                    Stretch = Stretch.UniformToFill,
                    Effect = new BlurEffect { Radius = 22, KernelType = KernelType.Gaussian },
                });
            inner.Children.Add(new Border { Background = Brushes.White, Opacity = 0.5 });
            panel.Background = Brushes.Transparent;
            panel.BorderBrush = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
            fg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#15181D")!);
        }

        var content = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        content.Children.Add(new TextBlock
        {
            Text = "RDP Servers",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = fg,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        });
        foreach (var n in new[] { "ACME-DC01", "ACME-SQL", "Finance-Server" })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 3, 2, 3) };
            row.Children.Add(new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(4),
                Background = (Brush)Resources["Accent"],
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = n,
                Foreground = fg,
                FontSize = 12.5,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            content.Children.Add(row);
        }
        inner.Children.Add(content);
        panel.Child = inner;
        host.Children.Add(panel);
        PreviewHost.Child = host;
    }

    private static ImageBrush? WallpaperBrush()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key?.GetValue("WallPaper") as string is not { Length: > 0 } path
                || !System.IO.File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 600;
            bmp.EndInit();
            bmp.Freeze();
            return new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
        }
        catch { return null; }
    }

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        StartupManager.SetEnabled(StartupCheck.IsChecked == true);
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
            B("Fg", "#1B1E24"); B("FgDim", "#5C636E"); B("CardBg", "#6EFFFFFF");
            B("CardBorder", "#3CFFFFFF"); B("CtrlBg", "#66FFFFFF");
            B("Accent", "#4F7CF5"); B("AccentText", "#FFFFFF");
        }
        else
        {
            B("Fg", "#F2F4F8"); B("FgDim", "#A7AEB9"); B("CardBg", "#24FFFFFF");
            B("CardBorder", "#2EFFFFFF"); B("CtrlBg", "#1CFFFFFF");
            B("Accent", "#6E9BFF"); B("AccentText", "#0B0E14");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Updates ----

    /// <summary>Starts an update check programmatically (used by the tray "Check for updates").</summary>
    public void BeginUpdateCheck() => Check_Click(this, new RoutedEventArgs());

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        DownloadButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Checking…";
        var r = await UpdateService.CheckAsync(App.AppVersion);
        CheckButton.IsEnabled = true;

        switch (r.Status)
        {
            case UpdateStatus.UpToDate:
                UpdateStatusText.Text = $"You're up to date (latest {r.LatestVersion}).";
                break;
            case UpdateStatus.UpdateAvailable:
                UpdateStatusText.Text = $"Update available: {r.LatestVersion}.";
                _downloadUrl = r.Url;
                _setupUrl = r.SetupUrl;
                _setupSha256 = r.SetupSha256;
                DownloadButton.Content = _setupUrl != null ? "Download and install" : "Open download page";
                DownloadButton.Visibility = Visibility.Visible;
                break;
            case UpdateStatus.NoReleases:
                UpdateStatusText.Text = r.Message ?? "No releases found.";
                break;
            default:
                UpdateStatusText.Text = $"Couldn't check for updates: {r.Message}";
                break;
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        // No installer asset (older release / fork): fall back to opening the release page.
        if (string.IsNullOrEmpty(_setupUrl))
        {
            if (!string.IsNullOrEmpty(_downloadUrl))
                try { Process.Start(new ProcessStartInfo(_downloadUrl) { UseShellExecute = true }); } catch { }
            return;
        }

        DownloadButton.IsEnabled = false;
        CheckButton.IsEnabled = false;
        var progress = new Progress<int>(p => UpdateStatusText.Text = $"Downloading update… {p}%");
        try
        {
            var setup = await AppUpdater.DownloadAsync(_setupUrl, progress,
                System.Threading.CancellationToken.None, _setupSha256);
            UpdateStatusText.Text = "Installing… Glass Folders will restart.";
            AppUpdater.RunInstaller(setup);
            await System.Threading.Tasks.Task.Delay(700); // let the installer start before we release the files
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Update failed: " + ex.Message;
            DownloadButton.IsEnabled = true;
            CheckButton.IsEnabled = true;
        }
    }

    // ---- Import / export ----

    /// <summary>Dedicated, discoverable folder for exports: Documents\Glass Folders Exports.</summary>
    private static string ExportsDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Glass Folders Exports");

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Save straight into the exports folder (no Save dialog); a timestamped name so
            // repeat exports don't overwrite each other. Tell the user the exact path.
            System.IO.Directory.CreateDirectory(ExportsDir);
            var dest = System.IO.Path.Combine(ExportsDir, $"glass-folders-{DateTime.Now:yyyy-MM-dd_HHmmss}.json");
            FolderIO.ExportAll(_store, dest);
            BackupStatus.Text = $"Exported to: {dest}";
        }
        catch (Exception ex) { BackupStatus.Text = "Export failed: " + ex.Message; }
    }

    private void OpenExports_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ExportsDir);
            Process.Start(new ProcessStartInfo(ExportsDir) { UseShellExecute = true });
        }
        catch (Exception ex) { BackupStatus.Text = "Couldn't open the folder: " + ex.Message; }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import folders",
            Filter = "Glass Folders backup (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var s = FolderIO.Import(_store, dlg.FileName);
            BackupStatus.Text =
                $"Imported {s.Folders} folder(s): {s.AppsAdded} app(s) re-linked, {s.AppsSkipped} not installed here.";
            _onFoldersChanged();
        }
        catch (Exception ex) { BackupStatus.Text = "Import failed: " + ex.Message; }
    }

    // ---- Diagnostics ----

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Save a timestamped copy into a dedicated "saved logs" folder (not the Desktop),
            // then open Explorer with it selected so it's easy to attach.
            var dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Diag.Path)!, "saved");
            System.IO.Directory.CreateDirectory(dir);
            var dest = System.IO.Path.Combine(dir, $"GlassFolders-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            if (Diag.SaveCopyTo(dest))
            {
                DiagStatus.Text = $"Saved to your logs folder: {System.IO.Path.GetFileName(dest)}";
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dest}\"")); } catch { }
            }
            else
                DiagStatus.Text = "No log yet — open a folder or two first, then try again.";
        }
        catch (Exception ex) { DiagStatus.Text = "Couldn't save the log: " + ex.Message; }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Diag.Path)!;
            System.IO.Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { DiagStatus.Text = "Couldn't open the folder: " + ex.Message; }
    }
}
