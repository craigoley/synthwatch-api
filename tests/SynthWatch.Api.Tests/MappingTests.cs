using System.Text.Json;
using SynthWatch.Api.Data;
using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>Pure DTO-mapping + status-classification tests (no DB).</summary>
public class MappingTests
{
    [Fact]
    public void RunDto_surfaces_artifact_proxy_paths_not_raw_urls()
    {
        var run = new Run
        {
            Id = 42, CheckId = 2, Status = "fail",
            ScreenshotUrl = "https://acct.blob.core.windows.net/synthwatch-artifacts/run-42.png",
            TraceUrl = "https://acct.blob.core.windows.net/synthwatch-artifacts/traces/run-42.zip"
        };
        var dto = RunDto.From(run);
        Assert.Equal("/api/runs/42/screenshot", dto.ScreenshotUrl); // proxy path, not the blob URL
        Assert.Equal("/api/runs/42/trace", dto.TraceUrl);
    }

    [Fact]
    public void RunDto_artifact_paths_null_when_absent()
    {
        var dto = RunDto.From(new Run { Id = 7, CheckId = 1, Status = "pass" });
        Assert.Null(dto.ScreenshotUrl);
        Assert.Null(dto.TraceUrl);
    }

    [Fact]
    public void RunDto_surfaces_location_and_defaults_to_default()
    {
        // multi-location run carries its region
        Assert.Equal("westus", RunDto.From(new Run { Id = 1, CheckId = 1, Status = "fail", Location = "westus" }).Location);
        // legacy/empty -> "default" (never null, dashboard-safe)
        Assert.Equal("default", RunDto.From(new Run { Id = 2, CheckId = 1, Status = "pass", Location = "" }).Location);
    }

    [Fact]
    public void RunDto_surfaces_sandbox_flag()
    {
        // sandbox run (paused-monitor on-demand validation) is distinguishable...
        Assert.True(RunDto.From(new Run { Id = 1, CheckId = 1, Status = "pass", Sandbox = true }).Sandbox);
        // ...and a normal run defaults false (dashboard renders no badge)
        Assert.False(RunDto.From(new Run { Id = 2, CheckId = 1, Status = "pass" }).Sandbox);
    }

    [Fact]
    public void RunDto_surfaces_confirmation_linkage()
    {
        // confirmation-retry (0077): a confirmation run points at its original; a superseded transient points at
        // the confirmation that resolved it. A normal run has neither (null).
        var confirmation = RunDto.From(new Run { Id = 10, CheckId = 1, Status = "pass", ConfirmationOfRunId = 9 });
        Assert.Equal(9, confirmation.ConfirmationOfRunId);
        var transient = RunDto.From(new Run { Id = 9, CheckId = 1, Status = "fail", SupersededByRunId = 10 });
        Assert.Equal(10, transient.SupersededByRunId);
        var normal = RunDto.From(new Run { Id = 2, CheckId = 1, Status = "pass" });
        Assert.Null(normal.ConfirmationOfRunId);
        Assert.Null(normal.SupersededByRunId);
    }

    [Fact]
    public void RunDto_HasTraceSignals_is_independent_of_TraceUrl()
    {
        // ★ The sensitive-green-run case: NO downloadable trace (TraceUrl null, by B10 design) but the
        // redacted trace_signals ARE persisted → the dashboard can still surface the summary as "has trace".
        var dto = RunDto.From(new Run { Id = 1, CheckId = 1, Status = "pass", TraceUrl = null, TraceSignals = "{\"network\":{}}" });
        Assert.True(dto.HasTraceSignals);
        Assert.Null(dto.TraceUrl);
        // No signals persisted → false (a run that genuinely has no trace data).
        Assert.False(RunDto.From(new Run { Id = 2, CheckId = 1, Status = "pass", TraceSignals = null }).HasTraceSignals);
        Assert.False(RunDto.From(new Run { Id = 3, CheckId = 1, Status = "pass", TraceSignals = "" }).HasTraceSignals);
    }

