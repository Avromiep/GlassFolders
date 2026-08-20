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
}
