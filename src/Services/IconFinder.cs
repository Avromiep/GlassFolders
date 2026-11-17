using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GlassFolders.Services;

/// <summary>One icon the finder turned up: the decoded image bytes plus its pixel size and a short,
/// non-identifying source label for the preview.</summary>
public sealed record IconCandidate(byte[] Bytes, int Width, int Height, string SourceLabel);

/// <summary>
/// Finds an app's real icon on the web, on explicit user action only (the "Find app icon" command).
/// It prefers the site's own app-style icon (apple-touch-icon — the image a site uses when installed as
/// an app, which matches the desktop app icon), and falls back to a keyless favicon service for the
/// bare logo. Only a derived domain is ever sent; no account, identifier, user name, or list of installed
/// apps leaves the machine, and nothing fetched is executed — images are normalised through the custom
/// icon path. The domain-guessing and HTML parsing are pure and unit-tested; the network parts are
/// isolated here and fail quietly offline.
/// </summary>
public static class IconFinder
{
    public const int MinUsefulPx = 48;      // reject 16/32px favicons as "too small"
    private const int MaxDomains = 3;        // candidate domains to try
    private const int MaxCandidates = 6;     // icons to offer in the preview

    private const string AppIconLabel = "App icon";   // apple-touch-icon: matches the desktop app icon
    private const string LogoLabel = "Logo";          // favicon service: the bare brand mark

