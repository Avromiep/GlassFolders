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
    ///
    /// Most icons at the requested (large) size fill their frame and are returned unchanged.
    /// A few apps (e.g. BlackVue) ship a full-bleed *small* icon but a mostly-empty *large*
    /// frame, so a large request comes back as a tiny glyph adrift in transparent padding —
    /// visibly smaller than the same app on the desktop (which renders at ~48). For only those
    /// under-filled cases we fall back to the smaller frame that actually fills and scale it up
    /// to fill the tile, matching the desktop's apparent size. Well-filled icons are untouched.
    /// </summary>
    public static Bitmap? GetIcon(string path, int size)
    {
        var primary = ExtractRaw(path, size);
        if (primary == null) return null;

        var pb = OpaqueBounds(primary);
        double primaryFill = pb.IsEmpty ? 0 : Math.Max(pb.Width, pb.Height) / (double)size;
        if (primaryFill >= 0.60) return primary;   // adequately filled -> exactly as before

        // Under-filled: probe smaller frames (often the properly drawn ones) for a better fill.
        Bitmap? best = null;
        Rectangle bestBox = Rectangle.Empty;
        double bestFill = primaryFill;
        foreach (int fs in new[] { 96, 64, 48, 32 })
        {
            if (fs >= size) continue;
            var cand = ExtractRaw(path, fs);
            if (cand == null) continue;
            var cb = OpaqueBounds(cand);
            double cf = cb.IsEmpty ? 0 : Math.Max(cb.Width, cb.Height) / (double)fs;
            if (cf > bestFill) { best?.Dispose(); best = cand; bestBox = cb; bestFill = cf; }
            else cand.Dispose();
            if (bestFill >= 0.80) break;           // good enough; stop probing
        }

        // Only swap when a smaller frame is clearly better, so we never disturb a normal icon.
        if (best != null && bestFill - primaryFill >= 0.20)
        {
            var normalized = NormalizeFill(best, bestBox, size);
            best.Dispose();
            primary.Dispose();
            return normalized;
        }
        best?.Dispose();
        return primary;
    }

    /// <summary>Raw shell extraction at exactly <paramref name="size"/> (the original GetIcon body).</summary>
    private static Bitmap? ExtractRaw(string path, int size)
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

    /// <summary>Bounding box of pixels whose alpha exceeds a small threshold (Empty if none).</summary>
    private static Rectangle OpaqueBounds(Bitmap bmp, byte alphaThreshold = 12)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
            var row = new byte[Math.Abs(data.Stride)];
            for (int y = 0; y < bmp.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < bmp.Width; x++)
                {
                    if (row[x * 4 + 3] > alphaThreshold)   // BGRA -> alpha is byte 3
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>Draws <paramref name="src"/>'s opaque region (<paramref name="box"/>) scaled to fill
    /// a square <paramref name="outSize"/> canvas with a small even margin — so a padded/undersized
    /// source ends up filling the tile like a normal icon.</summary>
    private static Bitmap NormalizeFill(Bitmap src, Rectangle box, int outSize)
    {
        double margin = outSize * 0.06;
        double avail = outSize - 2 * margin;
        double s = Math.Min(avail / box.Width, avail / box.Height);
        double w = box.Width * s, h = box.Height * s;

        var outBmp = new Bitmap(outSize, outSize, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(outBmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        g.DrawImage(src,
            new RectangleF((float)((outSize - w) / 2), (float)((outSize - h) / 2), (float)w, (float)h),
            new RectangleF(box.X, box.Y, box.Width, box.Height), GraphicsUnit.Pixel);
        return outBmp;
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
