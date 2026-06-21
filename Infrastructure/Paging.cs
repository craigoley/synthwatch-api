using Microsoft.AspNetCore.Http;

namespace SynthWatch.Api.Infrastructure;

/// <summary>Parses <c>?page=</c> / <c>?pageSize=</c> query params with safe bounds.</summary>
public static class Paging
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static (int Page, int PageSize) Parse(HttpRequest req)
    {
        var page = 1;
        if (int.TryParse(req.Query["page"], out var p) && p > 0)
            page = p;

        var pageSize = DefaultPageSize;
        if (int.TryParse(req.Query["pageSize"], out var ps) && ps > 0)
            pageSize = Math.Min(ps, MaxPageSize);

        return (page, pageSize);
    }
}
