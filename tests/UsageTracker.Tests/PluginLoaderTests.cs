using UsageTracker.Contracts;
using UsageTracker.Plugins;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Proves the plugin seam AND the contract-version gate (PROJECT_CONTEXT.md §5
/// rule 3). The reference Cursor plugin is built alongside the tests but loaded
/// dynamically by path — exactly how a third-party adapter would arrive.
/// </summary>
public class PluginLoaderTests
{
    // Walk up from the test bin dir to the repo root, then to the built plugin dll.
    // Config (Debug/Release) + TFM are derived from THIS assembly's own output path,
    // not hardcoded — CI builds Release, local builds Debug, and both must resolve.
    private static string ReferencePluginPath()
    {
        var dir = AppContext.BaseDirectory; // .../tests/UsageTracker.Tests/bin/<Config>/<tfm>/
        // The last two path segments are <Configuration>/<TargetFramework>.
        var tfm = new DirectoryInfo(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
        var config = new DirectoryInfo(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;

        var root = new DirectoryInfo(dir);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiUsageTracker.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "plugins", "UsageTracker.Adapters.ReferenceCursor",
            "bin", config, tfm, "UsageTracker.Adapters.ReferenceCursor.dll");
        Assert.True(File.Exists(path), $"reference plugin not built at {path}");
        return path;
    }

    [Fact]
    public void Loads_reference_plugin_and_exposes_its_adapter()
    {
        var loader = new PluginLoader(); // host = current contract version
        var result = loader.TryLoad(ReferencePluginPath());

        Assert.True(result.Loaded, result.RejectionReason);
        Assert.Equal("reference-cursor", result.Name);
        var adapters = result.Plugin!.CreateAdapters().ToList();
        var cursor = Assert.Single(adapters);
        // The adapter's type IS the host's contract interface (unified identity).
        Assert.IsAssignableFrom<IUsageAdapter>(cursor);
        Assert.Equal("cursor", cursor.SourceId);
    }

    [Fact]
    public async Task Loaded_adapter_actually_pulls_a_canonical_span()
    {
        var loader = new PluginLoader();
        var adapter = loader.TryLoad(ReferencePluginPath()).Plugin!.CreateAdapters().First();

        var spans = new List<Span>();
        await foreach (var s in adapter.PullAsync("tenant-x", DateTimeOffset.UnixEpoch))
            spans.Add(s);

        var span = Assert.Single(spans);
        Assert.Equal("tenant-x", span.TenantId);
        Assert.Equal("cursor", span.Metadata!["surface"]);
        Assert.Equal(1200, span.RawUsage!.InputTokens);
    }

    [Fact]
    public void Rejects_plugin_built_against_a_different_contract_MAJOR()
    {
        // Host claims to be contract 1.x; the reference plugin declares 0.1 → refused.
        var futureHost = new PluginLoader(hostMajor: 1, hostMinor: 0);
        var result = futureHost.TryLoad(ReferencePluginPath());

        Assert.False(result.Loaded);
        Assert.Null(result.Plugin);
        Assert.Contains("major mismatch", result.RejectionReason);
        Assert.Equal(0, result.DeclaredMajor);   // it still reports what the plugin declared
    }

    [Fact]
    public void Rejects_plugin_needing_a_newer_MINOR_than_host_provides()
    {
        // Host is 0.0; plugin needs 0.1 (additive features host lacks) → refused.
        var olderHost = new PluginLoader(hostMajor: 0, hostMinor: 0);
        var result = olderHost.TryLoad(ReferencePluginPath());

        Assert.False(result.Loaded);
        Assert.Contains("minor", result.RejectionReason);
    }

    [Fact]
    public void Rejects_a_non_plugin_assembly()
    {
        // The Contracts dll has no [UsageTrackerPlugin] attribute → not a plugin.
        var contractsDll = typeof(IUsageAdapter).Assembly.Location;
        var result = new PluginLoader().TryLoad(contractsDll);

        Assert.False(result.Loaded);
        Assert.Contains("not a recognized plugin", result.RejectionReason);
    }

    [Fact]
    public void Missing_file_is_reported_not_thrown()
    {
        // Cross-platform absent path (Windows @"C:\…" isn't "missing" on Linux — it's
        // a valid relative name — which made this pass locally but fail on CI).
        var missing = Path.Combine(AppContext.BaseDirectory, "does-not-exist-" + Guid.NewGuid().ToString("n") + ".dll");
        var result = new PluginLoader().TryLoad(missing);
        Assert.False(result.Loaded);
        Assert.Equal("file not found", result.RejectionReason);
    }
}
