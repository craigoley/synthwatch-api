# Tracing a prod error by its correlation id

When a synthwatch-api response is an error, its `application/problem+json` body (RFC 9457, #124/#125) carries an
**`instance`** field — the correlation id (the Function invocation id). This is how you trace what happened.

```json
{ "type":"about:blank", "title":"Internal Server Error", "status":500,
  "detail":"An unexpected error occurred.", "instance":"7495f28a-4baa-43c0-962a-dc73955ed7fb", ... }
```

```bash
scripts/trace-by-instance.sh 7495f28a-4baa-43c0-962a-dc73955ed7fb
# → the AppRequest (name, status, url) + the AppException (type, message, stack) + error/warning AppTraces
```

## ★ Why you must query the WORKSPACE, not `az monitor app-insights query`

`synthwatch-api-ai` is a **workspace-based** App Insights (`ingestionMode = LogAnalytics`). Its telemetry lands in
the Log Analytics workspace **`synthwatch-api-law`** under the `App*` tables (`AppRequests`, `AppExceptions`,
`AppTraces`, `AppDependencies`) — **not** the classic `requests`/`traces` store.

> ⚠️ `az monitor app-insights query --app <appId> --analytics-query "requests | ..."` returns **EMPTY** for this
> resource. That is **not** "telemetry is dark" — it's the wrong store. Telemetry flows fine
> (~8k requests / ~39k traces per day, verified). Query the workspace instead:

```bash
WSID=$(az monitor log-analytics workspace show -g synthwatch-rg -n synthwatch-api-law --query customerId -o tsv)
az monitor log-analytics query -w "$WSID" --analytics-query "
  AppRequests | where TimeGenerated > ago(1h)
  | where tostring(Properties.InvocationId) == '<instance-id>'
  | project TimeGenerated, Name, ResultCode, Url"
```

The RFC 9457 `instance` == `Properties.InvocationId` in every `App*` table, so one id pivots across the request,
its exception, and its traces.

## ★ This was a real, costly miss

The approve-500 (PR #129/#154) was diagnosed by **role-impersonation** because the classic `az monitor
app-insights query` came back empty and looked "dark." But the exact exception was in the workspace the whole
time — `AppExceptions`: `Npgsql.PostgresException: "42501: permission denied for table reconcile_apply_plan"`,
plus the failing `UPDATE reconcile_apply_plan ...` in `AppTraces`. `scripts/trace-by-instance.sh <id>` would
have surfaced it in seconds. **There was nothing to fix in the telemetry pipeline** — the connection string, the
worker-SDK registration (`Program.cs:16-17`), and sampling (`host.json`, Requests excluded) are all correct.

## Handy KQL (run against `synthwatch-api-law`)

```kusto
// recent 5xx, newest first
AppRequests | where TimeGenerated > ago(24h) | where toint(ResultCode) >= 500
| project TimeGenerated, Name, ResultCode, Url, invocation=tostring(Properties.InvocationId)
| order by TimeGenerated desc

// all exceptions in the last day with their correlation id
AppExceptions | where TimeGenerated > ago(24h)
| project TimeGenerated, ExceptionType, OuterMessage, invocation=tostring(Properties.InvocationId)
```
