using System.Text.RegularExpressions;

namespace UsageTracker.Security;

/// <summary>One control row from the governance register (backs the Regulatory Governance page).</summary>
public sealed record GovernanceControl
{
    public required string Id { get; init; }
    public required string Control { get; init; }
    public required string Mechanism { get; init; }
    public required string Status { get; init; }   // Designed | Scaffolded | Implemented | Verified | Certified
    public required string Evidence { get; init; }
}

/// <summary>The full control matrix served to the UI.</summary>
public sealed record GovernanceMatrix
{
    public required IReadOnlyList<GovernanceControl> Controls { get; init; }
    public required string LastUpdated { get; init; }
    public required IReadOnlyDictionary<string, int> StatusCounts { get; init; }
}

/// <summary>
/// Parses GOVERNANCE.md's control-register table into a structured
/// <see cref="GovernanceMatrix"/> so the in-product Regulatory Governance page (D6)
/// is sourced from the SAME file engineers maintain — it can never drift from
/// reality (the Phase-8 exit criterion: "no stale hand-maintained governance copy").
/// Reads the cross-framework foundational-controls table (rows shaped
/// <c>| ID | Control | Mechanism | Status | Evidence |</c>).
/// </summary>
public static partial class GovernanceParser
{
    [GeneratedRegex(@"^\*\*Last updated:\*\*\s*([0-9]{4}-[0-9]{2}-[0-9]{2})", RegexOptions.Multiline)]
    private static partial Regex LastUpdatedLine();

    private static readonly string[] KnownStatuses =
        { "Designed", "Scaffolded", "Implemented", "Verified", "Certified" };

    public static GovernanceMatrix Parse(string markdown)
    {
        string lastUpdated = LastUpdatedLine().Match(markdown) is { Success: true } m ? m.Groups[1].Value : "unknown";

        var controls = new List<GovernanceControl>();
        foreach (var line in markdown.Split('\n'))
        {
            var t = line.Trim();
            // A control row starts "| C-…". Skip the header/separator and non-control rows.
            if (!t.StartsWith("| C-", StringComparison.Ordinal)) continue;

            // Split on unescaped pipes; drop the leading/trailing empty cells.
            var cells = t.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 5) continue;

            controls.Add(new GovernanceControl
            {
                Id = cells[0],
                Control = cells[1],
                Mechanism = StripMarkdown(cells[2]),
                Status = NormalizeStatus(cells[3]),
                Evidence = StripMarkdown(cells[4]),
            });
        }

        var counts = KnownStatuses.ToDictionary(
            s => s,
            s => controls.Count(c => c.Status.StartsWith(s, StringComparison.OrdinalIgnoreCase)));

        return new GovernanceMatrix
        {
            Controls = controls,
            LastUpdated = lastUpdated,
            StatusCounts = counts,
        };
    }

    // A status cell may be "**Verified (app-layer)**" — keep the qualifier but strip bold.
    private static string NormalizeStatus(string raw) => StripMarkdown(raw);

    private static string StripMarkdown(string s) =>
        s.Replace("**", "").Replace("`", "").Trim();
}
