namespace UsageTracker.Contracts;

/// <summary>
/// The contract-version an adapter/plugin was built against. The plugin harness
/// refuses to load a plugin whose <see cref="ContractMajor"/> differs from the
/// host's (PROJECT_CONTEXT.md §5 rule 3: "every plugin ships against a contract
/// version and is refused if incompatible"). Applied at assembly level.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class UsageTrackerPluginAttribute : Attribute
{
    public UsageTrackerPluginAttribute(int contractMajor, int contractMinor)
    {
        ContractMajor = contractMajor;
        ContractMinor = contractMinor;
    }

    /// <summary>Breaking-change axis. Must equal the host's major to load.</summary>
    public int ContractMajor { get; }

    /// <summary>Additive-change axis. Plugin minor ≤ host minor is compatible.</summary>
    public int ContractMinor { get; }
}

/// <summary>
/// The single source of truth for the current contract version. A breaking change
/// to any interface/DTO in this package bumps <see cref="Major"/>; an additive
/// change bumps <see cref="Minor"/>. The harness compares a plugin's declared
/// version against these.
/// </summary>
public static class ContractVersion
{
    public const int Major = 0;
    public const int Minor = 1;
    public static string Display => $"{Major}.{Minor}";
}

/// <summary>
/// Marks a plugin's entry-point type. The harness instantiates the first exported
/// type implementing this to obtain the adapters/tiers the plugin provides.
/// </summary>
public interface IUsageTrackerPlugin
{
    string Name { get; }
    /// <summary>Adapters this plugin contributes (may be empty).</summary>
    IEnumerable<IUsageAdapter> CreateAdapters();
}
