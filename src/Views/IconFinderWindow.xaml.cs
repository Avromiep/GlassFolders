using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassFolders.Services;

namespace GlassFolders.Views;

/// <summary>
/// Finds an app's real icon on the web (only when the user runs "Find app icon"), shows the best match
/// in a preview, and lets the user accept it, cycle to another candidate, or cancel. Nothing is applied
/// until the user clicks Use this icon; the chosen image is exposed as <see cref="ChosenBytes"/> for the
/// caller to run through the custom-icon path.
/// </summary>
public partial class IconFinderWindow : Window
{
    private readonly string _appName;
    private readonly CancellationTokenSource _cts = new();
    private List<IconCandidate> _candidates = new();
    private int _index;

    /// <summary>The image bytes the user accepted (null if they cancelled).</summary>
    public byte[]? ChosenBytes { get; private set; }

    public IconFinderWindow(string appName, bool dark)
    {
        _appName = appName;
        InitializeComponent();
        ApplyTheme(dark);
        SourceInitialized += (_, _) => Theming.ApplyGlass(this, dark);
        HeaderText.Text = $"Find icon for {appName}";
        StatusText.Text = "Searching the web for the app's icon…";
        Loaded += async (_, _) => await SearchAsync();
    }

    private async Task SearchAsync()
    {
        try { _candidates = await IconFinder.FindAsync(_appName, _cts.Token); }
        catch { _candidates = new(); }

        if (_cts.IsCancellationRequested) return;

        if (_candidates.Count == 0)
        {
            StatusText.Text = "Couldn't find an icon for this app online. You can use Change icon… to pick "
                            + "an image yourself.";
            PreviewPanel.Visibility = Visibility.Collapsed;
            CaptionText.Visibility = Visibility.Collapsed;
            UseButton.Visibility = Visibility.Collapsed;
            AnotherButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Close";
            return;
        }

        _index = 0;
        StatusText.Text = "Here's the best match — use it, or try another.";
        PreviewPanel.Visibility = Visibility.Visible;
        CaptionText.Visibility = Visibility.Visible;
        UseButton.Visibility = Visibility.Visible;
        AnotherButton.Visibility = _candidates.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        var c = _candidates[_index];
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(c.Bytes, writable: false);
            img.EndInit();
            img.Freeze();
            PreviewImage.Source = img;
        }
        catch { PreviewImage.Source = null; }

        CaptionText.Text = _candidates.Count > 1
            ? $"{c.SourceLabel} · {c.Width}×{c.Height} · {_index + 1} of {_candidates.Count}"
            : $"{c.SourceLabel} · {c.Width}×{c.Height}";
    }

    private void Another_Click(object sender, RoutedEventArgs e)
    {
        if (_candidates.Count == 0) return;
        _index = (_index + 1) % _candidates.Count;
        ShowCurrent();
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (_candidates.Count == 0) return;
        ChosenBytes = _candidates[_index].Bytes;
        _cts.Cancel();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { _cts.Cancel(); Close(); }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _cts.Cancel(); Close(); }
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
        if (!dark)
        {
            B("Fg", "#1B1E24"); B("FgDim", "#5C636E"); B("CardBg", "#7AFFFFFF");
            B("CardBorder", "#40FFFFFF"); B("CtrlBg", "#66FFFFFF");
            B("Accent", "#4F7CF5"); B("AccentText", "#FFFFFF"); B("PreviewBg", "#14000000");
        }
        else
        {
            B("Fg", "#F2F4F8"); B("FgDim", "#A7AEB9"); B("CardBg", "#26FFFFFF");
            B("CardBorder", "#2EFFFFFF"); B("CtrlBg", "#1CFFFFFF");
            B("Accent", "#6E9BFF"); B("AccentText", "#0B0E14"); B("PreviewBg", "#22FFFFFF");
        }
    }
}
