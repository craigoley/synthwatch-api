using Microsoft.EntityFrameworkCore;
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
    }
}
