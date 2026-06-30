namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Ambient per-request correlation id (the function invocation id), so the STATIC <see cref="ApiResults"/>
/// helpers can stamp <c>instance</c> on a 4xx problem+json body without threading the FunctionContext through
/// every handler call site. Set once per request by RequestLoggingMiddleware (the outermost middleware) before
/// it calls <c>next</c>, so the value flows down the invocation's async context to the handler. Each invocation
/// runs on its own async flow, so concurrent requests never see each other's id; null outside a request.
/// </summary>
public static class RequestCorrelation
{
    private static readonly AsyncLocal<string?> _id = new();

    public static string? Current
    {
        get => _id.Value;
        set => _id.Value = value;
    }
}
