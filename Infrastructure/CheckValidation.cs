using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;

namespace SynthWatch.Api.Infrastructure;

/// <summary>
/// Application-level validation that mirrors the live DB CHECK constraints so we reject bad
/// input with a clean 400 instead of letting the database raise (and leak) a constraint error.
/// </summary>
public static class CheckValidation
{
    public static readonly string[] Kinds = { "http", "browser", "ssl", "dns", "tcp", "ping", "multistep" };
    private static readonly HashSet<string> NetworkKinds = new(StringComparer.Ordinal) { "dns", "tcp", "ping" };
    // DNS record types the runner supports (runner/netChecks.ts). recordType defaults to 'A'.
    private static readonly HashSet<string> DnsRecordTypes =
        new(StringComparer.Ordinal) { "A", "AAAA", "CNAME", "MX", "TXT", "NS" };
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
        var kindIsNetwork = !string.IsNullOrWhiteSpace(req.Kind) && NetworkKinds.Contains(req.Kind!);
        if (string.IsNullOrWhiteSpace(req.TargetUrl))
            errors["targetUrl"] = "Required.";
        else if (kindIsNetwork ? !IsNetTarget(req.TargetUrl) : !IsHttpUrl(req.TargetUrl))
            errors["targetUrl"] = kindIsNetwork
                ? "Must be a host or host:port."
                : "Must be an absolute http(s) URL.";

        // browser kind requires a flow_name (DB constraint: browser_needs_flow). On spec activation the
        // dashboard sets flow_name = flowNameFor(spec_path) (synthetic) so this is satisfied even though
        // spec_path — not flow_name — drives the runner's Option C fetch path.
        if (req.Kind == "browser" && string.IsNullOrWhiteSpace(req.FlowName))
            errors["flowName"] = "Required when kind is 'browser'.";

