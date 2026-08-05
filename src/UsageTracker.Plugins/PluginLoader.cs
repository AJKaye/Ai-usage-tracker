using System.Reflection;
using System.Runtime.Loader;
using UsageTracker.Contracts;

namespace UsageTracker.Plugins;

/// <summary>Outcome of attempting to load one plugin assembly.</summary>
public sealed record PluginLoadResult(
    string Path,
    bool Loaded,
    string? Name,
    int? DeclaredMajor,
    int? DeclaredMinor,
    string? RejectionReason,
    IUsageTrackerPlugin? Plugin);

/// <summary>
/// Loads adapter plugins from assemblies in an isolated <see cref="AssemblyLoadContext"/>
/// and enforces the contract-version gate (PROJECT_CONTEXT.md §5 rule 3). A plugin
/// is REFUSED unless it (a) declares <see cref="UsageTrackerPluginAttribute"/>,
/// (b) matches the host's contract MAJOR, and (c) its MINOR ≤ the host's. This is
/// the mechanism that keeps "add a surface = ship a plugin" safe across versions.
///
/// Isolation note: the plugin context shares the Contracts assembly with the host
/// (a shared contract type must be the SAME Type across the boundary), but loads
/// the plugin's own dependencies in isolation — the standard host/plugin pattern.
/// </summary>
public sealed class PluginLoader
{
    private readonly int _hostMajor;
    private readonly int _hostMinor;

    public PluginLoader(int hostMajor = ContractVersion.Major, int hostMinor = ContractVersion.Minor)
    {
        _hostMajor = hostMajor;
        _hostMinor = hostMinor;
    }

    public PluginLoadResult TryLoad(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
            return new(assemblyPath, false, null, null, null, "file not found", null);

        var alc = new PluginLoadContext(assemblyPath);
        Assembly asm;
        try
        {
            asm = alc.LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(assemblyPath)));
        }
        catch (Exception ex)
        {
            return new(assemblyPath, false, null, null, null, $"load failed: {ex.Message}", null);
        }

        var attr = asm.GetCustomAttribute<UsageTrackerPluginAttribute>();
        if (attr is null)
            return new(assemblyPath, false, null, null, null,
                "missing [UsageTrackerPlugin] — not a recognized plugin", null);

        // --- the gate ---
        if (attr.ContractMajor != _hostMajor)
            return new(assemblyPath, false, null, attr.ContractMajor, attr.ContractMinor,
                $"contract major mismatch: plugin {attr.ContractMajor}.x, host {_hostMajor}.x", null);

        if (attr.ContractMinor > _hostMinor)
            return new(assemblyPath, false, null, attr.ContractMajor, attr.ContractMinor,
                $"plugin needs contract minor {attr.ContractMinor}, host provides {_hostMinor}", null);

        var entry = asm.GetTypes()
            .FirstOrDefault(t => typeof(IUsageTrackerPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });
        if (entry is null)
            return new(assemblyPath, false, null, attr.ContractMajor, attr.ContractMinor,
                "no IUsageTrackerPlugin entry point found", null);

        if (Activator.CreateInstance(entry) is not IUsageTrackerPlugin plugin)
            return new(assemblyPath, false, null, attr.ContractMajor, attr.ContractMinor,
                "entry point could not be instantiated", null);

        return new(assemblyPath, true, plugin.Name, attr.ContractMajor, attr.ContractMinor, null, plugin);
    }

    /// <summary>Load every *.dll in a directory, returning a result per file.</summary>
    public IReadOnlyList<PluginLoadResult> LoadDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return Array.Empty<PluginLoadResult>();
        return Directory.GetFiles(dir, "*.dll").Select(TryLoad).ToList();
    }
}

/// <summary>
/// Per-plugin load context. Resolves the plugin's private dependencies from its
/// own folder, but lets shared contract types fall through to the default context
/// so a <see cref="IUsageAdapter"/> from a plugin IS the host's interface type.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
        => _resolver = new AssemblyDependencyResolver(pluginPath);

    protected override Assembly? Load(AssemblyName name)
    {
        // Shared contracts must unify with the host — let the default ALC own them.
        if (name.Name == "UsageTracker.Contracts") return null;
        var path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
