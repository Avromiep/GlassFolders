using System.Text.RegularExpressions;

namespace GlassFolders.Services;

/// <summary>Where a pickable app came from.</summary>
public enum AppSource { StartMenu, AppsFolder }

/// <summary>One installed app offered in the picker. Plain data — no COM, no Win32 — so the merge,
/// search and identity rules can be unit-tested. <see cref="Aumid"/> is set for packaged (Store/UWP)
/// apps and some Start Menu entries; <see cref="TargetPath"/>/<see cref="Arguments"/> describe a
/// classic shortcut; <see cref="LnkPath"/> is the source .lnk for a Start Menu entry (copied as-is).</summary>
public sealed record AppEntry(
    string Name,
    AppSource Source,
    string? Aumid = null,
    string? TargetPath = null,
    string? Arguments = null,
    string? LnkPath = null);

/// <summary>
/// Pure decision logic for the installed-apps picker: identity/dedupe, search filtering, the Browse
/// start folder, the packaged-AUMID validator and the explorer.exe fallback shortcut. No Win32 and
/// no state, so every rule is directly testable under --fixtest.
/// </summary>
public static class AppPickerRules
{
    // Packaged-app AUMID: Family_PublisherId!AppId. Validated before it can ever reach a shortcut
    // argument. ASCII only by construction (the character classes exclude spaces, quotes, commas,
    // slashes, backslashes, colons, control chars and non-ASCII letters); total length capped too.
    // \z (not $) so a trailing newline can't sneak through; the family must start with a letter or
    // digit so a leading '-' or '.' is rejected (real AUMIDs start with the publisher name).
    private static readonly Regex AumidPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9.-]{2,49}_[a-z0-9]{13}![A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)*\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>True only for a well-formed packaged-app AUMID (<= 130 chars). Everything that ends up
    /// in the explorer.exe fallback argument must pass this first.</summary>
    public static bool IsValidPackagedAumid(string? aumid) =>
        !string.IsNullOrEmpty(aumid) && aumid.Length <= 130 && AumidPattern.IsMatch(aumid);

    // The packaged-app signature: PackageFamily_<13-char publisher id>!AppId. Looser than the strict
    // validator above — it only recognises a Store/UWP app so AppsFolder can skip classic desktop
    // tiles (which already come from the Start Menu, with a real working shortcut and icon). The
    // strict validator still gates the explorer.exe fallback argument.
    private static readonly Regex PackagedSignature = new(
        @"_[a-z0-9]{13}!", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>True when the AUMID is a packaged (Store/UWP) app, not a classic desktop tile.</summary>
    public static bool IsPackagedAumid(string? aumid) =>
        !string.IsNullOrEmpty(aumid) && aumid.Length <= 130 && PackagedSignature.IsMatch(aumid);

    /// <summary>Identity key used for dedupe and the "already in this folder" check. Never the display
    /// name. AUMID (case-insensitive) when present; otherwise the full target path (case-insensitive)
    /// PLUS the trimmed argument string, so shortcuts sharing an exe but differing in arguments
    /// (browser profiles, control.exe, rundll32.exe, explorer.exe targets) stay distinct. Null when
    /// there is nothing to key on.</summary>
    public static string? IdentityKey(string? aumid, string? targetPath, string? arguments)
    {
        if (!string.IsNullOrEmpty(aumid))
            return "aumid:" + aumid.ToLowerInvariant();
        if (!string.IsNullOrEmpty(targetPath))
            return "path:" + targetPath.ToLowerInvariant() + "\u0001" + (arguments ?? "").Trim();
        return null;
    }

    public static string? IdentityKey(AppEntry e) => IdentityKey(e.Aumid, e.TargetPath, e.Arguments);

    /// <summary>Deduplicate by identity key (never by display name). A Start Menu entry wins over an
    /// AppsFolder entry with the same identity; within the same source the first seen wins (so the
    /// current-user Start Menu copy, enumerated first, beats the all-users one). Entries with no
    /// identity key are kept as-is.</summary>
    public static List<AppEntry> Merge(IEnumerable<AppEntry> entries)
    {
        var chosen = new Dictionary<string, AppEntry>(StringComparer.Ordinal);
        var result = new List<AppEntry>();
        foreach (var e in entries)
        {
            var key = IdentityKey(e);
            if (key is null) { result.Add(e); continue; }
            if (!chosen.TryGetValue(key, out var kept))
            {
                chosen[key] = e;
                result.Add(e);
            }
            else if (kept.Source == AppSource.AppsFolder && e.Source == AppSource.StartMenu)
            {
                // A real Start Menu shortcut supersedes the AppsFolder tile for the same app.
                int i = result.IndexOf(kept);
                if (i >= 0) result[i] = e;
                chosen[key] = e;
            }
            // else: same identity, keep the first (current-user) one.
        }
        return result;
    }

    /// <summary>Case-insensitive display-name substring filter. Empty/whitespace query returns all.</summary>
    public static List<AppEntry> Filter(IEnumerable<AppEntry> entries, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return entries.ToList();
        var q = query.Trim();
        return entries.Where(e => e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The Browse-for-a-file start folder: the current user's Start Menu Programs folder,
    /// else all-users Programs, else Start Menu — never empty when one exists, never System32 or the
    /// process directory. Pure: the caller supplies the folder paths and an existence check.</summary>
    public static string? BrowseInitialDirectory(string? programs, string? commonPrograms, string? startMenu,
        Func<string, bool> exists)
    {
        foreach (var d in new[] { programs, commonPrograms, startMenu })
            if (!string.IsNullOrEmpty(d) && exists(d)) return d;
        return null;
    }

    /// <summary>Absolute %WINDIR%\explorer.exe — the fixed target for a packaged-app fallback shortcut
    /// (never the bare name "explorer.exe").</summary>
    public static string FallbackTarget(string windowsDir) =>
        System.IO.Path.Combine(windowsDir, "explorer.exe");

    /// <summary>Exactly "shell:AppsFolder\&lt;AUMID&gt;" — the fallback shortcut's only argument. Call
    /// only with an AUMID that passed <see cref="IsValidPackagedAumid"/>.</summary>
    public static string FallbackArguments(string aumid) => "shell:AppsFolder\\" + aumid;
}
