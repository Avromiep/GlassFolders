using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GlassFolders.Services;

namespace GlassFolders.Views;

/// <summary>
/// A focused, guided update experience (WhisperText-style): it checks on open, tells you plainly
/// whether you're up to date, and — when there's a new version — downloads it with a progress bar
/// and restarts into the installer. One window, clear states, no hunting through Settings.
/// </summary>
public partial class UpdateWindow : Window
{
    private enum Phase { Checking, UpToDate, Available, Downloading, Error }

    private string? _setupUrl;
    private string? _setupSha256;
    private string? _downloadUrl;
    private bool _busy;

    public UpdateWindow(bool dark)
    {
        InitializeComponent();
        ApplyTheme(dark);
        SourceInitialized += (_, _) => Theming.ApplyGlass(this, dark);
        CurrentVersionText.Text = $"You have version {App.AppVersion}";
        Loaded += async (_, _) => await CheckAsync();
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
            B("Fg", "#1B1E24"); B("FgDim", "#5C636E"); B("CardBg", "#7AFFFFFF");
            B("CardBorder", "#40FFFFFF"); B("CtrlBg", "#66FFFFFF");
            B("Accent", "#4F7CF5"); B("AccentText", "#FFFFFF"); B("TrackBg", "#22000000");
        }
        else
        {
            B("Fg", "#F2F4F8"); B("FgDim", "#A7AEB9"); B("CardBg", "#26FFFFFF");
            B("CardBorder", "#2EFFFFFF"); B("CtrlBg", "#1CFFFFFF");
            B("Accent", "#6E9BFF"); B("AccentText", "#0B0E14"); B("TrackBg", "#33FFFFFF");
        }
    }

    // ---- States ----

    private void SetPhase(Phase p, string status, string? detail = null)
    {
        StatusText.Text = status;
        DetailText.Text = detail ?? "";
        DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        ProgressTrack.Visibility = p == Phase.Downloading ? Visibility.Visible : Visibility.Collapsed;

        switch (p)
        {
            case Phase.Checking:
            case Phase.Downloading:
                CloseButton.Content = "Close";
                ActionButton.Visibility = Visibility.Collapsed;
                break;
            case Phase.UpToDate:
                ActionButton.Visibility = Visibility.Collapsed;
                CloseButton.Content = "Done";
                break;
            case Phase.Available:
                CloseButton.Content = "Close";
                ActionButton.Content = _setupUrl != null ? "Download & install" : "Open download page";
                ActionButton.Visibility = Visibility.Visible;
                ActionButton.IsEnabled = true;
                break;
            case Phase.Error:
                CloseButton.Content = "Close";
                ActionButton.Content = "Try again";
                ActionButton.Visibility = Visibility.Visible;
                ActionButton.IsEnabled = true;
                break;
        }
    }

    private async Task CheckAsync()
    {
        _busy = true;
        SetPhase(Phase.Checking, "Checking for updates…");
        var r = await UpdateService.CheckAsync(App.AppVersion);
        _busy = false;

        switch (r.Status)
        {
            case UpdateStatus.UpToDate:
                SetPhase(Phase.UpToDate, "You're on the latest version.",
                    $"Version {r.LatestVersion} is the newest available.");
                break;
            case UpdateStatus.UpdateAvailable:
                _downloadUrl = r.Url;
                _setupUrl = r.SetupUrl;
                _setupSha256 = r.SetupSha256;
                SetPhase(Phase.Available, "A new version is available!",
                    $"Version {r.LatestVersion} — you have {App.AppVersion}.");
                break;
            case UpdateStatus.NoReleases:
                SetPhase(Phase.Error, "No updates found.",
                    r.Message ?? "There are no published releases yet.");
                break;
            default:
                SetPhase(Phase.Error, "Couldn't check for updates.",
                    r.Message ?? "Please check your internet connection and try again.");
                break;
        }
    }

    private async Task DownloadAndInstallAsync()
    {
        // No installer asset (older release / fork): open the release page instead.
        if (string.IsNullOrEmpty(_setupUrl))
        {
            if (!string.IsNullOrEmpty(_downloadUrl))
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_downloadUrl) { UseShellExecute = true }); } catch { }
            return;
        }

        _busy = true;
        CloseButton.IsEnabled = false;
        SetPhase(Phase.Downloading, "Downloading update…", "Starting…");
        SetProgress(0);

        var progress = new Progress<int>(p =>
        {
            SetProgress(p);
            DetailText.Text = $"{p}%";
            DetailText.Visibility = Visibility.Visible;
        });
        try
        {
            var setup = await AppUpdater.DownloadAsync(_setupUrl, progress,
                System.Threading.CancellationToken.None, _setupSha256);
            SetProgress(100);
            SetPhase(Phase.Downloading, "Installing…", "Glass Folders will restart to finish.");
            AppUpdater.RunInstaller(setup);
            await Task.Delay(700); // let the installer grab the files before we exit
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _busy = false;
            CloseButton.IsEnabled = true;
            SetPhase(Phase.Error, "Update failed.", ex.Message);
        }
    }

    private void SetProgress(int percent)
    {
        double p = Math.Clamp(percent, 0, 100) / 100.0;
        // Fill width tracks the track's actual width.
        ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * p);
    }

    // ---- Test hooks (screenshots only) ----

    internal void TestAvailable(string version)
    {
        _setupUrl = "https://example/GlassFolders-Setup.exe";
        SetPhase(Phase.Available, "A new version is available!",
            $"Version {version} — you have {App.AppVersion}.");
    }

    internal void TestDownloading(int percent)
    {
        SetPhase(Phase.Downloading, "Downloading update…", $"{percent}%");
        CloseButton.IsEnabled = false;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => SetProgress(percent));
    }

    // ---- Handlers ----

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        // "Try again" after an error re-runs the check; otherwise it's the download action.
        if ((string)ActionButton.Content == "Try again") await CheckAsync();
        else await DownloadAndInstallAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) { if (!_busy) Close(); }

    private void Card_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_busy) Close();
    }
}
