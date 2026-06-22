using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SynthWatch.Api.Data.Entities;

namespace SynthWatch.Api.Data;

/// <summary>
/// Read-mostly DbContext mapped to the runner-owned Postgres schema.
/// This API does NOT own migrations and never mutates the schema — entities map to
/// existing tables/columns only. The only writes are to <c>checks</c> (CRUD + pause).
/// </summary>
public class SynthWatchDbContext : DbContext
{
    public SynthWatchDbContext(DbContextOptions<SynthWatchDbContext> options) : base(options)
    {
    }

    public DbSet<Check> Checks => Set<Check>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<RunStep> RunSteps => Set<RunStep>();
    public DbSet<RunMetric> RunMetrics => Set<RunMetric>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<SlaAvailabilityRow> SlaAvailability => Set<SlaAvailabilityRow>();
    public DbSet<CheckMetricsRow> CheckMetrics => Set<CheckMetricsRow>();
    public DbSet<FlowManifest> FlowManifests => Set<FlowManifest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Check>(e =>
        {
            e.ToTable("checks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.TargetUrl).HasColumnName("target_url");
            e.Property(x => x.FlowName).HasColumnName("flow_name");
            e.Property(x => x.Method).HasColumnName("method");
            e.Property(x => x.ExpectedStatus).HasColumnName("expected_status");
            e.Property(x => x.BodyMustContain).HasColumnName("body_must_contain");
            e.Property(x => x.IntervalSeconds).HasColumnName("interval_seconds");
            e.Property(x => x.LastRunAt).HasColumnName("last_run_at");
            e.Property(x => x.TimeoutMs).HasColumnName("timeout_ms");
            e.Property(x => x.FailureThreshold).HasColumnName("failure_threshold");
            e.Property(x => x.Severity).HasColumnName("severity");
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            e.Property(x => x.LighthouseEnabled).HasColumnName("lighthouse_enabled");
            e.Property(x => x.LighthouseIntervalSeconds).HasColumnName("lighthouse_interval_seconds");
            e.Property(x => x.LighthouseFormFactor).HasColumnName("lighthouse_form_factor");
            e.Property(x => x.PerfBudgetLcpMs).HasColumnName("perf_budget_lcp_ms");
            e.Property(x => x.PerfBudgetTransferBytes).HasColumnName("perf_budget_transfer_bytes");
            e.Property(x => x.CertExpiryWarnDays).HasColumnName("cert_expiry_warn_days");

            // No-code assertion model + request config (jsonb / text). Typed CLR models are
            // (de)serialized to jsonb via System.Text.Json value converters (camelCase keys,
            // matching the runner's contract). request_body is plain text.
            var (asConv, asCmp) = JsonbColumn<List<Assertion>>();
            e.Property(x => x.Assertions).HasColumnName("assertions").HasColumnType("jsonb")
                .HasConversion(asConv, asCmp);
            var (hdrConv, hdrCmp) = JsonbColumn<Dictionary<string, string>?>();
            e.Property(x => x.RequestHeaders).HasColumnName("request_headers").HasColumnType("jsonb")
                .HasConversion(hdrConv, hdrCmp);
            e.Property(x => x.RequestBody).HasColumnName("request_body");
            var (authConv, authCmp) = JsonbColumn<Dictionary<string, string>?>();
            e.Property(x => x.Auth).HasColumnName("auth").HasColumnType("jsonb")
                .HasConversion(authConv, authCmp);
            var (netConv, netCmp) = JsonbColumn<NetConfig?>();
            e.Property(x => x.NetConfig).HasColumnName("net_config").HasColumnType("jsonb")
                .HasConversion(netConv, netCmp);
        });

        modelBuilder.Entity<Run>(e =>
        {
            e.ToTable("runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.StartedAt).HasColumnName("started_at").ValueGeneratedOnAdd();
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");
            e.Property(x => x.HttpStatus).HasColumnName("http_status");
            e.Property(x => x.ErrorMessage).HasColumnName("error_message");
            e.Property(x => x.FailedStep).HasColumnName("failed_step");
            e.Property(x => x.ScreenshotUrl).HasColumnName("screenshot_url");
            e.Property(x => x.CertDaysRemaining).HasColumnName("cert_days_remaining");
            e.HasOne(x => x.Check).WithMany(c => c.Runs).HasForeignKey(x => x.CheckId);
            e.HasIndex(x => new { x.CheckId, x.StartedAt });
        });

        modelBuilder.Entity<RunStep>(e =>
        {
            e.ToTable("run_steps");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.StepIndex).HasColumnName("step_index");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");
            e.Property(x => x.ErrorMessage).HasColumnName("error_message");
            e.Property(x => x.StartedAt).HasColumnName("started_at").ValueGeneratedOnAdd();
            e.HasOne(x => x.Run).WithMany(r => r.Steps).HasForeignKey(x => x.RunId);
        });

        modelBuilder.Entity<RunMetric>(e =>
        {
            e.ToTable("run_metrics");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.TtfbMs).HasColumnName("ttfb_ms");
            e.Property(x => x.DomContentLoadedMs).HasColumnName("dom_content_loaded_ms");
            e.Property(x => x.LoadEventMs).HasColumnName("load_event_ms");
            e.Property(x => x.FcpMs).HasColumnName("fcp_ms");
            e.Property(x => x.LcpMs).HasColumnName("lcp_ms");
            e.Property(x => x.TransferBytes).HasColumnName("transfer_bytes");
            e.Property(x => x.ResourceCount).HasColumnName("resource_count");
            e.Property(x => x.DomNodeCount).HasColumnName("dom_node_count");
            e.Property(x => x.JsHeapBytes).HasColumnName("js_heap_bytes");
            e.Property(x => x.CpuTimeMs).HasColumnName("cpu_time_ms");
            e.Property(x => x.LayoutCount).HasColumnName("layout_count");
            e.Property(x => x.RecalcStyleCount).HasColumnName("recalc_style_count");
            e.Property(x => x.Cls).HasColumnName("cls");
            e.Property(x => x.InpMs).HasColumnName("inp_ms");
            e.Property(x => x.CapturedAt).HasColumnName("captured_at").ValueGeneratedOnAdd();
            e.HasIndex(x => x.RunId).IsUnique();
            e.HasOne(x => x.Run).WithOne(r => r.Metrics).HasForeignKey<RunMetric>(x => x.RunId);
        });

