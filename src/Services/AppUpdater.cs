using System.IO;
using System.Net.Http;

namespace GlassFolders.Services;

/// <summary>
/// In-app update install (WhisperText-style): download the installer with progress, then run it
/// silently. The installer closes the running app, upgrades in place, and relaunches the new
/// version — so the whole flow stays in-app with no browser hand-off. Downloading via HttpClient
/// (rather than the browser) also avoids the mark-of-the-web SmartScreen prompt.
/// </summary>
public static class AppUpdater
{
    /// <summary>Downloads the setup to a temp file, reporting 0..100 percent.</summary>
    public static async Task<string> DownloadAsync(string setupUrl, IProgress<int> progress, CancellationToken ct)
    {
        var dest = Path.Combine(Path.GetTempPath(), "GlassFolders-Setup.exe");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GlassFolders-Updater");

        using var resp = await http.GetAsync(setupUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(dest))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                readTotal += n;
                if (total > 0) progress.Report((int)(readTotal * 100 / total));
            }
        }

        if (new FileInfo(dest).Length < 1_000_000) // sanity: a real setup is tens of MB
            throw new IOException("The downloaded installer looks incomplete.");
        return dest;
    }

    /// <summary>
    /// Launches the downloaded installer silently. It closes this app, upgrades in place, and
    /// relaunches. Call Application.Shutdown right after so the files aren't locked.
    /// </summary>
    public static void RunInstaller(string setupPath)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setupPath,
            "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
        { UseShellExecute = true });
    }
}
