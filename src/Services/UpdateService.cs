using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace GlassFolders.Services;

public enum UpdateStatus { UpToDate, UpdateAvailable, NoReleases, Error }

public sealed record UpdateResult(UpdateStatus Status, string? LatestVersion, string? Url, string? Message);

/// <summary>
/// Checks the latest GitHub release for a newer version (WhisperText-style).
/// Works once releases are published; a private repo needs its releases to be reachable
/// (make them public, or add a token later).
/// </summary>
public static class UpdateService
{
    // Set these to your repo when you publish it.
    public const string RepoOwner = "Avromiep";
    public const string RepoName = "LiquidFolders";

    public static async Task<UpdateResult> CheckAsync(string currentVersion)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LiquidFolders-Updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            var resp = await http.GetAsync(url);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new(UpdateStatus.NoReleases, null, null,
                    "No published releases found yet (or the repo is private/unset).");
            if (!resp.IsSuccessStatusCode)
                return new(UpdateStatus.Error, null, null, $"GitHub returned {(int)resp.StatusCode}.");

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

            var latest = ParseVersion(tag);
            var current = ParseVersion(currentVersion);
            if (latest == null)
                return new(UpdateStatus.Error, tag, htmlUrl, "Couldn't parse the release version.");
            if (current != null && latest > current)
                return new(UpdateStatus.UpdateAvailable, tag, htmlUrl, null);
            return new(UpdateStatus.UpToDate, tag, htmlUrl, null);
        }
        catch (Exception ex)
        {
            return new(UpdateStatus.Error, null, null, ex.Message);
        }
    }

    private static Version? ParseVersion(string s)
    {
        s = s.Trim().TrimStart('v', 'V');
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        s = s[..i].Trim('.');
        return Version.TryParse(s, out var v) ? v : null;
    }
}
