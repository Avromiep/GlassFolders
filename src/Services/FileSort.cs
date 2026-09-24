using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GlassFolders.Services;

/// <summary>Orders a set of file PATHS for a bulk drag-in. Windows hands dropped files in an
/// arbitrary order, so a batch dropped together is added in natural (Explorer-style) name order —
/// a deterministic, sensible starting order. Ordering across separate drops is the folder's own
/// add order ("As I added them"), and the Sort dropdown re-orders the view (name / date added /
/// date modified) on top of that.</summary>
public static class FileSort
{
    public static IEnumerable<string> OrderPaths(IEnumerable<string> paths) =>
        paths.OrderBy(Path.GetFileName, NaturalStringComparer.Instance);
}