        modelBuilder.Entity<Incident>(e =>
        {
            e.ToTable("incidents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Severity).HasColumnName("severity");
            e.Property(x => x.OpenedAt).HasColumnName("opened_at").ValueGeneratedOnAdd();
            e.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
            e.Property(x => x.OpenedRunId).HasColumnName("opened_run_id");
            e.Property(x => x.ResolvedRunId).HasColumnName("resolved_run_id");
            e.Property(x => x.ConsecutiveFailures).HasColumnName("consecutive_failures");
            e.Property(x => x.Summary).HasColumnName("summary");
            e.HasOne(x => x.Check).WithMany(c => c.Incidents).HasForeignKey(x => x.CheckId);
        });

        // Keyless: read from the SLA function/views via raw SQL only.
        modelBuilder.Entity<SlaAvailabilityRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.CheckName).HasColumnName("check_name");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.WindowFrom).HasColumnName("window_from");
            e.Property(x => x.WindowTo).HasColumnName("window_to");
            e.Property(x => x.CompletedRuns).HasColumnName("completed_runs");
            e.Property(x => x.UpRuns).HasColumnName("up_runs");
            e.Property(x => x.DownRuns).HasColumnName("down_runs");
            e.Property(x => x.AvailabilityPct).HasColumnName("availability_pct");
        });

        modelBuilder.Entity<FlowManifest>(e =>
        {
            e.ToTable("flow_manifest");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.EntryUrlHint).HasColumnName("entry_url_hint");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
        });

        // Keyless: read per-check parity metrics via the ported lateral-join raw SQL only.
        modelBuilder.Entity<CheckMetricsRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.P50Ms).HasColumnName("p50_ms");
            e.Property(x => x.P95Ms).HasColumnName("p95_ms");
            e.Property(x => x.Runs24h).HasColumnName("runs_24h");
            e.Property(x => x.OpenIncidentCount).HasColumnName("open_incident_count");
            e.Property(x => x.MaxOpenSeverity).HasColumnName("max_open_severity");
            e.Property(x => x.Spark).HasColumnName("spark");
        });
    }

    // camelCase keys, omit nulls — matches the runner's JSONB shape (e.g. assertion elements
    // {source, comparison, ...}, auth {type, token_env, ...}). Dictionary keys are preserved verbatim.
    private static readonly JsonSerializerOptions JsonbOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Builds an EF value converter + comparer that (de)serializes a CLR model to a jsonb string.
    /// EF stores SQL NULL for a null property (the converter is only invoked for non-null values).
    /// The comparer compares by serialized JSON so change tracking works for reassigned values.
    /// </summary>
    private static (ValueConverter<T, string>, ValueComparer<T>) JsonbColumn<T>()
    {
        var converter = new ValueConverter<T, string>(
            v => JsonSerializer.Serialize(v, JsonbOptions),
            v => JsonSerializer.Deserialize<T>(v, JsonbOptions)!);
        var comparer = new ValueComparer<T>(
            (a, b) => JsonSerializer.Serialize(a, JsonbOptions) == JsonSerializer.Serialize(b, JsonbOptions),
            v => v == null ? 0 : JsonSerializer.Serialize(v, JsonbOptions).GetHashCode(StringComparison.Ordinal),
            v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonbOptions), JsonbOptions)!);
        return (converter, comparer);
    }
}
