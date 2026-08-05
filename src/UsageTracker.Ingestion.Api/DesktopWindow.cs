using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UsageTracker.Ingestion.Api;

/// <summary>
/// Opens the SPA in a chromeless "app-mode" browser window so the downloadable
/// <c>.exe</c> feels like a native desktop app — while staying a server underneath
/// (D4/D3: same binary runs headless on a server or as a windowed app on a laptop).
///
/// It only triggers for the <c>solo</c> desktop profile with NO operator-pinned URL,
/// so servers/SaaS and the test host (which run other profiles / pin a URL) are
/// untouched. Spawning the browser is best-effort and never throws — a failed window
/// must not take down the server; the URL is always logged as a fallback.
/// </summary>
public static class DesktopWindow
{
    /// <summary>
    /// Whether to open a desktop window: only in the <c>solo</c> profile, when no URL
    /// was pinned by an operator (ASPNETCORE_URLS / --urls), and not explicitly
    /// disabled (USAGETRACKER__NO_WINDOW). Pure — the decision is unit-tested.
    /// </summary>
    public static bool ShouldOpen(string profile, string? pinnedUrls, string? noWindowFlag) =>
        string.Equals(profile, "solo", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(pinnedUrls)
        && !IsTruthy(noWindowFlag);

    private static bool IsTruthy(string? v) =>
        v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Ordered browser-launch candidates for the given OS. App-mode launchers
    /// (Edge/Chrome/Chromium <c>--app=</c>) first — a chromeless window with no tabs
    /// or address bar — then a plain default-browser fallback (opens a normal tab).
    /// Pure so it's unit-testable without spawning anything.
    /// </summary>
    public static IReadOnlyList<(string File, string Args)> Candidates(string url, OSPlatform os)
    {
        if (os == OSPlatform.Windows)
            return new[]
            {
                ("msedge", $"--app={url}"),   // Edge ships on Windows 10/11
                ("chrome", $"--app={url}"),
                (url, ""),                    // default browser via ShellExecute (normal window)
            };
        if (os == OSPlatform.OSX)
            return new[]
            {
                ("open", $"-a \"Google Chrome\" --args --app={url}"),
                ("open", $"-a \"Microsoft Edge\" --args --app={url}"),
                ("open", url),
            };
        // Linux / other
        return new[]
        {
            ("google-chrome", $"--app={url}"),
            ("chromium", $"--app={url}"),
            ("chromium-browser", $"--app={url}"),
            ("xdg-open", url),
        };
    }

    /// <summary>
    /// Best-effort: try each candidate until one launches; log the URL regardless so
    /// the user can always open it manually. Never throws.
    /// </summary>
    public static void Open(string url, ILogger log)
    {
        log.LogInformation("AI Usage Tracker is running at {Url} — opening a window (set USAGETRACKER__NO_WINDOW=1 to disable).", url);
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
               : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX
               : OSPlatform.Linux;

        foreach (var (file, argsText) in Candidates(url, os))
        {
            try
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = true };
                if (!string.IsNullOrEmpty(argsText)) psi.Arguments = argsText;
                var p = Process.Start(psi);
                if (p is not null || file == url)   // shell-exec of a URL returns null but still opens
                {
                    log.LogDebug("opened window via '{File}'.", file);
                    return;
                }
            }
            catch (Exception ex)
            {
                log.LogDebug("window launcher '{File}' unavailable: {Msg}", file, ex.Message);
            }
        }
        log.LogWarning("could not auto-open a window — browse to {Url} manually.", url);
    }
}
