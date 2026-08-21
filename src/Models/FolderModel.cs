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
    public const int DefaultFrostiness = 45;

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
