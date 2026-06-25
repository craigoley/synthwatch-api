using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace SynthWatch.Api.Infrastructure;

/// <summary>Consistent JSON error/result helpers for the HTTP handlers.</summary>
public static class ApiResults
{
    public static IActionResult ValidationError(IReadOnlyDictionary<string, string> errors) =>
        new BadRequestObjectResult(new { error = "validation_error", details = errors });

    public static IActionResult NotFound(string message) =>
        new NotFoundObjectResult(new { error = "not_found", message });

    public static IActionResult BadRequest(string message) =>
        new BadRequestObjectResult(new { error = "bad_request", message });

    public static IActionResult Conflict(string message) =>
        new ConflictObjectResult(new { error = "conflict", message });

    public static IActionResult Ok(object value) => new OkObjectResult(value);

    public static IActionResult Created(string location, object value) =>
        new ObjectResult(value) { StatusCode = StatusCodes.Status201Created };

    public static IActionResult Accepted(object value) =>
        new ObjectResult(value) { StatusCode = StatusCodes.Status202Accepted };

    public static IActionResult NoContent() => new NoContentResult();
}
