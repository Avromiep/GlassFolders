using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace GlassFolders.Services;

/// <summary>
/// Renders the "closed folder" composite icon: a frosted, translucent rounded panel
/// with a 3x3 grid of the real first-page app icons painted on top, then emits a
/// multi-resolution .ico. Translucent pixels keep real alpha, so the desktop wallpaper
/// shows through — the closest a static desktop icon can get to frosted glass.
/// </summary>
public static class IconComposer
{
    // Frame sizes baked into the .ico. Desktop uses 48/256; smaller ones cover
    // taskbar/details views.
    private static readonly int[] Sizes = { 256, 128, 64, 48, 32, 16 };

    // Source icon resolution we extract once per item, then downscale per cell.
    private const int SourceRes = 128;

    /// <summary>
    /// Builds and writes the composite .ico for the given first-page shortcut paths
    /// (max 9 are used). Returns false if nothing could be rendered.
    /// </summary>
    public static bool BuildIcon(IReadOnlyList<string> firstPagePaths, string icoOutputPath)
    {
        var sources = new List<Bitmap>();
        try
        {
            foreach (var p in firstPagePaths.Take(9))
            {
                var bmp = IconExtractor.GetIcon(p, SourceRes);
                if (bmp != null)
                    sources.Add(bmp);
            }

            var frames = new List<Bitmap>();
            try
            {
                foreach (var size in Sizes)
                    frames.Add(RenderFrame(size, sources));

                Directory.CreateDirectory(Path.GetDirectoryName(icoOutputPath)!);
                IcoWriter.Write(icoOutputPath, frames);
                return true;
            }
            finally
            {
                foreach (var f in frames) f.Dispose();
            }
        }
        finally
        {
            foreach (var s in sources) s.Dispose();
        }
    }

    /// <summary>Builds the multi-resolution launcher/app icon (frosted tile + gear).</summary>
    public static bool BuildAppIcon(string icoOutputPath)
    {
        var frames = new List<Bitmap>();
        try
        {
            foreach (var size in Sizes) frames.Add(RenderAppFrame(size));
            Directory.CreateDirectory(Path.GetDirectoryName(icoOutputPath)!);
            IcoWriter.Write(icoOutputPath, frames);
            return true;
        }
        finally
        {
            foreach (var f in frames) f.Dispose();
        }
    }

    /// <summary>Renders a single composite frame to a standalone bitmap (for previews/tests).</summary>
    public static Bitmap RenderPreview(IReadOnlyList<string> firstPagePaths, int size)
    {
        var sources = new List<Bitmap>();
        try
        {
            foreach (var p in firstPagePaths.Take(9))
            {
                var bmp = IconExtractor.GetIcon(p, SourceRes);
                if (bmp != null) sources.Add(bmp);
            }
            return RenderFrame(size, sources);
        }
        finally
        {
            foreach (var s in sources) s.Dispose();
        }
    }

