using System.Text.Json;
using SynthWatch.Api.Data;
using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;
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