    // Apps whose brand domain isn't "<name>.com". Keeps results accurate without a web search.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "claude.ai",
        ["chatgpt"] = "chatgpt.com",
        ["openai"] = "openai.com",
        ["gpt"] = "chatgpt.com",
        ["gemini"] = "gemini.google.com",
        ["copilot"] = "copilot.microsoft.com",
        ["perplexity"] = "perplexity.ai",
        ["grok"] = "grok.com",
        ["notion"] = "notion.so",
        ["figma"] = "figma.com",
        ["spotify"] = "spotify.com",
        ["discord"] = "discord.com",
        ["slack"] = "slack.com",
        ["youtube"] = "youtube.com",
        ["x"] = "x.com",
        ["twitter"] = "x.com",
        ["whatsapp"] = "whatsapp.com",
    };

    /// <summary>Lower-cased, letters-and-digits-only slug of a display name. Pure. e.g. "Grok Bot"→"grokbot".</summary>
    public static string Slug(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>Ordered, distinct candidate domains for an app name (alias first, then sensible TLD
    /// guesses). Pure — testable without any network.</summary>
    public static List<string> CandidateDomains(string name)
    {
        var result = new List<string>();
        void Add(string? d) { if (!string.IsNullOrEmpty(d) && !result.Contains(d!)) result.Add(d!); }

        string slug = Slug(name);
        if (slug.Length == 0) return result;
        if (Aliases.TryGetValue(slug, out var alias)) Add(alias);
        Add(slug + ".com");
        Add(slug + ".ai");
        Add(slug + ".app");
        return result;
    }

    /// <summary>The keyless favicon-service URL for a domain (returns up to a ~180–256px logo).</summary>
    public static string FaviconServiceUrl(string domain) =>
        "https://www.google.com/s2/favicons?domain=" + Uri.EscapeDataString(domain) + "&sz=256";

    /// <summary>The conventional apple-touch-icon file paths, tried directly so a site whose page blocks
    /// scraping (bot protection) still yields its app icon. Pure.</summary>
    public static IEnumerable<string> WellKnownAppleTouchUrls(string domain) => new[]
    {
        "https://" + domain + "/apple-touch-icon.png",
        "https://" + domain + "/apple-touch-icon-precomposed.png",
    };

    /// <summary>Extracts apple-touch-icon URLs from a page's HTML, largest first, resolved to absolute
    /// URLs against <paramref name="baseUrl"/>. Pure — testable with synthetic HTML.</summary>
    public static List<string> ParseAppleTouchIcons(string html, string baseUrl)
    {
        var found = new List<(int size, string url)>();
        if (!string.IsNullOrEmpty(html))
        {
            foreach (Match link in Regex.Matches(html, "<link\\b[^>]*>", RegexOptions.IgnoreCase))
            {
                string tag = link.Value;
                var rel = Attr(tag, "rel");
                if (rel == null || rel.IndexOf("apple-touch-icon", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                var href = Attr(tag, "href");
                if (string.IsNullOrWhiteSpace(href)) continue;
                var abs = ResolveUrl(baseUrl, href!.Trim());
                if (abs == null) continue;
                int size = ParseSizes(Attr(tag, "sizes"));
                found.Add((size, abs));
            }
        }
        // Largest first; unknown sizes (0) sort last. De-dupe by URL, preserving order.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var f in found.OrderByDescending(f => f.size))
            if (seen.Add(f.url)) ordered.Add(f.url);
        return ordered;
    }

    /// <summary>Highest pixel count from a sizes attribute like "180x180 120x120"; 0 if absent.</summary>
    public static int ParseSizes(string? sizes)
    {
        if (string.IsNullOrEmpty(sizes)) return 0;
        int best = 0;
        foreach (Match m in Regex.Matches(sizes, "(\\d+)\\s*[xX]\\s*(\\d+)"))
            if (int.TryParse(m.Groups[1].Value, out int w)) best = Math.Max(best, w);
        return best;
    }

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, name + "\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(tag, name + "\\s*=\\s*'([^']*)'", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Resolves a possibly-relative href against a base URL; null if it can't be made absolute
    /// or isn't http(s). Pure.</summary>
    public static string? ResolveUrl(string baseUrl, string href)
    {
        try
        {
            if (href.StartsWith("//")) href = "https:" + href;
            Uri result = Uri.TryCreate(href, UriKind.Absolute, out var abs)
                ? abs
                : (Uri.TryCreate(new Uri(baseUrl, UriKind.Absolute), href, out var rel) ? rel : null!);
            if (result == null) return null;
            return (result.Scheme == Uri.UriSchemeHttp || result.Scheme == Uri.UriSchemeHttps)
                ? result.ToString() : null;
        }
        catch { return null; }
    }

    // ---- Network (user-initiated only) ----

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,                         // send no cookies / identity
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        // Generic UA only — never the user's name, email, or app list.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return c;
    }

    /// <summary>Finds candidate icons for an app name, best (largest, app-style) first. Runs only when the
    /// user invokes Find app icon. Never throws; returns an empty list on no network or no match.</summary>
    public static async Task<List<IconCandidate>> FindAsync(string appName, CancellationToken ct)
    {
        var results = new List<IconCandidate>();
        var seenHashes = new HashSet<string>();
        try
        {
            foreach (var domain in CandidateDomains(appName).Take(MaxDomains))
            {
                if (ct.IsCancellationRequested || results.Count >= MaxCandidates) break;

                // 1) Prefer the site's own app-style icon (apple-touch-icon). First the ones the page
                //    declares (largest), then the conventional file paths — the latter still work when
                //    the page itself blocks scraping (e.g. Cloudflare), which is how we get Claude's icon.
                foreach (var iconUrl in await AppleTouchIconUrls(domain, ct).ConfigureAwait(false))
                {
                    if (results.Count >= MaxCandidates) break;
                    await TryAdd(iconUrl, AppIconLabel, results, seenHashes, ct).ConfigureAwait(false);
                }
                foreach (var wk in WellKnownAppleTouchUrls(domain))
                {
                    if (results.Count >= MaxCandidates) break;
                    await TryAdd(wk, AppIconLabel, results, seenHashes, ct).ConfigureAwait(false);
                }
                // 2) Fall back to the keyless favicon service (bare logo).
                if (results.Count < MaxCandidates)
                    await TryAdd(FaviconServiceUrl(domain), LogoLabel, results, seenHashes, ct).ConfigureAwait(false);
            }
        }
        catch { /* offline / blocked -> whatever we have (possibly none) */ }

        // App-style icons first (what matches the desktop app icon), then by size. A bigger bare logo
        // never outranks the real app icon.
        return results
            .OrderBy(c => c.SourceLabel == AppIconLabel ? 0 : 1)
            .ThenByDescending(c => c.Width * c.Height)
            .Take(MaxCandidates)
            .ToList();
    }

    private static async Task<List<string>> AppleTouchIconUrls(string domain, CancellationToken ct)
    {
        try
        {
            string baseUrl = "https://" + domain + "/";
            using var resp = await Http.GetAsync(baseUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new();
            var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (ctype.IndexOf("html", StringComparison.OrdinalIgnoreCase) < 0) return new();
            string html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Only need the <head>; cap how much we scan.
            if (html.Length > 200_000) html = html.Substring(0, 200_000);
            return ParseAppleTouchIcons(html, resp.RequestMessage?.RequestUri?.ToString() ?? baseUrl);
        }
        catch { return new(); }
    }

    private static async Task TryAdd(string url, string label, List<IconCandidate> into,
        HashSet<string> seenHashes, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (ctype.StartsWith("text", StringComparison.OrdinalIgnoreCase)) return;  // not an image
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) return;

            int w, h;
            using (var ms = new MemoryStream(bytes, false))
            using (var img = Image.FromStream(ms))   // decode only; proves it's a real image
            { w = img.Width; h = img.Height; }
            if (w < MinUsefulPx || h < MinUsefulPx) return;

            string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes));
            if (!seenHashes.Add(hash)) return;       // identical image from another URL
            into.Add(new IconCandidate(bytes, w, h, label));
        }
        catch { /* skip this one */ }
    }
}