    private static Bitmap NewCanvas(int size, out Graphics g)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        return bmp;
    }

    /// <summary>Draws the frosted rounded panel and returns its rectangle.</summary>
    private static RectangleF DrawFrostedPanel(Graphics g, int size)
    {
        float inset = size * 0.03f;
        var panelRect = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
        float radius = size * 0.22f;
        using var panelPath = RoundedRect(panelRect, radius);

        // Translucent light fill (alpha < 255 lets wallpaper through -> pseudo-frost).
        using (var fill = new LinearGradientBrush(panelRect,
                   Color.FromArgb(165, 255, 255, 255),
                   Color.FromArgb(120, 236, 240, 245),
                   LinearGradientMode.Vertical))
            g.FillPath(fill, panelPath);

        // Soft inner top highlight for a glassy sheen.
        var highlightRect = new RectangleF(panelRect.X, panelRect.Y, panelRect.Width, panelRect.Height * 0.5f);
        using (var highlight = new LinearGradientBrush(highlightRect,
                   Color.FromArgb(70, 255, 255, 255),
                   Color.FromArgb(0, 255, 255, 255),
                   LinearGradientMode.Vertical))
        {
            var clip = g.Clip;
            g.SetClip(panelPath);
            g.FillRectangle(highlight, highlightRect);
            g.Clip = clip;
        }

        using (var border = new Pen(Color.FromArgb(150, 255, 255, 255), Math.Max(1f, size / 128f)))
            g.DrawPath(border, panelPath);

        return panelRect;
    }

    private static Bitmap RenderFrame(int size, List<Bitmap> sources)
    {
        var bmp = NewCanvas(size, out var g);
        try
        {
            var panelRect = DrawFrostedPanel(g, size);
            if (sources.Count == 0) return bmp;

            // Below ~40px a 3x3 grid is unreadable; degrade gracefully.
            int cols = size >= 44 ? 3 : (size >= 28 ? 2 : 1);
            DrawGrid(g, panelRect, sources, cols);
            return bmp;
        }
        finally { g.Dispose(); }
    }

    private static Bitmap RenderAppFrame(int size)
    {
        var bmp = NewCanvas(size, out var g);
        try
        {
            var panelRect = DrawFrostedPanel(g, size);

            // Cool the app tile with an icy blue wash so it reads as frost, not plain glass.
            float radius = size * 0.22f;
            using (var panelPath = RoundedRect(panelRect, radius))
            using (var wash = new LinearGradientBrush(panelRect,
                       Color.FromArgb(70, 150, 205, 240),
                       Color.FromArgb(36, 74, 140, 205),
                       LinearGradientMode.Vertical))
            {
                var clip = g.Clip;
                g.SetClip(panelPath);
                g.FillRectangle(wash, panelRect);
                g.Clip = clip;
            }

            DrawSnowflake(g, panelRect, size);
            return bmp;
        }
        finally { g.Dispose(); }
    }

    /// <summary>A six-armed frost crystal centered in the tile — the app/launcher icon.</summary>
    private static void DrawSnowflake(Graphics g, RectangleF panel, int size)
    {
        float cx = panel.X + panel.Width / 2f;
        float cy = panel.Y + panel.Height / 2f;
        float armLen = panel.Width * 0.34f;
        float branchLen = armLen * 0.30f;
        // The finer branch detail vanishes below ~40px; keep the bare star so it still reads.
        bool detailed = size >= 40;

        PointF P(float len, float angDeg)
        {
            double a = angDeg * Math.PI / 180.0;
            return new PointF(cx + len * (float)Math.Cos(a), cy + len * (float)Math.Sin(a));
        }

        void Flake(Pen pen)
        {
            for (int k = 0; k < 6; k++)
            {
                float arm = k * 60f;
                g.DrawLine(pen, new PointF(cx, cy), P(armLen, arm));

                if (!detailed) continue;
                foreach (float pos in new[] { armLen * 0.46f, armLen * 0.74f })
                {
                    var b = P(pos, arm);
                    foreach (int side in new[] { -1, 1 })
                    {
                        double ba = (arm + side * 38f) * Math.PI / 180.0;
                        g.DrawLine(pen, b,
                            new PointF(b.X + branchLen * (float)Math.Cos(ba),
                                       b.Y + branchLen * (float)Math.Sin(ba)));
                    }
                }
            }
        }

        float mainW = Math.Max(1.4f, panel.Width * 0.030f);

        // Soft blue glow underneath, then the crisp icy-white crystal on top.
        using (var glow = new Pen(Color.FromArgb(120, 120, 185, 230), mainW * 2.1f)
        { StartCap = LineCap.Round, EndCap = LineCap.Round })
            Flake(glow);
        using (var main = new Pen(Color.FromArgb(245, 244, 250, 255), mainW)
        { StartCap = LineCap.Round, EndCap = LineCap.Round })
            Flake(main);

        // Center hub.
        float hub = mainW * 1.7f;
        using (var hb = new SolidBrush(Color.FromArgb(255, 248, 252, 255)))
            g.FillEllipse(hb, cx - hub, cy - hub, hub * 2, hub * 2);
    }

    private static void DrawGrid(Graphics g, RectangleF panel, List<Bitmap> sources, int cols)
    {
        int cells = cols * cols;
        float pad = panel.Width * 0.14f;
        var grid = new RectangleF(panel.X + pad, panel.Y + pad,
                                  panel.Width - 2 * pad, panel.Height - 2 * pad);
        float gap = grid.Width * 0.10f;
        float cell = (grid.Width - gap * (cols - 1)) / cols;

        for (int i = 0; i < Math.Min(cells, sources.Count); i++)
        {
            int r = i / cols;
            int c = i % cols;
            float x = grid.X + c * (cell + gap);
            float y = grid.Y + r * (cell + gap);
            g.DrawImage(sources[i], new RectangleF(x, y, cell, cell));
        }
    }

    internal static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2f);
        float d = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
