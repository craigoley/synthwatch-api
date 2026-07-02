using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Consistent error/result helpers for the HTTP handlers. ★ Every error is RFC 9457 <c>application/problem+json</c>
/// (the SAME hybrid <see cref="ProblemResults"/> the 500 + denial paths use, #124): type/title/status/detail/
/// instance + the legacy <c>error</c>/<c>message</c> extension members the dashboard reads — so the whole error
/// surface is standard + traceable (<c>instance</c> = the correlation id) with ZERO consumer change. The typed
/// <c>*ObjectResult</c> subclasses are kept so the status code (and existing IsType assertions) are unchanged —
/// only the BODY shape is richer.
/// </summary>
public static class ApiResults
{
    public static IActionResult ValidationError(IReadOnlyDictionary<string, string> errors) =>
        new BadRequestObjectResult(ProblemResults.ValidationBody(RequestCorrelation.Current, errors)) { ContentTypes = { ProblemResults.ContentType } };

    public static IActionResult NotFound(string message) =>
        new NotFoundObjectResult(ProblemResults.Body(StatusCodes.Status404NotFound, "Not Found", message, RequestCorrelation.Current, "not_found")) { ContentTypes = { ProblemResults.ContentType } };

    public static IActionResult BadRequest(string message) =>
        new BadRequestObjectResult(ProblemResults.Body(StatusCodes.Status400BadRequest, "Bad Request", message, RequestCorrelation.Current, "bad_request")) { ContentTypes = { ProblemResults.ContentType } };

    /// <summary>415 — the request body's Content-Type isn't JSON. Explicit + clean: <c>ReadFromJsonAsync</c>
    /// throws <c>InvalidOperationException</c> (NOT the <c>JsonException</c> handlers catch) on a wrong content
    /// type, which otherwise fell through to a shielded 500.</summary>
    public static IActionResult UnsupportedMediaType(string message) =>
        new ObjectResult(ProblemResults.Body(StatusCodes.Status415UnsupportedMediaType, "Unsupported Media Type", message, RequestCorrelation.Current, "unsupported_media_type"))
            { StatusCode = StatusCodes.Status415UnsupportedMediaType, ContentTypes = { ProblemResults.ContentType } };

    public static IActionResult Conflict(string message) =>
        new ConflictObjectResult(ProblemResults.Body(StatusCodes.Status409Conflict, "Conflict", message, RequestCorrelation.Current, "conflict")) { ContentTypes = { ProblemResults.ContentType } };

    public static IActionResult Unauthorized(string message) =>
        new UnauthorizedObjectResult(ProblemResults.Body(StatusCodes.Status401Unauthorized, "Unauthorized", message, RequestCorrelation.Current, "unauthorized")) { ContentTypes = { ProblemResults.ContentType } };

    /// <summary>403 — a valid session lacking the required role. Same problem+json body the
    /// AuthorizationMiddleware emits, so the dashboard's 401-vs-403 interceptor branches identically
    /// whether the deny came from the gate or a handler's own admin check.</summary>
    public static IActionResult Forbidden(string message) =>
        new ObjectResult(ProblemResults.Body(StatusCodes.Status403Forbidden, "Forbidden", message, RequestCorrelation.Current, "forbidden"))
            { StatusCode = StatusCodes.Status403Forbidden, ContentTypes = { ProblemResults.ContentType } };

    public static IActionResult Ok(object value) => new OkObjectResult(value);

    public static IActionResult Created(string location, object value) =>
        new ObjectResult(value) { StatusCode = StatusCodes.Status201Created };

    public static IActionResult Accepted(object value) =>
        new ObjectResult(value) { StatusCode = StatusCodes.Status202Accepted };

    public static IActionResult NoContent() => new NoContentResult();

    /// <summary>503 — a transient upstream (e.g. a throttled/blob error) couldn't be served; retrying may help.</summary>
    public static IActionResult ServiceUnavailable(string message) =>
        new ObjectResult(ProblemResults.Body(StatusCodes.Status503ServiceUnavailable, "Service Unavailable", message, RequestCorrelation.Current, "unavailable"))
            { StatusCode = StatusCodes.Status503ServiceUnavailable, ContentTypes = { ProblemResults.ContentType } };
}
