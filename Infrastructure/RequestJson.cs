using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Shared JSON request-body reader for the POST/PUT handlers. ★ Guards the CONTENT-TYPE BEFORE parsing:
/// <see cref="HttpRequestJsonExtensions.ReadFromJsonAsync{T}(HttpRequest, System.Threading.CancellationToken)"/>
/// throws <see cref="System.InvalidOperationException"/> — NOT the <see cref="JsonException"/> the handlers
/// catch — when the request's Content-Type isn't a JSON type. That uncaught throw fell through to the
/// exception middleware as a shielded 500 (confirmed: a non-JSON/absent Content-Type 500'd every body
/// endpoint). This returns a clean <c>415 Unsupported Media Type</c> instead; a JSON content type with a
/// malformed body stays the existing <c>400</c>. Returns <c>(body, null)</c> on success, else
/// <c>(null, error)</c> — the caller does <c>if (error is not null) return error;</c>. A JSON <c>null</c>
/// literal deserializes to <c>(null, null)</c>, so each caller keeps its own "body is required" check.
/// </summary>
public static class RequestJson
{
    public static async Task<(T? Body, IActionResult? Error)> ReadAsync<T>(HttpRequest req, CancellationToken ct = default)
        where T : class
    {
        // HasJsonContentType() accepts exactly what ReadFromJsonAsync will parse (application/json,
        // application/…+json, with an optional charset) — so the guard is aligned with what would throw.
        if (!req.HasJsonContentType())
            return (null, ApiResults.UnsupportedMediaType("Content-Type must be application/json."));
        try
        {
            return (await req.ReadFromJsonAsync<T>(ct), null);
        }
        catch (JsonException)
        {
            return (null, ApiResults.BadRequest("Request body is not valid JSON."));
        }
    }
}
