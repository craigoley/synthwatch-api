namespace SynthWatch.Api.Data.Entities;

/// <summary>Keyless projection of the runner-owned <c>azure_cost</c> singleton (runner migration 0090) — the
/// cache of what the runner PULLS from Azure Cost Management (azureCost.ts): MTD actual + Azure's own forecast
/// for the RG scope, plus a portal deep link and the fetch timestamp. GET /reports/cost serves it VERBATIM
/// (Azure's numbers, not modeled). ★ Read-only for the API — the runner writes it, so no grant beyond the
/// ops-level SELECT (this table carries no `writes` entry in required-grants.json by design). Queried as a
/// single row (WHERE id = 1); a missing table/row is caught and served as absent, never a fabricated 0.</summary>
public class AzureCostRow
{
    public string Scope { get; set; } = "";
    public string Currency { get; set; } = "";
    public DateOnly BillingMonth { get; set; }      // first-of-month (UTC) the figures cover
    public decimal MtdActual { get; set; }          // month-to-date actual cost, all meters in scope
    public int MtdDays { get; set; }                // days elapsed in the billing month at fetch
    public decimal? ForecastMonth { get; set; }     // Azure's own end-of-month forecast; null when none returned
    public string PortalUrl { get; set; } = "";     // deep link to Cost Management for the scope
    public DateTimeOffset FetchedAt { get; set; }   // when the runner pulled it (staleness / "as of" label)
}
