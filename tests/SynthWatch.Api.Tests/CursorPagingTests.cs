using Microsoft.AspNetCore.Http;
using SynthWatch.Api.Infrastructure;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// Pure tests for the reusable cursor + date-range parsing (no DB). The DB-backed walk over real
/// runs lives in <see cref="IntegrationTests.Check_runs_are_cursor_paginated_over_a_bounded_window"/>.
/// </summary>
public class CursorPagingTests
{
    private static HttpRequest Request(string? queryString = null)
    {
        var ctx = new DefaultHttpContext();
        if (queryString is not null) ctx.Request.QueryString = new QueryString(queryString);
        return ctx.Request;
    }

    [Fact]
    public void Cursor_round_trips_through_encode_decode()
    {
        var pos = new CursorPosition(new DateTimeOffset(2026, 6, 25, 12, 34, 56, TimeSpan.Zero), 987654321);
        Assert.True(CursorPosition.TryDecode(pos.Encode(), out var back));
        Assert.Equal(pos, back);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64-$$$")]
    [InlineData("bm90LWEtY3Vyc29y")] // valid base64 ("not-a-cursor") but no "ticks.id" shape
    public void Cursor_decode_rejects_garbage_without_throwing(string token)
    {
        Assert.False(CursorPosition.TryDecode(token, out _));
    }

    [Fact]
    public void Parse_defaults_to_a_bounded_recent_window()
    {
        var now = new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);
        var range = CursorPaging.Parse(Request(), now);

        Assert.True(range.IsValid);
        Assert.Equal(now, range.To);
        Assert.Equal(now - CursorPaging.DefaultWindow, range.From); // never all-time
        Assert.Null(range.Cursor);
        Assert.Equal(CursorPaging.DefaultPageSize, range.PageSize);
    }

    [Fact]
    public void Parse_honors_explicit_range_cursor_and_clamps_page_size()
    {
        var now = new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);
        var cursor = new CursorPosition(now.AddHours(-3), 42).Encode();
        var q = $"?from={Uri.EscapeDataString("2026-06-01T00:00:00Z")}" +
                $"&to={Uri.EscapeDataString("2026-06-20T00:00:00Z")}" +
                $"&cursor={Uri.EscapeDataString(cursor)}&pageSize=9999";
        var range = CursorPaging.Parse(Request(q), now);

        Assert.True(range.IsValid);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), range.From);
        Assert.Equal(new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero), range.To);
        Assert.Equal(new CursorPosition(now.AddHours(-3), 42), range.Cursor);
        Assert.Equal(CursorPaging.MaxPageSize, range.PageSize); // clamped from 9999
    }

    [Fact]
    public void Parse_overload_honors_a_custom_default_window()
    {
        var now = new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);
        // Incidents use a 30d default (sparser than runs' 7d) — still bounded, never all-time.
        var range = CursorPaging.Parse(Request(), now, TimeSpan.FromDays(30));

        Assert.True(range.IsValid);
        Assert.Equal(now, range.To);
        Assert.Equal(now - TimeSpan.FromDays(30), range.From);
    }

    [Theory]
    [InlineData("?from=not-a-date")]
    [InlineData("?to=not-a-date")]
    [InlineData("?cursor=%24%24garbage")]
    [InlineData("?from=2026-06-20T00:00:00Z&to=2026-06-01T00:00:00Z")] // from after to
    public void Parse_flags_malformed_input(string query)
    {
        var range = CursorPaging.Parse(Request(query), DateTimeOffset.UtcNow);
        Assert.False(range.IsValid);
        Assert.NotNull(range.Error);
    }
}
