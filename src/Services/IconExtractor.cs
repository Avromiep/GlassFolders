using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static GlassFolders.NativeMethods;

namespace GlassFolders.Services;

/// <summary>
/// Extracts a high-quality, alpha-correct bitmap for any file/shortcut/exe
/// using IShellItemImageFactory. Falls back to the associated icon on failure.
/// </summary>
public static class IconExtractor
{
    /// <summary>
    /// Returns a 32bpp ARGB bitmap of the shell icon for <paramref name="path"/> at the
    /// requested square size. Caller owns (must Dispose) the returned bitmap.
    /// </summary>
    public static Bitmap? GetIcon(string path, int size)
    {
        try
        {
            object shellItemObj;
            var iid = IID_IShellItemImageFactory;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out shellItemObj);
            var factory = (IShellItemImageFactory)shellItemObj;

            // ICONONLY: never fall back to a document thumbnail; we want the app/shortcut icon.
            // BIGGERSIZEOK: allow the shell to hand back a larger source we downscale ourselves.
            int hr = factory.GetImage(new SIZE(size, size),
                SIIGBF.IconOnly | SIIGBF.BiggerSizeOk | SIIGBF.ScaleUp,
                out IntPtr hBitmap);

            Marshal.ReleaseComObject(factory);

            if (hr != 0 || hBitmap == IntPtr.Zero)
                return null;

            try
            {
                return HBitmapToArgb(hBitmap, size);
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Copies the pixels out of the shell's DIB section (which carries premultiplied alpha)
    /// into a standalone managed ARGB bitmap of exactly <paramref name="targetSize"/>.
    /// </summary>
    private static Bitmap? HBitmapToArgb(IntPtr hBitmap, int targetSize)
    {
        var ds = new DIBSECTION();
        int read = GetObject(hBitmap, Marshal.SizeOf<DIBSECTION>(), ref ds);
        if (read == 0)
            return null;

        int w = ds.dsBm.bmWidth;
        int h = ds.dsBm.bmHeight;
        if (w <= 0 || h <= 0 || ds.dsBm.bmBits == IntPtr.Zero)
            return null;

        // The DIB is 32bpp premultiplied ARGB, but its row order depends on the source:
        // biHeight < 0 => top-down, biHeight > 0 => bottom-up. Point scan0 at the first
        // visual row and sign the stride accordingly so icons never come out flipped.
        int stride = ds.dsBm.bmWidthBytes;
        IntPtr scan0 = ds.dsBm.bmBits;
        if (ds.dsBmih.biHeight > 0)
        {
            scan0 = IntPtr.Add(ds.dsBm.bmBits, (h - 1) * stride);
            stride = -stride;
        }
        using var wrapped = new Bitmap(w, h, stride, PixelFormat.Format32bppPArgb, scan0);

        var result = new Bitmap(targetSize, targetSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(wrapped, new Rectangle(0, 0, targetSize, targetSize),
                0, 0, w, h, GraphicsUnit.Pixel);
        }
        return result;
    }
}
