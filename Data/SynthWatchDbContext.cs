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
    public DbSet<SloStatusRow> SloStatus => Set<SloStatusRow>();
    public DbSet<SloReportRow> SloReport => Set<SloReportRow>();
    public DbSet<CostReportRow> CostReport => Set<CostReportRow>();
    public DbSet<MttrIncidentRow> MttrIncidents => Set<MttrIncidentRow>();
    public DbSet<AvailabilitySeriesPointRow> AvailabilitySeries => Set<AvailabilitySeriesPointRow>();
    public DbSet<CheckMetricsRow> CheckMetrics => Set<CheckMetricsRow>();
    public DbSet<FlowManifest> FlowManifests => Set<FlowManifest>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<CheckLocation> CheckLocations => Set<CheckLocation>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<AlertRoute> AlertRoutes => Set<AlertRoute>();
    public DbSet<CheckTag> CheckTags => Set<CheckTag>();
    public DbSet<TagRoute> TagRoutes => Set<TagRoute>();
    public DbSet<TestSendRequest> TestSendRequests => Set<TestSendRequest>();
    public DbSet<RunRequest> RunRequests => Set<RunRequest>();
    public DbSet<AvailabilityReportRow> AvailabilityReport => Set<AvailabilityReportRow>();
    public DbSet<AvailabilitySeriesRow> AvailabilityReportSeries => Set<AvailabilitySeriesRow>();
    public DbSet<LatencyReportRow> LatencyReport => Set<LatencyReportRow>();
    public DbSet<VitalsReportRow> VitalsReport => Set<VitalsReportRow>();
    public DbSet<LatencySeriesRow> LatencyReportSeries => Set<LatencySeriesRow>();
    public DbSet<ReportNarrativeRow> ReportNarratives => Set<ReportNarrativeRow>();
    public DbSet<IncidentBreakdownRow> IncidentBreakdown => Set<IncidentBreakdownRow>();
    public DbSet<TrustMonitorRow> TrustMonitors => Set<TrustMonitorRow>();
    public DbSet<TrustRetryDayRow> TrustRetryDays => Set<TrustRetryDayRow>();
    public DbSet<StatusCheckRow> StatusChecks => Set<StatusCheckRow>();
    public DbSet<EgressRunRow> EgressRuns => Set<EgressRunRow>();
    public DbSet<RegionHealthRow> RegionHealth => Set<RegionHealthRow>();
    public DbSet<CheckLocationStatusRow> CheckLocationStatuses => Set<CheckLocationStatusRow>();
    public DbSet<StatusSlaRow> StatusSla => Set<StatusSlaRow>();
    public DbSet<StatusIncidentRow> StatusIncidents => Set<StatusIncidentRow>();
    public DbSet<DeployRow> Deploys => Set<DeployRow>();
    public DbSet<NearbyDeployRow> NearbyDeploys => Set<NearbyDeployRow>();
    public DbSet<ReconcileDriftRow> ReconcileDrift => Set<ReconcileDriftRow>();
    public DbSet<ReconcileApplyPlanRow> ReconcileApplyPlan => Set<ReconcileApplyPlanRow>();
    public DbSet<SpecCatalogRow> SpecCatalog => Set<SpecCatalogRow>();

    // Auth identity (Phase 12 slice 1, migration 0037). API-owned: the only writes the API makes
    // outside `checks` live here (mint/verify sessions, OTP codes, access requests, editor allowlist).
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Editor> Editors => Set<Editor>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Location>(e =>
        {
            e.ToTable("locations");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Enabled).HasColumnName("enabled");
        });

        modelBuilder.Entity<CheckLocation>(e =>
        {
            e.ToTable("check_locations");
            e.HasKey(x => new { x.CheckId, x.Location });
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.Location).HasColumnName("location");
            e.Property(x => x.LastRunAt).HasColumnName("last_run_at");
        });

        modelBuilder.Entity<Channel>(e =>
        {
            e.ToTable("channels");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Type).HasColumnName("type");
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            var (cfgConv, cfgCmp) = JsonbColumn<ChannelConfig>();
            e.Property(x => x.Config).HasColumnName("config").HasColumnType("jsonb")
                .HasConversion(cfgConv, cfgCmp);
        });

        modelBuilder.Entity<CheckTag>(e =>
        {
            e.ToTable("check_tags");
            e.HasKey(x => new { x.CheckId, x.Key });
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.Value).HasColumnName("value");
        });

        modelBuilder.Entity<TagRoute>(e =>
        {
            e.ToTable("tag_routes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TagKey).HasColumnName("tag_key");
            e.Property(x => x.TagValue).HasColumnName("tag_value");
            e.Property(x => x.ChannelId).HasColumnName("channel_id");
        });

        modelBuilder.Entity<TestSendRequest>(e =>
        {
            // Runner-owned (migration 0026): the API INSERTs 'pending' rows + READs status only.
            // id/requested_at are DB-generated; the runner mutates status/detail/completed_at.
            e.ToTable("test_send_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.ChannelId).HasColumnName("channel_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Detail).HasColumnName("detail");
            e.Property(x => x.RequestedAt).HasColumnName("requested_at").ValueGeneratedOnAdd();
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
        });

        modelBuilder.Entity<RunRequest>(e =>
        {
            // Runner-owned (migration 0042): the API INSERTs 'pending' rows + READs to coalesce only.
            // id/requested_at are DB-generated; the runner mutates status/completed_at.
            e.ToTable("run_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.RequestedAt).HasColumnName("requested_at").ValueGeneratedOnAdd();
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.Sandbox).HasColumnName("sandbox"); // runner migration 0064 (DEFAULT false)
        });

        modelBuilder.Entity<AlertRoute>(e =>
        {
            e.ToTable("alert_routes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Severity).HasColumnName("severity");
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.ChannelId).HasColumnName("channel_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
        });

        // Auth identity tables (migration 0037). id/created_at/requested_at/added_at are DB-generated.
        modelBuilder.Entity<OtpCode>(e =>
        {
            e.ToTable("otp_codes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.CodeHash).HasColumnName("code_hash");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
            e.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            e.Property(x => x.RequestIp).HasColumnName("request_ip");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
        });

        modelBuilder.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.TokenHash).HasColumnName("token_hash");
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            e.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.IssuedIp).HasColumnName("issued_ip");
        });

        modelBuilder.Entity<Editor>(e =>
        {
            e.ToTable("editors");
            e.HasKey(x => x.Email);
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.AddedBy).HasColumnName("added_by");
            e.Property(x => x.AddedAt).HasColumnName("added_at").ValueGeneratedOnAdd();
        });

        modelBuilder.Entity<AccessRequest>(e =>
        {
            e.ToTable("access_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.RequestedAt).HasColumnName("requested_at").ValueGeneratedOnAdd();
            e.Property(x => x.RequestIp).HasColumnName("request_ip");
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            // Append-only (migration 0038): the API INSERTs + SELECTs; UPDATE/DELETE are revoked at the DB.
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.Ts).HasColumnName("ts").ValueGeneratedOnAdd();
            e.Property(x => x.ActorEmail).HasColumnName("actor_email");
            e.Property(x => x.ActorIp).HasColumnName("actor_ip");
            e.Property(x => x.Action).HasColumnName("action");
            e.Property(x => x.TargetType).HasColumnName("target_type");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.HttpMethod).HasColumnName("http_method");
            e.Property(x => x.HttpPath).HasColumnName("http_path");
            e.Property(x => x.StatusCode).HasColumnName("status_code");
            e.Property(x => x.Success).HasColumnName("success");
            e.Property(x => x.BeforeJson).HasColumnName("before_json").HasColumnType("jsonb");
            e.Property(x => x.AfterJson).HasColumnName("after_json").HasColumnType("jsonb");
            e.Property(x => x.Note).HasColumnName("note");
        });

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
            e.Property(x => x.SloTarget).HasColumnName("slo_target");
            // Monitors-as-code activation (Phase 13): the manifest id + spec path this check runs.
            e.Property(x => x.SourceKey).HasColumnName("source_key");
            e.Property(x => x.SpecPath).HasColumnName("spec_path");
            e.Property(x => x.SuccessTraceUrl).HasColumnName("success_trace_url");
            e.Property(x => x.SuccessTraceAt).HasColumnName("success_trace_at");

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
            var (shConv, shCmp) = JsonbColumn<Dictionary<string, string>?>();
            e.Property(x => x.SecretHeaders).HasColumnName("secret_headers").HasColumnType("jsonb") // runner 0061; references-only
                .HasConversion(shConv, shCmp);
            var (netConv, netCmp) = JsonbColumn<NetConfig?>();
            e.Property(x => x.NetConfig).HasColumnName("net_config").HasColumnType("jsonb")
                .HasConversion(netConv, netCmp);
            var (stepsConv, stepsCmp) = JsonbColumn<List<ChainStep>?>();
            e.Property(x => x.Steps).HasColumnName("steps").HasColumnType("jsonb")
                .HasConversion(stepsConv, stepsCmp);
            // B10 (migration 0046): sensitive flag + declared redaction patterns (jsonb array of regex).
            e.Property(x => x.Sensitive).HasColumnName("sensitive");
            var (redactConv, redactCmp) = JsonbColumn<List<string>?>();
            e.Property(x => x.RedactPatterns).HasColumnName("redact_patterns").HasColumnType("jsonb")
                .HasConversion(redactConv, redactCmp);
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
            e.Property(x => x.TraceUrl).HasColumnName("trace_url");
            e.Property(x => x.TraceSignals).HasColumnName("trace_signals").HasColumnType("jsonb");
            e.Property(x => x.CertDaysRemaining).HasColumnName("cert_days_remaining");
            e.Property(x => x.RetryCount).HasColumnName("retry_count"); // runner 0048: attempts to verdict
            e.Property(x => x.Location).HasColumnName("location");
            e.Property(x => x.Sandbox).HasColumnName("sandbox"); // runner 0065: paused-monitor sandbox run
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
            var (rcaConv, rcaCmp) = JsonbColumn<IncidentRca?>();
            e.Property(x => x.Rca).HasColumnName("rca").HasColumnType("jsonb")
                .HasConversion(rcaConv, rcaCmp);
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

        // Keyless: read from the slo_status(check_id, from, to) function via raw SQL only.
        modelBuilder.Entity<SloStatusRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.SloTarget).HasColumnName("slo_target");
            e.Property(x => x.WindowFrom).HasColumnName("window_from");
            e.Property(x => x.WindowTo).HasColumnName("window_to");
            e.Property(x => x.TotalRuns).HasColumnName("total_runs");
            e.Property(x => x.DownRuns).HasColumnName("down_runs");
            e.Property(x => x.Budget).HasColumnName("budget");
            e.Property(x => x.Consumed).HasColumnName("consumed");
            e.Property(x => x.Remaining).HasColumnName("remaining");
            e.Property(x => x.RemainingPct).HasColumnName("remaining_pct");
            e.Property(x => x.BurnRate).HasColumnName("burn_rate");
        });

        // Keyless: GET /reports/slo — per-check budget from LATERAL slo_status(...) + name/kind, raw SQL only.
        // Keyless: GET /reports/cost — one row per ENABLED check with raw cost inputs (raw SQL only; the
        // dollar figures are computed in CostReportProjection from the config rate).
        modelBuilder.Entity<CostReportRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.SourceKey).HasColumnName("source_key");
            e.Property(x => x.CheckName).HasColumnName("check_name");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.IntervalSeconds).HasColumnName("interval_seconds");
            e.Property(x => x.RegionCount).HasColumnName("region_count");
            e.Property(x => x.AvgDurationS).HasColumnName("avg_duration_s");
            e.Property(x => x.SumDurationS7d).HasColumnName("sum_duration_s_7d");
        });

        modelBuilder.Entity<SloReportRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.CheckName).HasColumnName("check_name");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.SloTarget).HasColumnName("slo_target");
            e.Property(x => x.TotalRuns).HasColumnName("total_runs");
            e.Property(x => x.DownRuns).HasColumnName("down_runs");
            e.Property(x => x.Budget).HasColumnName("budget");
            e.Property(x => x.Consumed).HasColumnName("consumed");
            e.Property(x => x.Remaining).HasColumnName("remaining");
            e.Property(x => x.RemainingPct).HasColumnName("remaining_pct");
            e.Property(x => x.BurnRate).HasColumnName("burn_rate");
            e.Property(x => x.BurnState).HasColumnName("burn_state");
            e.Property(x => x.ReportedBurn).HasColumnName("reported_burn");
        });

        // Keyless: GET /reports/mttr — one row per incident in the window (+ its check), raw SQL only.
        modelBuilder.Entity<MttrIncidentRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.CheckName).HasColumnName("check_name");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.OpenedAt).HasColumnName("opened_at");
            e.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
            e.Property(x => x.Classification).HasColumnName("classification");
            e.Property(x => x.ConsecutiveFailures).HasColumnName("consecutive_failures");
            e.Property(x => x.IntervalSeconds).HasColumnName("interval_seconds");
        });

        // Keyless: the incident verdict-taxonomy breakdown (GROUP BY rca->>'classification'), raw SQL only.
        modelBuilder.Entity<IncidentBreakdownRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.Classification).HasColumnName("classification");
            e.Property(x => x.Count).HasColumnName("count");
        });

        // Keyless: §D1 trust scorecard — one row per enabled check (run/retry/last-green + RCA counts +
        // latest spec provenance). The chip + retryRate are derived in TrustReportProjection. Raw SQL only.
        modelBuilder.Entity<TrustMonitorRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.CheckName).HasColumnName("check_name");
            e.Property(x => x.Sensitive).HasColumnName("sensitive");
            e.Property(x => x.IntervalSeconds).HasColumnName("interval_seconds");
            e.Property(x => x.LastRunAt).HasColumnName("last_run_at");
            e.Property(x => x.LastGreenAt).HasColumnName("last_green_at");
            e.Property(x => x.RunCount).HasColumnName("run_count");
            e.Property(x => x.RetryCount).HasColumnName("retry_count");
            e.Property(x => x.RetriedPasses).HasColumnName("retried_passes");
            e.Property(x => x.IncidentTotal).HasColumnName("incident_total");
            e.Property(x => x.RealOutage).HasColumnName("real_outage");
            e.Property(x => x.FlakyTransient).HasColumnName("flaky_transient");
            e.Property(x => x.SelectorDrift).HasColumnName("selector_drift");
            e.Property(x => x.EnvironmentRegional).HasColumnName("environment_regional");
            e.Property(x => x.PerfRegression).HasColumnName("perf_regression");
            e.Property(x => x.Unclassified).HasColumnName("unclassified");
            e.Property(x => x.ExecutedSha256).HasColumnName("executed_sha256");
            e.Property(x => x.SpecPath).HasColumnName("spec_path");
            e.Property(x => x.RedTestedAt).HasColumnName("red_tested_at");
            e.Property(x => x.RedTestMethod).HasColumnName("red_test_method");
        });

        // Keyless: §D1 trust detail — daily retry-rate trend for one check (the detail sparkline). Raw SQL only.
        modelBuilder.Entity<TrustRetryDayRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.Day).HasColumnName("day");
            e.Property(x => x.RunCount).HasColumnName("run_count");
            e.Property(x => x.RetryCount).HasColumnName("retry_count");
        });

        // Keyless: GET /status — area-tagged checks' current signal / SLA / recent incidents, raw SQL only.
        modelBuilder.Entity<StatusCheckRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.Property).HasColumnName("property");
            e.Property(x => x.Severity).HasColumnName("severity");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.HasOpenIncident).HasColumnName("has_open_incident");
            e.Property(x => x.OpenSeverity).HasColumnName("open_severity");
        });
        modelBuilder.Entity<StatusSlaRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.Property).HasColumnName("property");
            e.Property(x => x.CompletedRuns).HasColumnName("completed_runs");
            e.Property(x => x.UpRuns).HasColumnName("up_runs");
            e.Property(x => x.DownRuns).HasColumnName("down_runs");
        });
        modelBuilder.Entity<StatusIncidentRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.Property).HasColumnName("property");
            e.Property(x => x.CheckName).HasColumnName("check_name");
            e.Property(x => x.Summary).HasColumnName("summary");
            e.Property(x => x.OpenedAt).HasColumnName("opened_at");
            e.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Severity).HasColumnName("severity");
        });

        // Keyless: GET /reports/deploys — auto-detected deploy markers per host (migration 0056), raw SQL only.
        modelBuilder.Entity<DeployRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.TargetHost).HasColumnName("target_host");
            e.Property(x => x.Sha).HasColumnName("sha");
            e.Property(x => x.Fingerprint).HasColumnName("fingerprint");
            e.Property(x => x.IsSha).HasColumnName("is_sha");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.DeployedAt).HasColumnName("deployed_at");
        });

        // Keyless: the incident-detail deploy-proximity annotation (deploys detected near an incident +
        // a computed signed minute offset), raw SQL only.
        modelBuilder.Entity<NearbyDeployRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.DetectedAt).HasColumnName("detected_at");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.IsSha).HasColumnName("is_sha");
            e.Property(x => x.Sha).HasColumnName("sha");
            e.Property(x => x.Fingerprint).HasColumnName("fingerprint");
            e.Property(x => x.OffsetMinutes).HasColumnName("offset_minutes");
        });

        // Keyless: GET /reports/egress — per-(location, egress_ip) roll-up from runs (0054), raw SQL only.
        modelBuilder.Entity<EgressRunRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.Location).HasColumnName("location");
            e.Property(x => x.Ip).HasColumnName("ip");
            e.Property(x => x.RunCount).HasColumnName("run_count");
            e.Property(x => x.FirstSeen).HasColumnName("first_seen");
            e.Property(x => x.LastSeen).HasColumnName("last_seen");
        });

        // Keyless: GET /reports/region-health — one row per enabled region, freshness = MAX(check_locations
        // .last_run_at). raw SQL only (LEFT JOIN locations→check_locations); last_run_at NULL = never reported.
        modelBuilder.Entity<RegionHealthRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.Location).HasColumnName("location");
            e.Property(x => x.LastRunAt).HasColumnName("last_run_at");
        });

        // Keyless: the check-detail per-location rollup — one row per ASSIGNED location (check_locations),
        // LEFT JOIN LATERAL its latest run's status. raw SQL only (see ChecksFunctions.CheckLocationsRollupAsync);
        // status NULL = assigned-but-never-run (pending). Same check_locations-driven discipline as RegionHealthRow.
        modelBuilder.Entity<CheckLocationStatusRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.Location).HasColumnName("location");
            e.Property(x => x.Status).HasColumnName("status");
        });

        // Keyless: read the inline availability-over-time bucketed query via raw SQL only.
        modelBuilder.Entity<AvailabilitySeriesPointRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.Ts).HasColumnName("ts");
            e.Property(x => x.UpRuns).HasColumnName("up_runs");
            e.Property(x => x.DownRuns).HasColumnName("down_runs");
            e.Property(x => x.AvailabilityPct).HasColumnName("availability_pct");
        });

        modelBuilder.Entity<AvailabilityReportRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.GroupValue).HasColumnName("group_value");
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.CheckName).HasColumnName("check_name");
            e.Property(x => x.UpCount).HasColumnName("up_count");
            e.Property(x => x.DownCount).HasColumnName("down_count");
            e.Property(x => x.TotalCount).HasColumnName("total_count");
            e.Property(x => x.DowntimeMinutes).HasColumnName("downtime_minutes");
            e.Property(x => x.IncidentsOpened).HasColumnName("incidents_opened");
        });

        modelBuilder.Entity<AvailabilitySeriesRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.GroupValue).HasColumnName("group_value");
            e.Property(x => x.Day).HasColumnName("day");
            e.Property(x => x.UpCount).HasColumnName("up_count");
            e.Property(x => x.DownCount).HasColumnName("down_count");
        });

        modelBuilder.Entity<LatencyReportRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.GroupValue).HasColumnName("group_value");
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.LatencyCount).HasColumnName("latency_count");
            e.Property(x => x.AvgMs).HasColumnName("avg_ms");
            e.Property(x => x.P50Ms).HasColumnName("p50_ms");
            e.Property(x => x.P95Ms).HasColumnName("p95_ms");
            e.Property(x => x.P99Ms).HasColumnName("p99_ms");
        });

        modelBuilder.Entity<VitalsReportRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.GroupValue).HasColumnName("group_value");
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.VitalsCount).HasColumnName("vitals_count");
            e.Property(x => x.LcpP75Ms).HasColumnName("lcp_p75_ms");
            e.Property(x => x.FcpP75Ms).HasColumnName("fcp_p75_ms");
            e.Property(x => x.TtfbP75Ms).HasColumnName("ttfb_p75_ms");
            e.Property(x => x.ClsP75).HasColumnName("cls_p75");
            e.Property(x => x.InpP75Ms).HasColumnName("inp_p75_ms");
            e.Property(x => x.InpCount).HasColumnName("inp_count");
            e.Property(x => x.ResourceCount).HasColumnName("resource_count");
        });

        modelBuilder.Entity<LatencySeriesRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.GroupValue).HasColumnName("group_value");
            e.Property(x => x.Day).HasColumnName("day");
            e.Property(x => x.AvgMs).HasColumnName("avg_ms");
        });

        modelBuilder.Entity<ReportNarrativeRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.ScopeType).HasColumnName("scope_type");
            e.Property(x => x.ScopeKey).HasColumnName("scope_key");
            e.Property(x => x.Window).HasColumnName("window");
            e.Property(x => x.GeneratedAt).HasColumnName("generated_at");
            e.Property(x => x.Headline).HasColumnName("headline");
            e.Property(x => x.Body).HasColumnName("body");
            e.Property(x => x.Highlights).HasColumnName("highlights");
            e.Property(x => x.FactPack).HasColumnName("fact_pack");
            e.Property(x => x.Model).HasColumnName("model");
        });

        // Keyless: read the runner-owned reconcile_drift table via raw SQL only (read-only drift surface).
        modelBuilder.Entity<ReconcileDriftRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.SourceKey).HasColumnName("source_key");
            e.Property(x => x.DriftType).HasColumnName("drift_type");
            e.Property(x => x.Detail).HasColumnName("detail");
            e.Property(x => x.DetectedAt).HasColumnName("detected_at");
        });

        // Keyless: the runner-written reconcile_apply_plan (dry-run, runner migration 0051) via raw SQL only.
        modelBuilder.Entity<ReconcileApplyPlanRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.SourceKey).HasColumnName("source_key");
            e.Property(x => x.DriftType).HasColumnName("drift_type");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Plan).HasColumnName("plan");
            e.Property(x => x.ComputedAt).HasColumnName("computed_at");
        });

        // Keyless: the spec-catalog read (spec_catalog LEFT JOIN checks + health) via raw SQL only.
        // Column names match the GetSpecCatalog query's aliases.
        modelBuilder.Entity<SpecCatalogRow>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            e.Property(x => x.SourceKey).HasColumnName("source_key");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.SpecPath).HasColumnName("spec_path");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.Target).HasColumnName("target");
            e.Property(x => x.SuggestedIntervalSeconds).HasColumnName("suggested_interval_seconds");
            e.Property(x => x.Tags).HasColumnName("tags");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.EnabledByDefault).HasColumnName("enabled_by_default");
            e.Property(x => x.Runnable).HasColumnName("runnable");
            e.Property(x => x.NotRunnableReason).HasColumnName("not_runnable_reason");
            e.Property(x => x.ProbedAt).HasColumnName("probed_at");
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.CheckName).HasColumnName("check_name");
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.CurrentStatus).HasColumnName("current_status");
            e.Property(x => x.LastRunAt).HasColumnName("last_run_at");
            e.Property(x => x.P95Ms).HasColumnName("p95_ms");
            e.Property(x => x.OpenIncidentCount).HasColumnName("open_incident_count");
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
