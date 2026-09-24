using System.Collections.Generic;

namespace GlassFolders.Services;

/// <summary>Numeric-aware ("natural") string ordering, matching how Windows Explorer sorts names
/// (so "Server-2" comes before "Server-10").</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();
    public int Compare(string? a, string? b) => NativeMethods.StrCmpLogicalW(a ?? "", b ?? "");
}
