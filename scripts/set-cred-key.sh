#!/usr/bin/env bash
# Single-source the CRED_ENC_KEY onto the API Function App from ~/.synthwatch.env — the SAME value the
# runner's deploy.sh uses (it reads the same file), so the runner can decrypt api-written credential
# ciphertext. Model B. Run this once after setting CRED_ENC_KEY in ~/.synthwatch.env (and again if you
# rotate it). The runner deploy.sh drift-check VERIFIES the two match.
#
# WHY an incremental `appsettings set` (not a full `az deployment group create` of the api bicep): the api
# bicep would apply ALL app settings, and params prod sets non-default (adminEmails, azureOpenAiEndpoint)
# default to '' — a partial-param bicep apply would WIPE them. `appsettings set --settings` touches ONLY
# CRED_ENC_KEY, leaving everything else intact. The code-deploy (deploy.yml) never touches app settings, so
# this value persists across code deploys. (The bicep `credEncKey` param remains the DECLARED source for a
# full bicep apply — pass credEncKey="$CRED_ENC_KEY" there too so a bicep re-apply re-asserts, not wipes.)
#
# The key VALUE is NEVER printed or logged — only its non-secret fingerprint (matches CredCrypto.Fingerprint
# + the runner deploy.sh drift-check), so you can eyeball that the api now holds the intended key.
set -euo pipefail

readonly RG="${SYNTHWATCH_RG:-synthwatch-rg}"
readonly APP="${SYNTHWATCH_API_APP:-synthwatch-api}"
readonly ENV_FILE="${HOME}/.synthwatch.env"

[[ -f "${ENV_FILE}" ]] || { echo "ERROR: ${ENV_FILE} not found" >&2; exit 1; }
# shellcheck disable=SC1090
source "${ENV_FILE}"

: "${CRED_ENC_KEY:?CRED_ENC_KEY must be set in ~/.synthwatch.env (base64 of 32 random bytes: openssl rand -base64 32)}"

# Non-secret fingerprint — sha256("CRED_ENC_KEY_FP_v1:"+key), first 16 hex. MUST match CredCrypto.Fingerprint
# + scripts/deploy.sh in the runner repo (change all three in lockstep).
fp="$(printf 'CRED_ENC_KEY_FP_v1:%s' "${CRED_ENC_KEY}" | openssl dgst -sha256 -hex | awk '{print $NF}' | cut -c1-16)"

echo "Setting CRED_ENC_KEY on ${APP} (fingerprint ${fp})…"
az functionapp config appsettings set -g "${RG}" -n "${APP}" \
  --settings CRED_ENC_KEY="${CRED_ENC_KEY}" -o none

echo "Done. ${APP} CRED_ENC_KEY fingerprint = ${fp}"
echo "Verify: the runner deploy.sh drift-check must report the SAME fingerprint (runner ↔ api key match)."
