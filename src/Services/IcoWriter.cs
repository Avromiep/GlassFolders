using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace GlassFolders.Services;

/// <summary>
/// Writes a multi-resolution .ico file whose frames are PNG-compressed.
/// PNG-in-ICO is supported at every size on Windows Vista+ (we target Win10/11),
/// which keeps this writer tiny and preserves full 32-bit alpha in every frame.
/// </summary>
public static class IcoWriter
{
    public static void Write(string path, IEnumerable<Bitmap> framesSource)
    {
        var frames = framesSource.ToList();

        // Encode each frame to PNG up front.
        var pngs = new List<byte[]>();
        foreach (var bmp in frames)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            pngs.Add(ms.ToArray());
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        // ICONDIR
        bw.Write((ushort)0);            // reserved
        bw.Write((ushort)1);            // type = icon
        bw.Write((ushort)pngs.Count);   // image count

        // ICONDIRENTRY table (16 bytes each), then image data follows.
        int offset = 6 + pngs.Count * 16;
        // We need each frame's pixel size; re-read from the bitmaps in order.
        var sizes = new List<int>();
        foreach (var bmp in frames)
            sizes.Add(bmp.Width);

        for (int i = 0; i < pngs.Count; i++)
        {
            int size = sizes[i];
            byte dim = (byte)(size >= 256 ? 0 : size); // 0 means 256
            bw.Write(dim);              // width
            bw.Write(dim);              // height
            bw.Write((byte)0);          // palette count
            bw.Write((byte)0);          // reserved
            bw.Write((ushort)1);        // color planes
            bw.Write((ushort)32);       // bits per pixel
            bw.Write((uint)pngs[i].Length);
            bw.Write((uint)offset);
            offset += pngs[i].Length;
        }

        foreach (var png in pngs)
            bw.Write(png);
    }

    /// <summary>Offset of the image bytes in a single-image .ico: 6-byte ICONDIR + one 16-byte entry.</summary>
    public const int ImageOffset = 22;

    /// <summary>Wraps already-encoded PNG bytes as a one-image .ico in memory (no System.Drawing needed),
    /// so the container layout is verifiable under --fixtest. <paramref name="width"/>/<paramref name="height"/>
    /// are the PNG's pixel dimensions (1..256); 256 is written as 0 in the directory entry, per the ICO
    /// convention. Throws on invalid input. Used by the custom-icon feature.</summary>
    public static byte[] WrapPng(byte[] png, int width, int height)
    {
        if (png == null || png.Length == 0)
            throw new ArgumentException("PNG data is empty.", nameof(png));
        if (width < 1 || width > 256)
            throw new ArgumentOutOfRangeException(nameof(width), "Icon width must be 1..256.");
        if (height < 1 || height > 256)
            throw new ArgumentOutOfRangeException(nameof(height), "Icon height must be 1..256.");

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        // ICONDIR
        bw.Write((short)0);                                   // reserved, must be 0
        bw.Write((short)1);                                   // image type: 1 = icon
        bw.Write((short)1);                                   // number of images
        // ICONDIRENTRY
        bw.Write((byte)(width == 256 ? 0 : width));           // width  (0 means 256)
        bw.Write((byte)(height == 256 ? 0 : height));         // height (0 means 256)
        bw.Write((byte)0);                                    // palette size (0 for true color)
        bw.Write((byte)0);                                    // reserved
        bw.Write((short)1);                                   // color planes
        bw.Write((short)32);                                  // bits per pixel
        bw.Write(png.Length);                                 // size of the image data
        bw.Write(ImageOffset);                                // offset of the image data
        bw.Write(png);                                        // the PNG itself
        bw.Flush();
        return ms.ToArray();
    }
}
