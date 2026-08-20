using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace GlassFolders.Services;

public static class ImageHelper
{
    /// <summary>Loads a shell icon for a path as a WPF ImageSource at the given size.</summary>
    public static BitmapImage? LoadIcon(string path, int size)
    {
        using var bmp = IconExtractor.GetIcon(path, size);
        return bmp == null ? null : ToImageSource(bmp);
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
