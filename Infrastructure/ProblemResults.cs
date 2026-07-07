namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// RFC 9457 "Problem Details for HTTP APIs" error body — served as <c>application/problem+json</c>. ★ Backward-
/// compatible: it keeps the legacy <c>error</c> + <c>message</c> fields (RFC 9457 extension members) that the
/// dashboard's error path already reads (api-client reads <c>body.message</c>/<c>body.error</c>), so adopting the
/// standard does NOT break error display. <c>instance</c> carries the invocation/correlation id so a user hitting
/// an error has something to quote in a support request (the gap: 500s previously had the id only in server logs).
/// </summary>
public static class ProblemResults
{
    public const string ContentType = "application/problem+json";

    /// <summary>Build the RFC 9457 problem+json response body (type = "about:blank"; the status IS the type,
    /// so <paramref name="title"/> carries the human label). Also emits the legacy error/message members the
    /// dashboard's error path reads, and the correlation id as <c>instance</c>.</summary>
    /// <param name="legacyError">the prior machine code (e.g. "internal_error", "unauthorized") — kept for the dashboard.</param>
    /// <param name="instance">the invocation/correlation id (null when none is available).</param>
    public static object Body(int status, string title, string detail, string? instance, string legacyError) => new
    {
        // RFC 9457 members. `type` = the default "about:blank" (the status code IS the type), so `title` carries
        // the human label — minimal + consistent, no per-error type registry to maintain.
        type = "about:blank",
        title,
        status,
        detail,
        instance,
        // Legacy extension members — what the dashboard's error path reads. Drop these in a future coordinated
        // dashboard change once it migrates to `detail`/`instance`.
        error = legacyError,
        message = detail,
    };

    /// <summary>
    /// The 400 validation-error body — RFC 9457 + the legacy <c>{error:"validation_error", details}</c> the
    /// dashboard reads (<c>body.details</c> is the field→error map). <c>details</c> is an RFC 9457 extension
    /// member; <c>error</c>/<c>message</c> are kept too, so the dashboard's error path is unaffected.
    /// </summary>
    public static object ValidationBody(string? instance, object details) => new
    {
        type = "about:blank",
        title = "Bad Request",
        status = 400,
        detail = "One or more fields are invalid.",
        instance,
        error = "validation_error",
        message = "One or more fields are invalid.",
        details, // the field→error map — legacy + RFC 9457 extension member (dashboard reads body.details)
    };
}
