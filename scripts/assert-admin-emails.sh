#!/usr/bin/env bash
# assert-admin-emails.sh — fail LOUD if ADMIN_EMAILS is empty/absent.
#
# ★ ADMIN_EMAILS is the API's SOLE source of admin identity (the dashboard's isAdmin comes from /auth/me, so
# they cannot disagree). It lives ONLY as an out-of-band Azure app setting — the CD is code-only and never
# applies infra/main.bicep, whose `adminEmails` param defaults to '' (empty). A manual
# `az deployment group create` WITHOUT `-p adminEmails=…` sets ADMIN_EMAILS='' and LOCKS OUT EVERY ADMIN — and
# nothing else asserts it (the CD drift-check watches storage/appinsights/postgres/cred-key; the runner's
# verify() never touches the API). This guard closes that gap: an empty list FAILS the deploy.
#
# ★★ NEVER prints the value — it is a list of real employee emails. Only the entry COUNT is logged.
# The failure mode to catch is '' (WIPED), NOT "the wrong people are on it".
#
# Reads ADMIN_EMAILS from the environment. Exit 1 (loud) if it parses to zero entries; exit 0 otherwise.
set -uo pipefail

val="${ADMIN_EMAILS-}"

# Count non-empty, comma-split, trimmed entries — mirrors the API's parse contract
# (Split(',', RemoveEmptyEntries | TrimEntries)). No address is ever echoed.
count="$(printf '%s' "$val" | tr ',' '\n' | sed 's/^[[:space:]]*//; s/[[:space:]]*$//' | grep -c .)" || count=0

if [ "${count:-0}" -lt 1 ]; then
  echo "::error::ADMIN_EMAILS is EMPTY or absent — EVERY ADMIN IS LOCKED OUT. Refusing to deploy. It is set OUT-OF-BAND (az functionapp config appsettings set … --settings ADMIN_EMAILS=…) and is NOT applied by this CD; a manual bicep apply without -p adminEmails=… wipes it." >&2
  exit 1
fi

echo "ADMIN_EMAILS present: ${count} entr$([ "$count" -eq 1 ] && printf y || printf ies) (value not logged)."
