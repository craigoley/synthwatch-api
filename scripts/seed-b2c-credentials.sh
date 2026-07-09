#!/usr/bin/env bash
# One-time seed: set the b2c-login-test monitor's login credentials (username/password) via the
# model-B credential WRITE endpoint (PUT /checks/{id}/credentials — Step B, api #202). The endpoint
# takes PLAINTEXT and ENCRYPTS it server-side with CRED_ENC_KEY, storing v1: ciphertext. So once the
# runner switches to decrypt-from-DB (Step A), b2c has real credentials to decrypt and can go green —
# WITHOUT waiting for the Step-C dashboard credential editor.
#
# IDEMPOTENT + SAFE: the PUT REPLACES each column, so re-running just re-sets the SAME values. The
# credential VALUES are read from ~/.synthwatch.env (B2C_TEST_USER / B2C_TEST_PASS) and are NEVER
# printed, logged, or echoed. The only credential-shaped output is the write-only MASK ("set" per slot).
#
# AUTH: needs an editor/admin session bearer in SW_BEARER. Get one by logging into the dashboard, or
# mint one directly per the CLAUDE.md recipe (insert a `sessions` row: token_hash = sha256-hex of an
# opaque token, email = an ADMIN_EMAILS entry, set expires_at; delete it afterward).
set -euo pipefail

readonly BASE="${SW_API_BASE:-https://synthwatch-api.azurewebsites.net/api}"
readonly CHECK_ID="${SW_B2C_CHECK_ID:-353}"          # wegmans-b2c-login-test (browser, monitors/wegmans/b2c-login-test.spec.ts)
readonly ENV_FILE="${SW_ENV_FILE:-${HOME}/.synthwatch.env}"

command -v jq   >/dev/null || { echo "ERROR: jq is required"   >&2; exit 1; }
command -v curl >/dev/null || { echo "ERROR: curl is required" >&2; exit 1; }

[[ -f "$ENV_FILE" ]] || { echo "ERROR: $ENV_FILE not found" >&2; exit 1; }
# shellcheck disable=SC1090
source "$ENV_FILE"

# Values (from ~/.synthwatch.env) + auth. `:?` fails loud on absence — never a silent no-op.
: "${B2C_TEST_USER:?B2C_TEST_USER must be set in ~/.synthwatch.env}"
: "${B2C_TEST_PASS:?B2C_TEST_PASS must be set in ~/.synthwatch.env}"
: "${SW_BEARER:?SW_BEARER must hold an editor/admin session token (dashboard login, or mint per CLAUDE.md)}"

# ── Preflight: prove Step B (#202) is actually DEPLOYED, not merely merged. An UNAUTH PUT to the route
#    returns 401 when the endpoint exists + is gated, 404 when it is not deployed yet. Fail LOUD on 404
#    (a seed that silently no-ops is worse than one that stops). ──
pre="$(curl -s -o /dev/null -w '%{http_code}' -X PUT "$BASE/checks/$CHECK_ID/credentials" \
        -H 'Content-Type: application/json' -d '{}')"
if [[ "$pre" == "404" ]]; then
  echo "ERROR: PUT $BASE/checks/$CHECK_ID/credentials -> 404. Step B (#202) is not deployed yet; aborting." >&2
  exit 1
fi

# ── PUT the credentials. jq --arg binds the values as JSON args (never string-interpolated), so any
#    character in the password is safe. Values in are PLAINTEXT; the endpoint encrypts before store. ──
body="$(jq -n --arg u "$B2C_TEST_USER" --arg p "$B2C_TEST_PASS" \
        '{loginCredentials: {username: $u, password: $p}}')"

resp="$(curl -s -w $'\n%{http_code}' -X PUT "$BASE/checks/$CHECK_ID/credentials" \
        -H "Authorization: Bearer $SW_BEARER" \
        -H 'Content-Type: application/json' \
        --data-binary "$body")"
unset body   # drop the plaintext body from the environment immediately

code="$(printf '%s' "$resp" | tail -n1)"
payload="$(printf '%s' "$resp" | sed '$d')"   # masked/error body — never contains a credential value

if [[ "$code" != "200" ]]; then
  echo "ERROR: PUT returned HTTP $code: $payload" >&2
  exit 1
fi

# ── Verify #1: the PUT's write-only echo. Both slots must read the mask "set". ──
put_u="$(printf '%s' "$payload" | jq -r '.loginCredentials.username // empty')"
put_p="$(printf '%s' "$payload" | jq -r '.loginCredentials.password // empty')"
if [[ "$put_u" != "set" || "$put_p" != "set" ]]; then
  echo "ERROR: unexpected PUT masked echo: $payload" >&2
  exit 1
fi

# ── Verify #2 (independent): GET the check detail with the same session and confirm the PERSISTED,
#    masked loginCredentials — proves it round-trips write-only ("set", never the value/ciphertext). ──
detail="$(curl -s -X GET "$BASE/checks/$CHECK_ID" -H "Authorization: Bearer $SW_BEARER")"
get_u="$(printf '%s' "$detail" | jq -r '.loginCredentials.username // empty')"
get_p="$(printf '%s' "$detail" | jq -r '.loginCredentials.password // empty')"
if [[ "$get_u" != "set" || "$get_p" != "set" ]]; then
  echo "ERROR: GET readback did not confirm both slots set: loginCredentials=$(printf '%s' "$detail" | jq -c '.loginCredentials')" >&2
  exit 1
fi

echo "OK  b2c (check $CHECK_ID): loginCredentials {username: set, password: set} — stored encrypted (v1:), verified via PUT echo + GET readback."
