using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace SynthWatch.Api.Functions;

/// <summary>
/// Answers CORS preflight (OPTIONS) for every route. The actual CORS headers are attached by
/// <c>CorsMiddleware</c> after this returns; here we just short-circuit with 204 No Content so
/// the browser's preflight succeeds before it sends the real request.
/// </summary>
public class CorsPreflightFunction
{
    [Function("CorsPreflight")]
    public IActionResult Handle(
        [HttpTrigger(AuthorizationLevel.Anonymous, "options", Route = "{*route}")] HttpRequest req)
        => new NoContentResult();
}
