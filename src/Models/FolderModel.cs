using System.IO;

namespace GlassFolders.Models;

/// <summary>How a folder presents its contents when opened.</summary>
public enum FolderView
{
    /// <summary>iOS/Android-style paged 3x3 icon grid (the default, best for apps).</summary>
    Grid,
    /// <summary>A vertical, Explorer-style list that grows in height (best for files, e.g. RDP).</summary>
    List,
}

/// <summary>Sort order for a file-list folder.</summary>
public enum FolderSort
{
    /// <summary>Keep the order items were added/dragged in (the default).</summary>
    Custom,
    NameAsc,
    NameDesc,
    /// <summary>By the target file's last-modified time, newest first.</summary>
    ModifiedNewest,
    ModifiedOldest,
}

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

    /// <summary>Grid (app tiles) or List (Explorer-style file list that grows in height).</summary>
    public FolderView View { get; set; } = FolderView.Grid;

    /// <summary>Sort order for the file list. Default = Custom (the order items were added).</summary>
    public FolderSort Sort { get; set; } = FolderSort.Custom;

    /// <summary>Which monitor the panel opens on. Null/empty = "same monitor as the folder icon"
    /// (the default). Otherwise the chosen monitor's device name (e.g. \\.\DISPLAY3).</summary>
    public string? PanelMonitor { get; set; }

    /// <summary>The chosen monitor's bounds "L,T,W,H" at the time it was picked — a fallback used
    /// to re-find the monitor if its device name changed; if it can't be found we revert to the
    /// folder's own monitor.</summary>
    public string? PanelMonitorRect { get; set; }

    public int PageCount => Math.Max(1, (int)Math.Ceiling(Items.Count / (double)PageSize));

    public IEnumerable<ShortcutItem> Page(int index) =>
        Items.Skip(index * PageSize).Take(PageSize);

    public IReadOnlyList<string> FirstPagePaths() =>
        Items.Take(PageSize).Select(i => i.LnkPath).ToList();
}
