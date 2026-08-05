using System.Runtime.InteropServices;
using UsageTracker.Ingestion.Api;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// The desktop app-mode window: the .exe opens a chromeless browser window in the
/// solo profile so it feels native, while staying a server underneath. The launch
/// decision + candidate list are pure and unit-tested here (spawning a real window
/// can't be asserted in CI); the spawn itself is best-effort and never fatal.
/// </summary>
public class DesktopWindowTests
{
    [Fact]
    public void Opens_only_for_solo_with_no_pinned_url_and_not_disabled()
    {
        Assert.True(DesktopWindow.ShouldOpen("solo", pinnedUrls: null, noWindowFlag: null));
        Assert.True(DesktopWindow.ShouldOpen("SOLO", null, null));   // case-insensitive
    }

    [Theory]
    [InlineData("ephemeral")]   // test host
    [InlineData("standard")]    // server tier
    [InlineData("distributed")]
    public void Does_not_open_for_non_solo_profiles(string profile)
    {
        Assert.False(DesktopWindow.ShouldOpen(profile, null, null));
    }

    [Fact]
    public void Does_not_open_when_an_operator_pinned_a_url()
    {
        // A server/SaaS deploy sets ASPNETCORE_URLS — the window must stay closed.
        Assert.False(DesktopWindow.ShouldOpen("solo", pinnedUrls: "http://0.0.0.0:8080", noWindowFlag: null));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("YES")]
    public void Honors_the_no_window_opt_out(string flag)
    {
        Assert.False(DesktopWindow.ShouldOpen("solo", null, flag));
    }

    [Fact]
    public void Windows_candidates_lead_with_app_mode_then_fall_back_to_default()
    {
        var url = "http://127.0.0.1:5000";
        var c = DesktopWindow.Candidates(url, OSPlatform.Windows);

        // App-mode (chromeless) launchers first…
        Assert.Equal("msedge", c[0].File);
        Assert.Contains($"--app={url}", c[0].Args);
        Assert.Contains(c, x => x.File == "chrome" && x.Args.Contains($"--app={url}"));
        // …then a plain default-browser fallback (the URL itself, ShellExecute).
        Assert.Equal(url, c[^1].File);
        Assert.Equal("", c[^1].Args);
    }

    [Fact]
    public void Linux_and_mac_have_app_mode_and_a_generic_fallback()
    {
        var url = "http://127.0.0.1:5000";

        var linux = DesktopWindow.Candidates(url, OSPlatform.Linux);
        Assert.Contains(linux, x => x.Args.Contains($"--app={url}"));
        Assert.Contains(linux, x => x.File == "xdg-open");   // generic fallback

        var mac = DesktopWindow.Candidates(url, OSPlatform.OSX);
        Assert.Contains(mac, x => x.File == "open" && x.Args.Contains($"--app={url}"));
        Assert.Contains(mac, x => x.File == "open" && x.Args == url);
    }
}