    [Theory]
    [InlineData("pass", "up")]
    [InlineData("warn", "up")]   // warn counts as up (matches sla_availability)
    [InlineData("fail", "down")]
    [InlineData("error", "down")]
    [InlineData("running", "running")]
    [InlineData("weird", "unknown")]
    public void RunStatus_classifies_health_correctly(string status, string expected)
        => Assert.Equal(expected, RunStatus.Classify(status));

    [Fact]
    public void CheckSummary_carries_parity_fields_and_derived_health()
    {
        var check = new Check { Id = 1, Name = "c", Kind = "http", TargetUrl = "https://x", Enabled = true };
        var latest = new Run { Id = 9, CheckId = 1, Status = "warn" };
        var metrics = new CheckMetricsDto(50.0, 90.0, 100, 1, "critical",
            new List<SparkPoint> { new(default, 30, "pass") });
        var locations = new List<LocationStatusDto> { new("default", "warn") };

        var tags = new List<TagDto> { new("team", "web") };
        var dto = CheckSummaryDto.From(check, latest, metrics, locations, tags);

        Assert.Equal("team", Assert.Single(dto.Tags).Key);
        Assert.Equal(50.0, dto.P50Ms);
        Assert.Equal(90.0, dto.P95Ms);
        Assert.Equal(100, dto.Runs24h);
        Assert.Single(dto.Spark);
        Assert.Equal(1, dto.OpenIncidentCount);
        Assert.Equal("critical", dto.MaxOpenSeverity);
        Assert.True(dto.HasOpenIncident);
        Assert.Equal("warn", dto.CurrentStatus);  // raw latest status
        Assert.Equal("up", dto.CurrentHealth);     // warn -> up
    }

    [Fact]
    public void Check_dtos_project_environment_on_both_summary_and_detail()
    {
        // A pre-prod monitor surfaces its environment so the dashboard can render/filter env-aware
        // (runner 0059; non-prod is excluded from the fleet rollups). Projected on BOTH read DTOs.
        var staging = new Check { Id = 1, Name = "c", Kind = "http", TargetUrl = "https://x", Environment = "staging" };
        Assert.Equal("staging", CheckSummaryDto.From(staging, null, CheckMetricsDto.Empty,
            Array.Empty<LocationStatusDto>(), Array.Empty<TagDto>()).Environment);
        Assert.Equal("staging", CheckDetailDto.From(staging, Array.Empty<Run>(), Array.Empty<TagDto>()).Environment);

        // The entity default mirrors the column's NOT NULL DEFAULT 'prod' — a check with no env set is prod.
        var prod = new Check { Id = 2, Name = "c", Kind = "http", TargetUrl = "https://x" };
        Assert.Equal("prod", CheckSummaryDto.From(prod, null, CheckMetricsDto.Empty,
            Array.Empty<LocationStatusDto>(), Array.Empty<TagDto>()).Environment);
        Assert.Equal("prod", CheckDetailDto.From(prod, Array.Empty<Run>(), Array.Empty<TagDto>()).Environment);
    }

    [Fact]
    public void Env_override_yields_effective_env_and_source_on_both_dtos()
    {
        // env PR-3: environment_override (dashboard-owned) WINS over the derived environment. Effective =
        // override ?? environment; source = "override" when set, else "derived". The git-authoritative
        // `Environment` is surfaced unchanged alongside so the UI can show "staging (overridden from prod)".
        var overridden = new Check
        {
            Id = 1, Name = "c", Kind = "http", TargetUrl = "https://x",
            Environment = "prod", EnvironmentOverride = "staging",
        };
        var s = CheckSummaryDto.From(overridden, null, CheckMetricsDto.Empty, Array.Empty<LocationStatusDto>(), Array.Empty<TagDto>());
        Assert.Equal("prod", s.Environment);              // the derived (git) env is unchanged
        Assert.Equal("staging", s.EnvironmentOverride);
        Assert.Equal("staging", s.EffectiveEnvironment);  // the override wins
        Assert.Equal("override", s.EnvironmentSource);
        var d = CheckDetailDto.From(overridden, Array.Empty<Run>(), Array.Empty<TagDto>());
        Assert.Equal("staging", d.EffectiveEnvironment);
        Assert.Equal("override", d.EnvironmentSource);

        // No override → effective == derived env, source "derived".
        var derived = new Check { Id = 2, Name = "c", Kind = "http", TargetUrl = "https://x", Environment = "staging", EnvironmentOverride = null };
        var ds = CheckSummaryDto.From(derived, null, CheckMetricsDto.Empty, Array.Empty<LocationStatusDto>(), Array.Empty<TagDto>());
        Assert.Null(ds.EnvironmentOverride);
        Assert.Equal("staging", ds.EffectiveEnvironment);
        Assert.Equal("derived", ds.EnvironmentSource);
    }

