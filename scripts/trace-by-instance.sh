#!/usr/bin/env bash
# trace-by-instance.sh <rfc9457-instance-id> [lookback]
#
# Trace a synthwatch-api error by the `instance` correlation id from its RFC 9457 problem+json body
# (#124/#125): the request + its exception + its error/warning traces.
#
# ★ WHY THIS SCRIPT EXISTS: synthwatch-api-ai is a WORKSPACE-BASED App Insights (ingestionMode=LogAnalytics),
# so `az monitor app-insights query --app <appId>` against the classic store returns EMPTY — the telemetry
# lives in the Log Analytics workspace `synthwatch-api-law` under the App* tables (AppRequests/AppExceptions/
# AppTraces), keyed by Properties.InvocationId == the RFC 9457 `instance`. Querying the classic endpoint is
# what made the boundary look "dark" and forced the approve-500 to be diagnosed by role-impersonation — yet the
# exact exception ("permission denied for table reconcile_apply_plan") was in the workspace the whole time.
#
# Usage:  scripts/trace-by-instance.sh 11594ae0-036b-4b17-a609-29712defac89 [7d]
set -euo pipefail

INST="${1:?usage: trace-by-instance.sh <instance-id> [lookback, default 7d]}"
SINCE="${2:-7d}"
RG="${SYNTHWATCH_RG:-synthwatch-rg}"
WS="${SYNTHWATCH_LAW:-synthwatch-api-law}"

WSID="$(az monitor log-analytics workspace show -g "$RG" -n "$WS" --query customerId -o tsv)"
echo "workspace: $WS ($WSID) · instance: $INST · lookback: $SINCE"

q() { az monitor log-analytics query -w "$WSID" --analytics-query "$1" -o table; }
FILTER="where TimeGenerated > ago(${SINCE}) | where tostring(Properties.InvocationId) == '${INST}'"

echo; echo "── request ──────────────────────────────────────────────"
q "AppRequests | ${FILTER} | project TimeGenerated, Name, ResultCode, DurationMs, Url, OperationId"

echo; echo "── exception (the stack/cause) ──────────────────────────"
q "AppExceptions | ${FILTER} | project TimeGenerated, ExceptionType, OuterMessage, ProblemId"

echo; echo "── error/warning traces ─────────────────────────────────"
q "AppTraces | ${FILTER} and SeverityLevel >= 2 | project TimeGenerated, SeverityLevel, Message"
