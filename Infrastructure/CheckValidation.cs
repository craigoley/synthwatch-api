using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Application-level validation that mirrors the live DB CHECK constraints so we reject bad
/// input with a clean 400 instead of letting the database raise (and leak) a constraint error.
/// </summary>
public static class CheckValidation
{
    public static readonly string[] Kinds = { "http", "browser" };
    public static readonly string[] Severities = { "critical", "warning" };
    public static readonly string[] FormFactors = { "desktop", "mobile" };
    private static readonly string[] Methods = { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

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

        // Enforce the cross-field DB constraint on the resulting state.
        if (check.Kind == "browser" && string.IsNullOrWhiteSpace(check.FlowName))
            errors["flowName"] = "Required when kind is 'browser'.";

        return errors;
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
