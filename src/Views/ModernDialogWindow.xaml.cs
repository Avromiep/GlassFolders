using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GlassFolders.Services;

namespace GlassFolders.Views;

public partial class ModernDialogWindow : Window
{
    private bool _ok;

    private ModernDialogWindow(bool dark)
    {
        InitializeComponent();
        ApplyTheme(dark);
        SourceInitialized += (_, _) => Theming.ApplyGlass(this, dark);
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
            B("Accent", "#4F7CF5"); B("AccentText", "#FFFFFF"); B("Danger", "#E5484D");
        }
        else
        {
            B("Fg", "#F2F4F8"); B("FgDim", "#A7AEB9"); B("CardBg", "#26FFFFFF");
            B("CardBorder", "#2EFFFFFF"); B("CtrlBg", "#1CFFFFFF");
            B("Accent", "#6E9BFF"); B("AccentText", "#0B0E14"); B("Danger", "#FF6369");
        }
    }

    // ---- Public API ----

    public static bool Confirm(Window owner, string title, string message,
        string okText = "OK", string cancelText = "Cancel", bool danger = false)
    {
        var dlg = new ModernDialogWindow(Theming.IsDark()) { Owner = owner };
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.OkButton.Content = okText;
        dlg.CancelButton.Content = cancelText;
        if (danger)
            dlg.OkButton.SetResourceReference(BackgroundProperty, "Danger");
        dlg.OkButton.Focus();
        dlg.ShowDialog();
        return dlg._ok;
    }

    public static string? Prompt(Window owner, string title, string message, string initial)
    {
        var dlg = new ModernDialogWindow(Theming.IsDark()) { Owner = owner };
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.OkButton.Content = "OK";
        dlg.InputBox.Visibility = Visibility.Visible;
        dlg.InputBox.Text = initial;
        dlg.Loaded += (_, _) => { dlg.InputBox.Focus(); dlg.InputBox.SelectAll(); };
        dlg.ShowDialog();
        return dlg._ok ? dlg.InputBox.Text.Trim() : null;
    }

    // ---- Handlers ----

    private void Ok_Click(object sender, RoutedEventArgs e) { _ok = true; DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _ok = false; DialogResult = false; }
    private void Card_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _ok = false; DialogResult = false; }
        else if (e.Key == Key.Enter) { _ok = true; DialogResult = true; }
    }
}
