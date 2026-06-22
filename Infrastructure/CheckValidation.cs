using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Application-level validation that mirrors the live DB CHECK constraints so we reject bad
/// input with a clean 400 instead of letting the database raise (and leak) a constraint error.
/// </summary>
public static class CheckValidation
{
    public static readonly string[] Kinds = { "http", "browser", "ssl" };
    public static readonly string[] Severities = { "critical", "warning" };
    public static readonly string[] FormFactors = { "desktop", "mobile" };
    private static readonly string[] Methods = { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    // Assertion vocabulary — mirrors the runner contract (migration 0008 / runner/assertions.ts).
    private static readonly HashSet<string> AssertionSources =
        new(StringComparer.Ordinal) { "status", "response_time", "header", "body", "json_path", "size" };
    private static readonly HashSet<string> AssertionComparisons = new(StringComparer.Ordinal)
        { "eq", "ne", "lt", "gt", "gte", "lte", "contains", "not_contains", "matches", "exists", "one_of" };
    // Sources whose assertion requires a target (header name / JSONPath expr).
    private static readonly HashSet<string> SourcesNeedingTarget =
        new(StringComparer.Ordinal) { "header", "json_path" };
    // auth.type values the runner understands.
    private static readonly HashSet<string> AuthTypes =
        new(StringComparer.Ordinal) { "none", "bearer", "basic", "api_key" };
    // Non-secret auth keys allowed inline; any other non-"*_env" key is treated as an inline secret.
    private static readonly HashSet<string> AuthNonSecretKeys =
        new(StringComparer.Ordinal) { "type", "username", "header" };

    /// <summary>Validates a create request and, when valid, produces the entity to insert.</summary>
    public static bool TryBuildNew(CreateCheckRequest req, out Check check, out Dictionary<string, string> errors)
    {
        errors = new Dictionary<string, string>();
        check = new Check();

        if (string.IsNullOrWhiteSpace(req.Name))
            errors["name"] = "Required.";
        if (string.IsNullOrWhiteSpace(req.Kind) || !Kinds.Contains(req.Kind))
            errors["kind"] = $"Must be one of: {string.Join(", ", Kinds)}.";
        if (string.IsNullOrWhiteSpace(req.TargetUrl) || !IsHttpUrl(req.TargetUrl))
            errors["targetUrl"] = "Required; must be an absolute http(s) URL.";

        // browser kind requires a flow_name (DB constraint: browser_needs_flow).
        if (req.Kind == "browser" && string.IsNullOrWhiteSpace(req.FlowName))
            errors["flowName"] = "Required when kind is 'browser'.";

        var method = string.IsNullOrWhiteSpace(req.Method) ? "GET" : req.Method!.ToUpperInvariant();
        if (!Methods.Contains(method))
            errors["method"] = $"Must be one of: {string.Join(", ", Methods)}.";

        var severity = string.IsNullOrWhiteSpace(req.Severity) ? "critical" : req.Severity!;
        if (!Severities.Contains(severity))
            errors["severity"] = $"Must be one of: {string.Join(", ", Severities)}.";

        var formFactor = string.IsNullOrWhiteSpace(req.LighthouseFormFactor) ? "desktop" : req.LighthouseFormFactor!;
        if (!FormFactors.Contains(formFactor))
            errors["lighthouseFormFactor"] = $"Must be one of: {string.Join(", ", FormFactors)}.";

        if (req.ExpectedStatus is < 100 or > 599)
            errors["expectedStatus"] = "Must be a valid HTTP status code (100-599).";
        if (req.IntervalSeconds is <= 0)
            errors["intervalSeconds"] = "Must be greater than 0.";
        if (req.TimeoutMs is <= 0)
            errors["timeoutMs"] = "Must be greater than 0.";
        if (req.FailureThreshold is <= 0)
            errors["failureThreshold"] = "Must be greater than 0.";
        if (req.LighthouseIntervalSeconds is <= 0)
            errors["lighthouseIntervalSeconds"] = "Must be greater than 0 when provided.";
        if (req.CertExpiryWarnDays is <= 0)
            errors["certExpiryWarnDays"] = "Must be greater than 0 when provided.";
        ValidateAssertions(req.Assertions, errors);
        ValidateAuth(req.Auth, errors);

        if (errors.Count > 0)
            return false;

        check.Name = req.Name!.Trim();
        check.Kind = req.Kind!;
        check.TargetUrl = req.TargetUrl!.Trim();
        check.FlowName = string.IsNullOrWhiteSpace(req.FlowName) ? null : req.FlowName!.Trim();
        check.Method = method;
        check.ExpectedStatus = req.ExpectedStatus ?? 200;
        check.BodyMustContain = string.IsNullOrWhiteSpace(req.BodyMustContain) ? null : req.BodyMustContain;
        check.IntervalSeconds = req.IntervalSeconds ?? 300;
        check.TimeoutMs = req.TimeoutMs ?? 30000;
        check.FailureThreshold = req.FailureThreshold ?? 3;
        check.Severity = severity;
        check.Enabled = req.Enabled ?? true;
        check.LighthouseEnabled = req.LighthouseEnabled ?? false;
        check.LighthouseIntervalSeconds = req.LighthouseIntervalSeconds;
        check.LighthouseFormFactor = formFactor;
        check.PerfBudgetLcpMs = req.PerfBudgetLcpMs;
        check.PerfBudgetTransferBytes = req.PerfBudgetTransferBytes;
        check.CertExpiryWarnDays = req.CertExpiryWarnDays ?? 30; // DB default is 30
        check.Assertions = req.Assertions ?? new();
        check.RequestHeaders = req.RequestHeaders;
        check.RequestBody = string.IsNullOrWhiteSpace(req.RequestBody) ? null : req.RequestBody;
        check.Auth = req.Auth;
        return true;
    }

    /// <summary>Applies an in-place patch to an existing entity; returns validation errors, if any.</summary>
    public static Dictionary<string, string> ApplyPatch(UpdateCheckRequest req, Check check)
    {
        var errors = new Dictionary<string, string>();

        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name)) errors["name"] = "Cannot be empty.";
            else check.Name = req.Name.Trim();
        }
        if (req.TargetUrl is not null)
        {
            if (!IsHttpUrl(req.TargetUrl)) errors["targetUrl"] = "Must be an absolute http(s) URL.";
            else check.TargetUrl = req.TargetUrl.Trim();
        }
        if (req.FlowName is not null)
            check.FlowName = string.IsNullOrWhiteSpace(req.FlowName) ? null : req.FlowName.Trim();
        if (req.Method is not null)
        {
            var m = req.Method.ToUpperInvariant();
            if (!Methods.Contains(m)) errors["method"] = $"Must be one of: {string.Join(", ", Methods)}.";
            else check.Method = m;
        }
        if (req.ExpectedStatus is { } es)
        {
            if (es is < 100 or > 599) errors["expectedStatus"] = "Must be a valid HTTP status code (100-599).";
            else check.ExpectedStatus = es;
        }
        if (req.BodyMustContain is not null)
            check.BodyMustContain = string.IsNullOrWhiteSpace(req.BodyMustContain) ? null : req.BodyMustContain;
        if (req.IntervalSeconds is { } iv)
        {
            if (iv <= 0) errors["intervalSeconds"] = "Must be greater than 0.";
            else check.IntervalSeconds = iv;
        }
        if (req.TimeoutMs is { } to)
        {
            if (to <= 0) errors["timeoutMs"] = "Must be greater than 0.";
            else check.TimeoutMs = to;
        }
        if (req.FailureThreshold is { } ft)
        {
            if (ft <= 0) errors["failureThreshold"] = "Must be greater than 0.";
            else check.FailureThreshold = ft;
        }
        if (req.Severity is not null)
        {
            if (!Severities.Contains(req.Severity)) errors["severity"] = $"Must be one of: {string.Join(", ", Severities)}.";
            else check.Severity = req.Severity;
        }
        if (req.Enabled is { } en)
            check.Enabled = en;
        if (req.LighthouseEnabled is { } le)
            check.LighthouseEnabled = le;
        if (req.LighthouseIntervalSeconds is { } lis)
        {
            if (lis <= 0) errors["lighthouseIntervalSeconds"] = "Must be greater than 0.";
            else check.LighthouseIntervalSeconds = lis;
        }
        if (req.LighthouseFormFactor is not null)
        {
            if (!FormFactors.Contains(req.LighthouseFormFactor)) errors["lighthouseFormFactor"] = $"Must be one of: {string.Join(", ", FormFactors)}.";
            else check.LighthouseFormFactor = req.LighthouseFormFactor;
        }
        if (req.PerfBudgetLcpMs is not null)
            check.PerfBudgetLcpMs = req.PerfBudgetLcpMs;
        if (req.PerfBudgetTransferBytes is not null)
            check.PerfBudgetTransferBytes = req.PerfBudgetTransferBytes;
        if (req.CertExpiryWarnDays is { } cwd)
        {
            if (cwd <= 0) errors["certExpiryWarnDays"] = "Must be greater than 0.";
            else check.CertExpiryWarnDays = cwd;
        }
        if (req.Assertions is not null)
        {
            ValidateAssertions(req.Assertions, errors);
            if (!errors.Keys.Any(k => k.StartsWith("assertions", StringComparison.Ordinal)))
                check.Assertions = req.Assertions;
        }
        if (req.RequestHeaders is not null)
            check.RequestHeaders = req.RequestHeaders;
        if (req.RequestBody is not null)
            check.RequestBody = string.IsNullOrWhiteSpace(req.RequestBody) ? null : req.RequestBody;
        if (req.Auth is not null)
        {
            ValidateAuth(req.Auth, errors);
            if (!errors.ContainsKey("auth") && !errors.ContainsKey("auth.type"))
                check.Auth = req.Auth;
        }

        // Enforce the cross-field DB constraint on the resulting state.
        if (check.Kind == "browser" && string.IsNullOrWhiteSpace(check.FlowName))
            errors["flowName"] = "Required when kind is 'browser'.";

        return errors;
    }

    /// <summary>Validates the assertion array (unknown source/comparison, missing required target).</summary>
    private static void ValidateAssertions(List<Assertion>? assertions, Dictionary<string, string> errors)
    {
        if (assertions is null) return;
        for (var i = 0; i < assertions.Count; i++)
        {
            var a = assertions[i];
            if (string.IsNullOrWhiteSpace(a.Source) || !AssertionSources.Contains(a.Source))
                errors[$"assertions[{i}].source"] = $"Must be one of: {string.Join(", ", AssertionSources)}.";
            if (string.IsNullOrWhiteSpace(a.Comparison) || !AssertionComparisons.Contains(a.Comparison))
                errors[$"assertions[{i}].comparison"] = $"Must be one of: {string.Join(", ", AssertionComparisons)}.";
            if (a.Source is not null && SourcesNeedingTarget.Contains(a.Source) && string.IsNullOrWhiteSpace(a.Target))
                errors[$"assertions[{i}].target"] = $"Required when source is '{a.Source}'.";
        }
    }

    /// <summary>
    /// Validates the auth reference object: known type, and NO inline credential values — secrets
    /// must be referenced by env-var name (a key ending in <c>_env</c>). This keeps plaintext
    /// tokens/passwords out of the DB end-to-end.
    /// </summary>
    private static void ValidateAuth(Dictionary<string, string>? auth, Dictionary<string, string> errors)
    {
        if (auth is null) return;
        if (!auth.TryGetValue("type", out var type) || !AuthTypes.Contains(type))
        {
            errors["auth.type"] = $"Must be one of: {string.Join(", ", AuthTypes)}.";
        }
        foreach (var key in auth.Keys)
        {
            if (!AuthNonSecretKeys.Contains(key) && !key.EndsWith("_env", StringComparison.Ordinal))
            {
                errors["auth"] = "Inline credential values are not allowed; reference a secret by " +
                                 "env-var name (a key ending in '_env', e.g. token_env).";
                break;
            }
        }
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
