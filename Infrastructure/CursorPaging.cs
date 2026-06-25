using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// A keyset cursor position: the <c>(timestamp, id)</c> of the LAST row on a page. The next page
/// is every row ordered strictly after it. Used for append-only lists — runs on <c>started_at</c>,
/// incidents on <c>opened_at</c> next — where OFFSET re-scans the prefix and double-counts as new
/// rows insert at the head; a keyset cursor is stable under those inserts. The <c>id</c> tie-breaks
/// rows that share a timestamp so no row is skipped or repeated across a page boundary.
/// </summary>
public readonly record struct CursorPosition(DateTimeOffset Ts, long Id)
{
    /// <summary>
    /// Opaque token: base64url of <c>"{utcTicks}.{id}"</c>. Opaque on purpose — clients echo it
    /// back verbatim as <c>?cursor=</c>, so the encoding can change without a contract break.
    /// </summary>
    public string Encode()
    {
        var raw = $"{Ts.UtcTicks}.{Id}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Decode a token produced by <see cref="Encode"/>. False (not throw) on any garbage.</summary>
    public static bool TryDecode(string? token, out CursorPosition position)
    {
        position = default;
        if (string.IsNullOrEmpty(token))
            return false;

        try
        {
            var b64 = token.Replace('-', '+').Replace('_', '/');
            b64 += (b64.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var dot = raw.IndexOf('.');
            if (dot <= 0)
                return false;
            if (!long.TryParse(raw.AsSpan(0, dot), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                return false;
            if (!long.TryParse(raw.AsSpan(dot + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return false;
            if (ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
                return false;
            position = new CursorPosition(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException)
        {
            return false; // not valid base64
        }
    }
}

/// <summary>
/// A parsed cursor + date-range request. <see cref="Error"/> is non-null when a supplied param was
/// malformed (the handler returns 400). Otherwise the window is ALWAYS bounded: <see cref="From"/>
/// defaults to <see cref="To"/> minus <see cref="CursorPaging.DefaultWindow"/>, so a param-less call
/// never scans all-time — the whole point of the date-range default.
/// </summary>
public readonly record struct CursorRange(
    DateTimeOffset From,
    DateTimeOffset To,
    CursorPosition? Cursor,
    int PageSize,
    string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// Reusable parsing of <c>?from=&amp;to=&amp;cursor=&amp;pageSize=</c> for append-only list endpoints.
/// Keep the SHAPE identical across lists (runs now, incidents next) so the dashboard's cursor +
/// date-range UX is one pattern.
/// </summary>
public static class CursorPaging
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>The default look-back when no <c>from</c> is supplied — recent, so the query is bounded.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Parse the date-range + cursor + page size with safe bounds. <paramref name="now"/> is injected
    /// (not <c>DateTimeOffset.UtcNow</c>) so the default window is deterministic in tests.
    /// </summary>
    public static CursorRange Parse(HttpRequest req, DateTimeOffset now)
    {
        var to = now;
        var toRaw = req.Query["to"].ToString();
        if (!string.IsNullOrEmpty(toRaw) && !TryParseTimestamp(toRaw, out to))
            return Invalid("to must be an ISO-8601 timestamp.");

        var from = to - DefaultWindow;
        var fromRaw = req.Query["from"].ToString();
        if (!string.IsNullOrEmpty(fromRaw) && !TryParseTimestamp(fromRaw, out from))
            return Invalid("from must be an ISO-8601 timestamp.");

        if (from > to)
            return Invalid("from must be on or before to.");

        CursorPosition? cursor = null;
        var cursorRaw = req.Query["cursor"].ToString();
        if (!string.IsNullOrEmpty(cursorRaw))
        {
            if (!CursorPosition.TryDecode(cursorRaw, out var pos))
                return Invalid("cursor is malformed.");
            cursor = pos;
        }

        var pageSize = DefaultPageSize;
        if (int.TryParse(req.Query["pageSize"], out var ps) && ps > 0)
            pageSize = Math.Min(ps, MaxPageSize);

        return new CursorRange(from, to, cursor, pageSize, null);
    }

    private static CursorRange Invalid(string error) =>
        new(default, default, null, DefaultPageSize, error);

    private static bool TryParseTimestamp(string raw, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
}
