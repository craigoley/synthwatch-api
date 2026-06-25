namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// Catalogue of monitored targets. Maps to the live <c>checks</c> table (21 columns).
/// The runner owns this schema; this API maps to it read-mostly and never migrates it.
/// </summary>
public class Check
{
    // bigint GENERATED ALWAYS AS IDENTITY — never set on insert.
    public long Id { get; set; }

    public string Name { get; set; } = null!;

    // CHECK: kind IN ('http','browser','ssl','dns','tcp','ping','multistep') — widened by the runner
    // across migrations; the authoritative allowlist is Infrastructure/CheckValidation.Kinds.
    public string Kind { get; set; } = null!;

    public string TargetUrl { get; set; } = null!;

    public string? FlowName { get; set; }

    public string Method { get; set; } = "GET";

    public int ExpectedStatus { get; set; } = 200;

    public string? BodyMustContain { get; set; }

    public int IntervalSeconds { get; set; } = 300;

    public DateTimeOffset? LastRunAt { get; set; }

    public int TimeoutMs { get; set; } = 30000;

    public int FailureThreshold { get; set; } = 3;

    // CHECK: severity IN ('critical','warning')
    public string Severity { get; set; } = "critical";

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public bool LighthouseEnabled { get; set; }

    public int? LighthouseIntervalSeconds { get; set; }

    public string LighthouseFormFactor { get; set; } = "desktop";

    public int? PerfBudgetLcpMs { get; set; }

    // bigint — maps natively to long, no string trap.
    public long? PerfBudgetTransferBytes { get; set; }

    // SSL: warn when the cert expires within this many days. int NOT NULL DEFAULT 30.
    public int CertExpiryWarnDays { get; set; } = 30;

    // No-code assertion model + request config (migration 0008; jsonb/text). assertions is
    // NOT NULL DEFAULT '[]'; the others are nullable. auth holds secret REFERENCES (*_env names),
    // never raw credentials.
    public List<Assertion> Assertions { get; set; } = new();
    public Dictionary<string, string>? RequestHeaders { get; set; }
    public string? RequestBody { get; set; }
    public Dictionary<string, string>? Auth { get; set; }

    // Network checks (dns/tcp/ping): per-kind config (migration 0011; jsonb). Null for other kinds.
    public NetConfig? NetConfig { get; set; }

    // Multistep API chains (kind='multistep'): ordered step list (migration 0013; jsonb). Null otherwise.
    public List<ChainStep>? Steps { get; set; }

    // SLO target as a fraction (migration 0016; real, nullable). Null = no SLO (opt-in). A meaningful
    // target is in (0,1); the API only computes the SLO when so — slo_status divides by (1 - target),
    // so target = 1.0 would div-by-zero (and 500 GetCheck) and target outside (0,1) is nonsensical.
    public float? SloTarget { get; set; }

    // Monitors-as-code (Phase 13 activation). source_key (migration 0030) = the synthwatch-monitors
    // manifest id this check was activated from; a partial unique index (checks_source_key_uniq) allows
    // at most one live check per manifest id. spec_path (0033) = the manifest's Playwright spec path
    // (e.g. monitors/wegmans/search-product.spec.ts); when set, the runner's executeBrowser FETCHES +
    // runs that spec from Git instead of a baked flow (Option C). Both nullable — legacy/hand-made checks
    // leave them null and run the baked-flow path unchanged. The DB enforces spec_path's shape
    // (checks_spec_path_shape) — the API mirrors it on create for a clean 400 instead of a constraint 500.
    public string? SourceKey { get; set; }
    public string? SpecPath { get; set; }

    // Navigation (read-mostly).
    public List<Run> Runs { get; set; } = new();
    public List<Incident> Incidents { get; set; } = new();
}