    [Fact]
    public void Archive_surfaces_on_dtos_and_takes_precedence_over_paused_in_status()
    {
        // An ARCHIVED check (archived_at set) surfaces ArchivedAt on both read DTOs and reads CurrentStatus
        // = "archived" — even when ALSO paused (enabled=false): archive is the deliberate retire, so it wins.
        var archived = new Check
        {
            Id = 1, Name = "c", Kind = "http", TargetUrl = "https://x",
            Enabled = false, ArchivedAt = DateTimeOffset.UtcNow,
        };
        var summary = CheckSummaryDto.From(archived, new Run { Id = 1, CheckId = 1, Status = "pass" },
            CheckMetricsDto.Empty, Array.Empty<LocationStatusDto>(), Array.Empty<TagDto>());
        Assert.NotNull(summary.ArchivedAt);
        Assert.Equal("archived", summary.CurrentStatus);       // archived, not "paused" or "pass"
        Assert.Equal(RunStatus.HealthPaused, summary.CurrentHealth);
        var detail = CheckDetailDto.From(archived, Array.Empty<Run>(), Array.Empty<TagDto>());
        Assert.NotNull(detail.ArchivedAt);
        Assert.Equal("archived", detail.CurrentStatus);

        // An ACTIVE check (archived_at null) is unchanged — ArchivedAt null, status from the run.
        var active = new Check { Id = 2, Name = "c", Kind = "http", TargetUrl = "https://x", Enabled = true };
        var activeDto = CheckSummaryDto.From(active, new Run { Id = 2, CheckId = 2, Status = "pass" },
            CheckMetricsDto.Empty, Array.Empty<LocationStatusDto>(), Array.Empty<TagDto>());
        Assert.Null(activeDto.ArchivedAt);
        Assert.Equal("pass", activeDto.CurrentStatus);
    }

    [Fact]
    public void Removed_surfaces_on_dtos_and_takes_precedence_over_archived_and_paused()
    {
        // A GIT-REMOVED check (removed_at set) surfaces RemovedAt on both read DTOs and reads CurrentStatus
        // = "removed" — even when ALSO archived + paused: removal (purging) supersedes archive (edge-case a).
        var removed = new Check
        {
            Id = 1, Name = "c", Kind = "http", TargetUrl = "https://x",
            Enabled = false, ArchivedAt = DateTimeOffset.UtcNow, RemovedAt = DateTimeOffset.UtcNow,
        };
        var summary = CheckSummaryDto.From(removed, new Run { Id = 1, CheckId = 1, Status = "pass" },
            CheckMetricsDto.Empty, Array.Empty<LocationStatusDto>(), Array.Empty<TagDto>());
        Assert.NotNull(summary.RemovedAt);
        Assert.Equal("removed", summary.CurrentStatus);        // removed, not "archived"/"paused"/"pass"
        Assert.Equal(RunStatus.HealthPaused, summary.CurrentHealth);
        var detail = CheckDetailDto.From(removed, Array.Empty<Run>(), Array.Empty<TagDto>());
        Assert.NotNull(detail.RemovedAt);
        Assert.Equal("removed", detail.CurrentStatus);

        // An active check has RemovedAt null (unchanged).
        var active = new Check { Id = 2, Name = "c", Kind = "http", TargetUrl = "https://x", Enabled = true };
        Assert.Null(CheckSummaryDto.From(active, null, CheckMetricsDto.Empty,
            Array.Empty<LocationStatusDto>(), Array.Empty<TagDto>()).RemovedAt);
    }