        // spec_path shape mirrors the DB constraint checks_spec_path_shape (migration 0033): a manifest
        // Playwright spec under monitors/, ending .spec.ts, with NO '..' traversal. The host/repo/branch
        // are hardcoded runner-side; only this path varies — validate it here so a bad path is a clean
        // 400, not a constraint-violation 500 on insert (and not a redirectable fetch).
        if (!string.IsNullOrWhiteSpace(req.SpecPath))
        {
            var sp = req.SpecPath!.Trim();
            if (!SpecPathRe.IsMatch(sp) || sp.Contains("..", StringComparison.Ordinal))
                errors["specPath"] = "Must be a 'monitors/….spec.ts' path with no '..' traversal.";
        }

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
        ValidateNetConfig(req.Kind, req.TargetUrl, req.NetConfig, errors);
        ValidateSteps(req.Kind, req.Steps, errors);

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
        check.FailureThreshold = req.FailureThreshold ?? 1; // runner canonical default (db/schema.sql DEFAULT 1, migration 0045: was 3)
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
        check.NetConfig = req.NetConfig;
        check.Steps = req.Steps;
        // Monitors-as-code activation: bind the manifest id + spec path (validated above). Trimmed to
        // null when blank so a hand-made check stays on the baked-flow path.
        check.SourceKey = string.IsNullOrWhiteSpace(req.SourceKey) ? null : req.SourceKey!.Trim();
        check.SpecPath = string.IsNullOrWhiteSpace(req.SpecPath) ? null : req.SpecPath!.Trim();
        return true;
    }

    // Mirrors checks_spec_path_shape's regex (the '..' traversal guard is applied alongside it).
    private static readonly System.Text.RegularExpressions.Regex SpecPathRe =
        new(@"^monitors/.+\.spec\.ts$", System.Text.RegularExpressions.RegexOptions.Compiled);

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
            var isNet = NetworkKinds.Contains(check.Kind);
            if (isNet ? !IsNetTarget(req.TargetUrl) : !IsHttpUrl(req.TargetUrl))
                errors["targetUrl"] = isNet ? "Must be a host or host:port." : "Must be an absolute http(s) URL.";
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
        // Reversible archive (0071): true → stamp archived_at=now(); false → clear it (re-activate). DISTINCT
        // from Enabled/pause — clearing archive leaves Enabled untouched, so the prior state resumes.
        if (req.Archived is { } ar)
            check.ArchivedAt = ar ? DateTimeOffset.UtcNow : null;
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
        if (req.NetConfig is not null)
        {
            // Validate against the resulting kind + (possibly patched) target_url.
            ValidateNetConfig(check.Kind, check.TargetUrl, req.NetConfig, errors);
            if (!errors.Keys.Any(k => k.StartsWith("netConfig", StringComparison.Ordinal)))
                check.NetConfig = req.NetConfig;
        }
        if (req.Steps is not null)
        {
            ValidateSteps(check.Kind, req.Steps, errors);
            if (!errors.Keys.Any(k => k.StartsWith("steps", StringComparison.Ordinal)))
                check.Steps = req.Steps;
        }

        // Enforce the cross-field DB constraint on the resulting state.
        if (check.Kind == "browser" && string.IsNullOrWhiteSpace(check.FlowName))
            errors["flowName"] = "Required when kind is 'browser'.";

        return errors;
    }

    /// <summary>
    /// Validates an assertion array (unknown source/comparison, missing required target).
    /// <paramref name="prefix"/> nests the error keys (e.g. "steps[2]." for a multistep step).
    /// </summary>
    private static void ValidateAssertions(List<Assertion>? assertions, Dictionary<string, string> errors, string prefix = "")
    {
        if (assertions is null) return;
        for (var i = 0; i < assertions.Count; i++)
        {
            var a = assertions[i];
            if (string.IsNullOrWhiteSpace(a.Source) || !AssertionSources.Contains(a.Source))
                errors[$"{prefix}assertions[{i}].source"] = $"Must be one of: {string.Join(", ", AssertionSources)}.";
            if (string.IsNullOrWhiteSpace(a.Comparison) || !AssertionComparisons.Contains(a.Comparison))
                errors[$"{prefix}assertions[{i}].comparison"] = $"Must be one of: {string.Join(", ", AssertionComparisons)}.";
            if (a.Source is not null && SourcesNeedingTarget.Contains(a.Source) && string.IsNullOrWhiteSpace(a.Target))
                errors[$"{prefix}assertions[{i}].target"] = $"Required when source is '{a.Source}'.";
        }
    }

    /// <summary>
    /// Validates an auth reference object: known type, and NO inline credential values — secrets
    /// must be referenced by env-var name (a key ending in <c>_env</c>). This keeps plaintext
    /// tokens/passwords out of the DB end-to-end. <paramref name="prefix"/> nests the error keys.
    /// </summary>
    private static void ValidateAuth(Dictionary<string, string>? auth, Dictionary<string, string> errors, string prefix = "")
    {
        if (auth is null) return;
        if (!auth.TryGetValue("type", out var type) || !AuthTypes.Contains(type))
        {
            errors[$"{prefix}auth.type"] = $"Must be one of: {string.Join(", ", AuthTypes)}.";
        }
        if (auth.Keys.Any(key => !AuthNonSecretKeys.Contains(key) && !key.EndsWith("_env", StringComparison.Ordinal)))
        {
            errors[$"{prefix}auth"] = "Inline credential values are not allowed; reference a secret by " +
                             "env-var name (a key ending in '_env', e.g. token_env).";
        }
    }

    private static readonly System.Text.RegularExpressions.Regex TemplateRef =
        new(@"\{\{\s*([^}\s]+)\s*\}\}", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex VarName =
        new("^[A-Za-z_][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Validates the multistep step chain (runner contract: migration 0013 / multistep.ts). Each step
    /// reuses the single-check request/assertion/auth validation; extract rules need a sane var +
    /// non-empty jsonPath; and {{var}} references are checked for integrity (every template var must
    /// have been extracted by an EARLIER step — choice (a), parseable from the runner's exact regex).
    /// </summary>
    private static void ValidateSteps(string? kind, List<ChainStep>? steps, Dictionary<string, string> errors)
    {
        var isMultistep = kind == "multistep";

        if (!isMultistep)
        {
            if (steps is not null) errors["steps"] = "Only multistep checks may carry steps.";
            return;
        }
        if (steps is null || steps.Count == 0)
        {
            errors["steps"] = "A multistep check requires a non-empty steps array.";
            return;
        }

        var extractedSoFar = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var p = $"steps[{i}].";

            if (string.IsNullOrWhiteSpace(s.Name)) errors[$"{p}name"] = "Required.";
            if (string.IsNullOrWhiteSpace(s.Url)) errors[$"{p}url"] = "Required.";
            if (!string.IsNullOrWhiteSpace(s.Method) && !Methods.Contains(s.Method!.ToUpperInvariant()))
                errors[$"{p}method"] = $"Must be one of: {string.Join(", ", Methods)}.";

            // Reuse the single-check assertion + secret-ref auth validation, nested per step.
            ValidateAssertions(s.Assertions, errors, p);
            ValidateAuth(s.Auth, errors, p);

            // {{var}} reference integrity: every template var must come from an earlier step's extract.
            // Report every dangling var (not just the last one) so the field detail stays complete.
            var missing = TemplateVars(s.Url).Concat(TemplateVars(s.Body))
                .Concat((s.Headers?.Values ?? Enumerable.Empty<string>()).SelectMany(TemplateVars))
                .Where(v => !extractedSoFar.Contains(v))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (missing.Count > 0)
                errors[$"{p}template"] =
                    $"References {string.Join(", ", missing.Select(v => $"{{{{{v}}}}}"))} which no earlier step extracts.";

            // Validate + register this step's extract vars (available to LATER steps).
            if (s.Extract is not null)
            {
                for (var j = 0; j < s.Extract.Count; j++)
                {
                    var e = s.Extract[j];
                    if (string.IsNullOrWhiteSpace(e.Var) || !VarName.IsMatch(e.Var))
                        errors[$"{p}extract[{j}].var"] = "Must be a valid identifier (letters, digits, underscore).";
                    else
                        extractedSoFar.Add(e.Var);
                    if (string.IsNullOrWhiteSpace(e.JsonPath))
                        errors[$"{p}extract[{j}].jsonPath"] = "Required.";
                }
            }
        }
    }

    private static IEnumerable<string> TemplateVars(string? input) =>
        string.IsNullOrEmpty(input)
            ? Enumerable.Empty<string>()
            : TemplateRef.Matches(input).Select(m => m.Groups[1].Value);

    /// <summary>
    /// Validates net_config against the runner's per-kind contract (migration 0011 / netChecks.ts):
    /// dns recordType (optional, defaults A) must be a supported type; tcp needs a port (from
    /// net_config.port OR host:port in target_url); ping port is optional (default 443); any port
    /// must be 1-65535. Non-network kinds must NOT carry net_config.
    /// </summary>
    private static void ValidateNetConfig(string? kind, string? targetUrl, NetConfig? net, Dictionary<string, string> errors)
    {
        var isNetwork = kind is not null && NetworkKinds.Contains(kind);

        if (net is not null && !isNetwork)
        {
            errors["netConfig"] = "Only dns/tcp/ping checks may carry netConfig.";
            return;
        }
        if (!isNetwork)
            return;

        if (net?.Port is { } port && (port < 1 || port > 65535))
            errors["netConfig.port"] = "Must be between 1 and 65535.";

        switch (kind)
        {
            case "dns":
                if (!string.IsNullOrWhiteSpace(net?.RecordType))
                {
                    var recordType = net!.RecordType!.ToUpperInvariant();
                    if (!DnsRecordTypes.Contains(recordType))
                        errors["netConfig.recordType"] = $"Must be one of: {string.Join(", ", DnsRecordTypes)}.";
                    else
                        // Normalize on store: the runner's DNS rrtype is upper-case-only, so persist
                        // the canonical form rather than whatever casing the caller sent.
                        net.RecordType = recordType;
                }
                break;
            case "tcp":
                // Port must be resolvable from net_config.port OR a host:port in target_url.
                if (net?.Port is null && PortFromTarget(targetUrl) is null)
                    errors["netConfig.port"] = "Required for tcp (set netConfig.port or include host:port in targetUrl).";
                break;
            case "ping":
                // Port optional (runner defaults to 443); range already checked above.
                break;
        }
    }

    /// <summary>Extracts an explicit port from a target like "host:port" / "scheme://host:port".</summary>
    private static int? PortFromTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return null;
        var s = target.Contains("://", StringComparison.Ordinal) ? target : $"tcp://{target}";
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Port > 0 ? u.Port : null;
    }

    /// <summary>A network target is a host or host:port (no http(s) scheme required).</summary>
    private static bool IsNetTarget(string value) =>
        Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : $"tcp://{value}",
            UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host);

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// B10 ENABLE GATE — the API mirror of the runner's GitOps gate. Returns true when a check is
    /// <c>sensitive</c> but declares NO <c>redact_patterns</c>: such a check MUST NOT be enabled (get a
    /// check_locations cursor), because its trace could leak session tokens / cart contents / account PII
    /// unredacted. ★ Keep EXACTLY in sync with runner reconcile.ts validateManifest (synthwatch #137):
    ///   e.sensitive === true &amp;&amp; (!Array.isArray(e.redact_patterns) || e.redact_patterns.length === 0)
    /// — same input → same verdict, so a check valid via the API is valid via reconcile and vice versa.
    /// </summary>
    public static bool SensitiveNeedsRedaction(bool sensitive, IReadOnlyList<string>? redactPatterns) =>
        sensitive && (redactPatterns is null || redactPatterns.Count == 0);
}
