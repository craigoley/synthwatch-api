using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// ★ THE TRIPWIRE (the docs-layer liveness check). docs/auth-gates.md is the most-trusted doc in the repo —
/// the endpoint gate table an operator and a security reviewer read. Nothing structural stopped it drifting
/// (it was already missing ~9 routes when this test was added — the exact "best doc becomes the most dangerous
/// doc" failure this guards against). This test reflects EVERY <c>[Function]</c> HTTP route out of the API
/// assembly and fails if one is not covered by the auth-gates table, or if the table lists a route that no
/// longer exists — so adding an endpoint and forgetting the table turns the build red, not the doc into a lie.
/// </summary>
public class AuthGatesDocParityTests
{
    /// <summary>Walk up from the test bin dir to the repo root (the dir holding docs/auth-gates.md) — the same
    /// source-tree discovery FlakeBudgetNoMuteTests uses; no hard-coded path.</summary>
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "docs", "auth-gates.md")))
            d = d.Parent;
        Assert.True(d is not null, "Could not locate repo root (docs/auth-gates.md) walking up from the test bin dir.");
        return d!.FullName;
    }

    /// <summary>Route normalizer: strip a leading /api and slashes, collapse every {param:constraint} to {} so
    /// the doc's {id} matches the code's {id:long}/{checkId:long}, lowercase. Glob '*' is preserved.</summary>
    private static string Normalize(string route)
    {
        route = route.Trim().Trim('/');
        if (route.StartsWith("api/", StringComparison.OrdinalIgnoreCase)) route = route[4..];
        route = Regex.Replace(route, @"\{[^}]+\}", "{}");
        return route.ToLowerInvariant();
    }

    /// <summary>Every HTTP route declared in the API assembly: reflect methods carrying [Function], find the
    /// parameter carrying [HttpTrigger], read its Route. By attribute NAME so no worker-SDK compile ref is needed.</summary>
    private static IReadOnlyList<string> ReflectedRoutes()
    {
        var asm = typeof(SynthWatch.Api.Functions.ReportsFunctions).Assembly;
        var routes = new List<string>();
        foreach (var type in asm.GetExportedTypes())
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!m.GetCustomAttributes().Any(a => a.GetType().Name == "FunctionAttribute")) continue;
                foreach (var p in m.GetParameters())
                {
                    var trigger = p.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name == "HttpTriggerAttribute");
                    if (trigger is null) continue;
                    if (trigger.GetType().GetProperty("Route")?.GetValue(trigger) is string route && route.Length > 0)
                        routes.Add(Normalize(route));
                }
            }
        }
        return routes.Distinct().OrderBy(r => r).ToList();
    }

    /// <summary>The routes documented in auth-gates.md's "Endpoint table": every backtick-quoted /path token in
    /// that section (route-shaped only — must start with '/'). A trailing '*' is a prefix glob (/reports/* covers
    /// reports/trust). Excludes prose and non-route code-spans (SessionReadGate, Cache-Control, …).</summary>
    private static IReadOnlyList<string> DocumentedTokens(string authGatesMd)
    {
        var start = authGatesMd.IndexOf("## Endpoint table", StringComparison.Ordinal);
        var end = authGatesMd.IndexOf("## Verifying", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "auth-gates.md must keep an '## Endpoint table' section followed by '## Verifying'.");
        var section = authGatesMd[start..end];
        return Regex.Matches(section, "`(/[A-Za-z0-9{}:_*/-]+)`")
            .Select(m => Normalize(m.Groups[1].Value))
            .Distinct()
            .ToList();
    }

    private static bool Covered(string route, IReadOnlyList<string> docTokens) =>
        docTokens.Any(d => d.EndsWith("*", StringComparison.Ordinal)
            ? route.StartsWith(d[..^1], StringComparison.Ordinal)
            : d == route);

    [Fact]
    public void Every_function_route_is_documented_in_auth_gates_md()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "auth-gates.md"));
        var docTokens = DocumentedTokens(doc);
        var routes = ReflectedRoutes();

        var undocumented = routes.Where(r => !Covered(r, docTokens)).ToList();
        Assert.True(undocumented.Count == 0,
            "★ Routes exist in code but are NOT in docs/auth-gates.md's Endpoint table — add each with its gate "
            + "(the doc is a security surface; do not skip):\n  " + string.Join("\n  ", undocumented));
    }

    [Fact]
    public void Auth_gates_md_lists_no_route_that_no_longer_exists()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "auth-gates.md"));
        var routes = ReflectedRoutes().ToHashSet();
        // Only literal (non-glob) documented tokens are checked in reverse; a glob (/reports/*) is a family, not a route.
        var stale = DocumentedTokens(doc)
            .Where(d => !d.EndsWith("*", StringComparison.Ordinal))
            .Where(d => !routes.Contains(d))
            .ToList();
        Assert.True(stale.Count == 0,
            "★ docs/auth-gates.md lists route(s) that no longer exist in code — a deleted endpoint left in the "
            + "security table is a lie; remove them:\n  " + string.Join("\n  ", stale));
    }
}
