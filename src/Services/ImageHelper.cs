using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace GlassFolders.Services;

public static class ImageHelper
{
    // Shell icon extraction is slow (tens of ms each); a folder re-extracted all 9 tile icons
    // on every open, which is the visible "open delay". Cache the frozen ImageSource per
    // (path, size, shortcut-mtime) so repeat opens are instant. Frozen bitmaps are thread-safe,
    // so we can also pre-warm this from a background thread at startup.
    private static readonly ConcurrentDictionary<string, BitmapImage> _iconCache = new();

    /// <summary>Loads a shell icon for a path as a WPF ImageSource at the given size (cached).</summary>
    public static BitmapImage? LoadIcon(string path, int size)
    {
        var key = CacheKey(path, size);
        if (_iconCache.TryGetValue(key, out var cached)) return cached;

        using var bmp = IconExtractor.GetIcon(path, size);
        if (bmp == null) return null;
        var img = ToImageSource(bmp);
        _iconCache[key] = img;
        return img;
    }

    /// <summary>Warms the cache for a set of shortcuts (call off the UI thread at startup).</summary>
    public static void Prewarm(IEnumerable<string> paths, int size)
    {
        foreach (var p in paths)
            try { LoadIcon(p, size); } catch { }
    }

    private static string CacheKey(string path, int size)
    {
        long ticks = 0;
        try { ticks = File.GetLastWriteTimeUtc(path).Ticks; } catch { }
        return $"{path}|{size}|{ticks}";
    }

    public static BitmapImage ToImageSource(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }
}
