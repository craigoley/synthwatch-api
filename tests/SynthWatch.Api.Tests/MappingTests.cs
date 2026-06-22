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

        var dto = CheckSummaryDto.From(check, latest, metrics);

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
}
