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

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_downloadUrl)) return;
        try { Process.Start(new ProcessStartInfo(_downloadUrl) { UseShellExecute = true }); } catch { }
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
}