    [Fact]
    public void ApplyPatch_archive_sets_and_clears_archived_at_without_touching_enabled()
    {
        var check = new Check { Id = 1, Name = "c", Kind = "http", TargetUrl = "https://x", Enabled = true };

        // { archived: true } stamps archived_at, leaves enabled alone (archive ≠ pause).
        var errs = CheckValidation.ApplyPatch(new UpdateCheckRequest { Archived = true }, check);
        Assert.Empty(errs);
        Assert.NotNull(check.ArchivedAt);
        Assert.True(check.Enabled); // pause state untouched, so re-activation restores it

        // { archived: false } clears it (re-activate); still doesn't touch enabled.
        CheckValidation.ApplyPatch(new UpdateCheckRequest { Archived = false }, check);
        Assert.Null(check.ArchivedAt);
        Assert.True(check.Enabled);

        // Omitted → unchanged (a partial patch that only edits the name must not un-archive).
        check.ArchivedAt = DateTimeOffset.UtcNow;
        CheckValidation.ApplyPatch(new UpdateCheckRequest { Name = "renamed" }, check);
        Assert.NotNull(check.ArchivedAt);
    }

    [Fact]
    public void CheckSummary_carries_per_location_rollup()
    {
        var check = new Check { Id = 1, Name = "c", Kind = "http", TargetUrl = "https://x", Enabled = true };
        var locations = new List<LocationStatusDto> { new("eastus2", "pass"), new("westus", "fail") };

        var dto = CheckSummaryDto.From(check, new Run { Id = 1, CheckId = 1, Status = "fail" },
            CheckMetricsDto.Empty, locations, Array.Empty<TagDto>());

        Assert.Equal(2, dto.Locations.Count);
        Assert.Equal("eastus2", dto.Locations[0].Location);
        Assert.Equal("pass", dto.Locations[0].Status);
        Assert.Equal("westus", dto.Locations[1].Location);
        Assert.Equal("fail", dto.Locations[1].Status);
    }

    [Fact]
    public void Check_dtos_project_secret_header_references_never_values()
    {
        // Item 1: model B — secret_headers stores ENCRYPTED VALUES ({ headerName -> ciphertext }). The
        // cred-mgmt UI reads WHICH slots are set, MASKED — never the value OR the ciphertext (write-only).
        var stored = new Dictionary<string, string>
        {
            ["X-Api-Key"] = "v1:CIPHERTEXTAAAA",
            ["X-Store-Id"] = "v1:CIPHERTEXTBBBB",
        };
        var check = new Check { Id = 1, Name = "c", Kind = "http", TargetUrl = "https://x", Enabled = true, SecretHeaders = stored };

        var summary = CheckSummaryDto.From(check, null, CheckMetricsDto.Empty, Array.Empty<LocationStatusDto>(), Array.Empty<TagDto>());
        var detail = CheckDetailDto.From(check, Array.Empty<Run>(), Array.Empty<TagDto>());

        foreach (var sh in new[] { summary.SecretHeaders, detail.SecretHeaders })
        {
            Assert.NotNull(sh);
            // MASKED: the keys are visible (so the editor can render rows) but every value is "set".
            Assert.Equal("set", sh!["X-Api-Key"]);
            Assert.Equal("set", sh["X-Store-Id"]);
            // ★ NO ciphertext (or value) anywhere in the projected map.
            Assert.DoesNotContain(sh.Values, v => v.StartsWith("v1:"));
        }
    }

    [Fact]
    public void Check_dtos_secret_headers_null_when_none()
    {
        var check = new Check { Id = 1, Name = "c", Kind = "http", TargetUrl = "https://x", Enabled = true };
        Assert.Null(CheckSummaryDto.From(check, null, CheckMetricsDto.Empty, Array.Empty<LocationStatusDto>(), Array.Empty<TagDto>()).SecretHeaders);
        Assert.Null(CheckDetailDto.From(check, Array.Empty<Run>(), Array.Empty<TagDto>()).SecretHeaders);
    }

