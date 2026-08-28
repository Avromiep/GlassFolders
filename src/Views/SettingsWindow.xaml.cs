using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using GlassFolders.Services;

namespace GlassFolders.Views;

public partial class SettingsWindow : Window
{
    private readonly FolderStore _store;
    private readonly Action _onFoldersChanged;
    private string? _downloadUrl;
    private string? _setupUrl;

    public SettingsWindow(FolderStore store, Action onFoldersChanged)
    {
        _store = store;
        _onFoldersChanged = onFoldersChanged;
        InitializeComponent();
        ApplyTheme(Theming.IsDark());
        SourceInitialized += (_, _) => Theming.ApplyGlass(this, Theming.IsDark());
        VersionText.Text = $"Version {App.AppVersion}";
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
            var setup = await AppUpdater.DownloadAsync(_setupUrl, progress, System.Threading.CancellationToken.None);
            UpdateStatusText.Text = "Installing… Liquid Folders will restart.";
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

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export folders",
            Filter = "Liquid Folders backup (*.json)|*.json",
            FileName = "liquid-folders.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            FolderIO.ExportAll(_store, dlg.FileName);
            BackupStatus.Text = $"Exported to {dlg.FileName}";
        }
        catch (Exception ex) { BackupStatus.Text = "Export failed: " + ex.Message; }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import folders",
            Filter = "Liquid Folders backup (*.json)|*.json|All files (*.*)|*.*",
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
            var dest = System.IO.Path.Combine(dir, $"LiquidFolders-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
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
