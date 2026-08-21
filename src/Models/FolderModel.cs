using System.IO;

namespace GlassFolders.Models;

/// <summary>One shortcut inside a folder. The .lnk is the source of truth.</summary>
public sealed class ShortcutItem
{
    public required string LnkPath { get; init; }
    public string DisplayName => Path.GetFileNameWithoutExtension(LnkPath);
}

/// <summary>
/// A folder = a real directory of .lnk files plus an order.txt describing display
/// order. Paging is derived by chunking the ordered list 9-per-page.
/// </summary>
public sealed class FolderModel
{
    public const int PageSize = 9;
    public const int DefaultFrostiness = 55;

    /// <summary>
    /// Frostiness (0..100) -> white-veil opacity. Anchored so the default (55) gives the light,
    /// faded-colour look that used to be around 20 — i.e. the scale is stretched: 55 == old ~20.
    /// 0 = nearly clear glass, 55 = light frost, 100 = heavy frost.
    /// </summary>
    public static double TintOpacity(int frostiness)
    {
        int v = Math.Clamp(frostiness, 0, 100);
        return v <= DefaultFrostiness
            ? 0.06 + (v / (double)DefaultFrostiness) * (0.40 - 0.06)
            : 0.40 + ((v - DefaultFrostiness) / (double)(100 - DefaultFrostiness)) * (0.95 - 0.40);
    }

    public required string Name { get; set; }
    public required string DirectoryPath { get; init; }
    public List<ShortcutItem> Items { get; } = new();

    /// <summary>0 = barely-there clear glass, 100 = heavy frost. Default is a middle frost.</summary>
    public int Frostiness { get; set; } = DefaultFrostiness;

    /// <summary>Whether a shortcut for this folder is placed on the desktop.</summary>
    public bool OnDesktop { get; set; } = true;

    /// <summary>Where the panel opens: 0..8 = a 3x3 screen grid (row*3+col); 4 = center.</summary>
    public int PanelPosition { get; set; } = 4;

    public int PageCount => Math.Max(1, (int)Math.Ceiling(Items.Count / (double)PageSize));

    public IEnumerable<ShortcutItem> Page(int index) =>
        Items.Skip(index * PageSize).Take(PageSize);

    public IReadOnlyList<string> FirstPagePaths() =>
        Items.Take(PageSize).Select(i => i.LnkPath).ToList();
}
