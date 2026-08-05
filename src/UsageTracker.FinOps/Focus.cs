using UsageTracker.Contracts;

namespace UsageTracker.FinOps;

/// <summary>
/// A FOCUS (FinOps Open Cost &amp; Usage Specification) cost row projected from a
/// canonical <see cref="Span"/> + its <see cref="CostBreakdown"/> (ARCHITECTURE.md
/// §6.1). FOCUS v1.2+ tracks virtual currencies (tokens/credits) as the entry point
/// for AI billing, so a token span and a coarse "AI unit" span both map to the same
/// column set. Column names match the FOCUS spec so exports join cleanly with cloud
/// FOCUS feeds.
///
/// Estimated-vs-reconciled (ARCHITECTURE.md §4.1) maps to FOCUS as:
/// <c>ListCost</c> = list-price estimate, <c>BilledCost</c>/<c>EffectiveCost</c> =
/// what we currently know (estimate until reconciliation overwrites it).
/// </summary>
public sealed record FocusRow
{
    // --- charge identity / period ---
    public required string BillingAccountId { get; init; }     // tenant
    public required DateTimeOffset ChargePeriodStart { get; init; }
    public DateTimeOffset? ChargePeriodEnd { get; init; }

    // --- provider / resource ---
    public string? ProviderName { get; init; }
    public string? ResourceId { get; init; }                   // span id
    public string? ServiceName { get; init; }                  // model
    public required string ChargeCategory { get; init; }       // "Usage"

    // --- consumption (virtual currency: tokens / units) ---
    public required decimal ConsumedQuantity { get; init; }
    public required string ConsumedUnit { get; init; }         // "tokens" | "ai_unit" | "request" | "seat"
    public required decimal PricingQuantity { get; init; }
    public required string PricingUnit { get; init; }

    // --- cost measures ---
    public required decimal ListCost { get; init; }            // list-price estimate
    public required decimal BilledCost { get; init; }          // what we bill now (estimate until reconciled)
    public required decimal EffectiveCost { get; init; }       // amortized/effective (== billed pre-reconciliation)
    public required string BillingCurrency { get; init; }
}

public static class FocusProjection
{
    /// <summary>Project one canonical span into a FOCUS cost row.</summary>
    public static FocusRow Project(Span span)
    {
        var cost = span.EstimatedCost;
        decimal amount = cost?.TotalCost ?? 0m;
        string currency = cost?.Currency ?? "USD";

        // Consumption: tokens for the token path, units for coarse surfaces.
        (decimal qty, string unit) = span.Granularity == Granularity.Token
            ? (span.Usage?.TotalTokens ?? 0, "tokens")
            : (span.UnitsConsumed ?? 0, span.UnitType ?? span.Granularity.ToString().ToLowerInvariant());

        return new FocusRow
        {
            BillingAccountId = span.TenantId,
            ChargePeriodStart = span.StartTime,
            ChargePeriodEnd = span.EndTime,
            ProviderName = span.Provider,
            ResourceId = span.SpanId,
            ServiceName = span.ResponseModel ?? span.RequestModel,
            ChargeCategory = "Usage",
            ConsumedQuantity = qty,
            ConsumedUnit = unit,
            PricingQuantity = qty,
            PricingUnit = unit,
            ListCost = amount,
            BilledCost = amount,
            EffectiveCost = amount,
            BillingCurrency = currency,
        };
    }

    public static IReadOnlyList<FocusRow> Project(IReadOnlyList<Span> spans) =>
        spans.Select(Project).ToList();
}
