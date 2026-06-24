using System.Text.Json.Serialization;

namespace SynthWatch.Api.Dtos;

// Report responses. groupBy is the tag key when grouped (null = ungrouped/fleet, served as one group
// with group=null). Availability aggregates additively from the daily rollup; latency/web-vitals
// percentiles are recomputed from raw runs over the window (NOT averaged daily percentiles).

public record AvailabilityPointDtoR(
    [property: JsonPropertyName("day")] DateOnly Day,
    [property: JsonPropertyName("availabilityPct")] decimal? AvailabilityPct,
    [property: JsonPropertyName("upCount")] long UpCount,
    [property: JsonPropertyName("downCount")] long DownCount);

public record AvailabilityCheckDto(
    [property: JsonPropertyName("checkId")] long CheckId,
    [property: JsonPropertyName("checkName")] string CheckName,
    [property: JsonPropertyName("availabilityPct")] decimal? AvailabilityPct,
    [property: JsonPropertyName("upCount")] long UpCount,
    [property: JsonPropertyName("downCount")] long DownCount,
    [property: JsonPropertyName("downtimeMinutes")] decimal DowntimeMinutes,
    [property: JsonPropertyName("incidentsOpened")] long IncidentsOpened);

public record AvailabilityGroupDto(
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("availabilityPct")] decimal? AvailabilityPct,
    [property: JsonPropertyName("upCount")] long UpCount,
    [property: JsonPropertyName("downCount")] long DownCount,
    [property: JsonPropertyName("totalCount")] long TotalCount,
    [property: JsonPropertyName("downtimeMinutes")] decimal DowntimeMinutes,
    [property: JsonPropertyName("incidentsOpened")] long IncidentsOpened,
    [property: JsonPropertyName("checks")] IReadOnlyList<AvailabilityCheckDto> Checks,
    [property: JsonPropertyName("series")] IReadOnlyList<AvailabilityPointDtoR> Series);

public record AvailabilityReportDto(
    [property: JsonPropertyName("window")] string Window,
    [property: JsonPropertyName("groupBy")] string? GroupBy,
    [property: JsonPropertyName("groups")] IReadOnlyList<AvailabilityGroupDto> Groups);

/// <summary>Latency over the window — percentiles recomputed from raw (NOT averaged daily percentiles).</summary>
public record LatencyDto(
    [property: JsonPropertyName("sampleCount")] long SampleCount,
    [property: JsonPropertyName("avgMs")] double? AvgMs,
    [property: JsonPropertyName("p50Ms")] int? P50Ms,
    [property: JsonPropertyName("p95Ms")] int? P95Ms,
    [property: JsonPropertyName("p99Ms")] int? P99Ms);

/// <summary>Browser web-vitals over the window (p75, recomputed from raw). Null for groups/checks with no browser runs. No INP.</summary>
public record WebVitalsDto(
    [property: JsonPropertyName("sampleCount")] long SampleCount,
    [property: JsonPropertyName("lcpP75Ms")] int? LcpP75Ms,
    [property: JsonPropertyName("fcpP75Ms")] int? FcpP75Ms,
    [property: JsonPropertyName("ttfbP75Ms")] int? TtfbP75Ms,
    [property: JsonPropertyName("clsP75")] double? ClsP75);

public record LatencyPointDto(
    [property: JsonPropertyName("day")] DateOnly Day,
    [property: JsonPropertyName("avgMs")] double? AvgMs);

public record PerformanceCheckDto(
    [property: JsonPropertyName("checkId")] long CheckId,
    [property: JsonPropertyName("checkName")] string CheckName,
    [property: JsonPropertyName("latency")] LatencyDto Latency,
    [property: JsonPropertyName("webVitals")] WebVitalsDto? WebVitals);

public record PerformanceGroupDto(
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("latency")] LatencyDto Latency,
    [property: JsonPropertyName("webVitals")] WebVitalsDto? WebVitals,
    [property: JsonPropertyName("checks")] IReadOnlyList<PerformanceCheckDto> Checks,
    [property: JsonPropertyName("series")] IReadOnlyList<LatencyPointDto> Series);

public record PerformanceReportDto(
    [property: JsonPropertyName("window")] string Window,
    [property: JsonPropertyName("groupBy")] string? GroupBy,
    [property: JsonPropertyName("groups")] IReadOnlyList<PerformanceGroupDto> Groups);
