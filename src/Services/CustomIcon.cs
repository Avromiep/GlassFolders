using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using static GlassFolders.NativeMethods;

namespace GlassFolders.Services;

/// <summary>
/// Replaces a folder item's icon with an image the user chooses. The image is normalised to a 256px
/// PNG-in-ICO and stored ONLY under <c>%LOCALAPPDATA%\GlassFolders\Icons\_custom</c>; it is then set as
/// the shortcut's icon location. Nothing outside our own data folder is written, the user's source file
/// is only read, and the .lnk's target / arguments / Store-app identity are left untouched — so this
/// works for classic desktop shortcuts and Store/UWP (ID-list) shortcuts alike (a PWA's baked-in tile
/// is overridden by the chosen image).
/// </summary>
public static class CustomIcon
{
    public const int IconPx = 256;
    private const long MaxSourceBytes = 16 * 1024 * 1024;   // sanity cap on the chosen file

    private static readonly string[] Allowed = { ".ico", ".png", ".jpg", ".jpeg", ".bmp" };

    /// <summary>OpenFileDialog filter for the image types we accept.</summary>
    public const string DialogFilter =
        "Image files (*.ico;*.png;*.jpg;*.jpeg;*.bmp)|*.ico;*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*";

    /// <summary>True when the extension is one we can turn into an icon. Pure — testable.</summary>
    public static bool IsSupportedSource(string? path) =>
        !string.IsNullOrEmpty(path) &&
        Array.IndexOf(Allowed, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>Stable, filesystem-safe key for a shortcut: a hex SHA-1 of its lower-cased full path.
    /// Used to name and later find/delete that item's stored custom icon(s). Pure — testable.</summary>
    public static string Key(string lnkPath)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(lnkPath.ToLowerInvariant()));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>The folder our custom icons live in (under the app's Icons root).</summary>
    public static string CustomDir(string iconsRoot) => Path.Combine(iconsRoot, "_custom");

    /// <summary>Applies <paramref name="sourceImagePath"/> as the shortcut's icon. Returns the stored
    /// .ico path on success, or null on any failure (unsupported type, missing file, oversize, write or
    /// COM error). Never throws.</summary>
    public static string? Set(string iconsRoot, string lnkPath, string sourceImagePath)
    {
        try
        {
            if (!IsSupportedSource(sourceImagePath) || !File.Exists(sourceImagePath)) return null;
            if (!File.Exists(lnkPath)) return null;
            var info = new FileInfo(sourceImagePath);
            if (info.Length <= 0 || info.Length > MaxSourceBytes) return null;

            byte[] ico =
                Path.GetExtension(sourceImagePath).Equals(".ico", StringComparison.OrdinalIgnoreCase)
                    ? File.ReadAllBytes(sourceImagePath)   // keep a real .ico's own frames as-is
                    : BuildIco(sourceImagePath);
            if (ico.Length == 0) return null;

            string dir = CustomDir(iconsRoot);
            Directory.CreateDirectory(dir);
            string key = Key(lnkPath);
            // Fresh file name each time so the shell never serves a stale cached thumbnail.
            string dest = Path.Combine(dir, $"{key}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.ico");

            File.WriteAllBytes(dest, ico);
            ShellLink.SetIconLocation(lnkPath, dest, 0);     // preserves target / ID list / identity
            DeleteFor(dir, key, keep: dest);                 // drop this item's previous custom icon(s)
            NotifyShell(lnkPath);
            return dest;
        }
        catch { return null; }
    }

    /// <summary>Clears any custom icon so the shortcut falls back to its app's own icon.</summary>
    public static void Reset(string iconsRoot, string lnkPath)
    {
        try { ShellLink.ClearIconLocation(lnkPath); } catch { }
        try { DeleteFor(CustomDir(iconsRoot), Key(lnkPath), keep: null); } catch { }
        NotifyShell(lnkPath);
    }

    /// <summary>Deletes a shortcut's stored custom icon(s) — called when the item leaves a folder so we
    /// don't leak files. Does not touch the .lnk (it's being removed anyway).</summary>
    public static void Forget(string iconsRoot, string lnkPath)
    {
        try { DeleteFor(CustomDir(iconsRoot), Key(lnkPath), keep: null); } catch { }
    }

    private static void DeleteFor(string dir, string key, string? keep)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, key + "_*.ico"))
            if (keep == null || !string.Equals(f, keep, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(f); } catch { }
    }

    /// <summary>Renders a png/jpg/bmp into a 256px square (aspect preserved, centered, small margin)
    /// and wraps it as a PNG-in-ICO.</summary>
    private static byte[] BuildIco(string sourceImagePath)
    {
        using var src = (Bitmap)Image.FromFile(sourceImagePath);
        using var canvas = new Bitmap(IconPx, IconPx, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            double margin = IconPx * 0.04;
            double avail = IconPx - 2 * margin;
            double s = Math.Min(avail / src.Width, avail / src.Height);
            double w = src.Width * s, h = src.Height * s;
            g.DrawImage(src, (float)((IconPx - w) / 2), (float)((IconPx - h) / 2), (float)w, (float)h);
        }
        using var ms = new MemoryStream();
        canvas.Save(ms, ImageFormat.Png);
        return IcoWriter.WrapPng(ms.ToArray(), IconPx, IconPx);
    }

    private static void NotifyShell(string lnkPath)
    {
        IntPtr p = IntPtr.Zero;
        try
        {
            p = Marshal.StringToHGlobalUni(lnkPath);
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, p, IntPtr.Zero);
        }
        catch { }
        finally { if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); }
    }
}