    [Fact]
    public void RunDto_exposes_trace_and_screenshot_with_no_sensitive_filter()
    {
        // Item 2: a sensitive monitor's run artifacts are NOT nulled on the API DTO — RunDto.From has no
        // sensitivity branch; access control lives at the (session-gated) artifact endpoints, not by hiding
        // the proxy link. (Populated once runner 1b ships; the projection is ready + tolerant of empty now.)
        var run = new Run
        {
            Id = 88, CheckId = 3, Status = "fail",
            ScreenshotUrl = "https://acct.blob.core.windows.net/synthwatch-artifacts/run-88.png",
            TraceUrl = "https://acct.blob.core.windows.net/synthwatch-artifacts/traces/run-88.zip",
        };
        var dto = RunDto.From(run);
        Assert.Equal("/api/runs/88/screenshot", dto.ScreenshotUrl);
        Assert.Equal("/api/runs/88/trace", dto.TraceUrl);
    }

    [Fact]
    public void IncidentDto_carries_rca()
    {
        var inc = new Incident
        {
            Id = 1, CheckId = 1, Status = "open", Severity = "critical",
            Rca = new IncidentRca { Classification = "real-outage", Confidence = "high", Observed = new() { "HTTP 503" } }
        };
        var dto = IncidentDto.From(inc, "c", "http");
        Assert.NotNull(dto.Rca);
        Assert.Equal("real-outage", dto.Rca!.Classification);
        Assert.Equal("high", dto.Rca.Confidence);   // confidence is a level (string), not a number
        Assert.Equal("HTTP 503", dto.Rca.Observed![0]);
    }

    [Fact]
    public void IncidentRca_deserializes_runner_shape_including_generated_at()
    {
        // Mirrors the jsonb converter options (camelCase). Runner writes single-word keys + the one
        // snake_case key generated_at, mapped via [JsonPropertyName].
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        const string json = """
        {"classification":"flaky-transient","confidence":"low","observed":["timeout at step 2"],
         "inferred":["network blip"],"summary":"likely transient","signature":"1|err|step2",
         "model":"gpt-4o","cached":false,"generated_at":"2026-06-22T12:00:00Z"}
        """;
        var rca = JsonSerializer.Deserialize<IncidentRca>(json, opts)!;
        Assert.Equal("flaky-transient", rca.Classification);
        Assert.Equal("low", rca.Confidence);
        Assert.Single(rca.Observed!);
        Assert.False(rca.Cached);
        Assert.Equal("2026-06-22T12:00:00Z", rca.GeneratedAt); // snake_case key mapped correctly
    }

    [Fact]
    public void IncidentDto_tolerates_null_check_name() // LEFT JOIN: missing check -> null, not dropped
    {
        var inc = new Incident { Id = 18, CheckId = 58, Status = "open", Severity = "critical" };
        var dto = IncidentDto.From(inc, null, null);
        Assert.Equal(18, dto.Id);
        Assert.Equal(58, dto.CheckId);
        Assert.Null(dto.CheckName);
        Assert.Null(dto.CheckKind);
    }

    [Fact]
    public void TimelineEntry_uses_proxy_paths_and_default_location()
    {
        var withArtifacts = TimelineEntryDto.From(new Run
        {
            Id = 5, CheckId = 1, Status = "fail",
            ScreenshotUrl = "https://acct.blob.core.windows.net/c/run-5.png",
            TraceUrl = "https://acct.blob.core.windows.net/c/run-5.zip"
        });
        Assert.Equal(5, withArtifacts.RunId);
        Assert.Equal("/api/runs/5/screenshot", withArtifacts.ScreenshotUrl); // proxy path, not blob URL
        Assert.Equal("/api/runs/5/trace", withArtifacts.TraceUrl);
        Assert.Equal("default", withArtifacts.Location);                     // empty -> default

        var bare = TimelineEntryDto.From(new Run { Id = 6, CheckId = 1, Status = "pass", Location = "westus" });
        Assert.Null(bare.ScreenshotUrl);
        Assert.Null(bare.TraceUrl);
        Assert.Equal("westus", bare.Location);
    }
}
