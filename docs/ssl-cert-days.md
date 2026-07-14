# SSL cert days-remaining — structured field reference

Field-level reference (not day-one onboarding). Moved out of the README so first-contact stays focused.

The dashboard / status page / alert profiles need cert **days-remaining** as a typed field so they don't
regex-parse prose. Two structured cert columns are surfaced:

- `checks.cert_expiry_warn_days` (`int NOT NULL DEFAULT 30`) — the per-check warn *threshold* (config
  input). Exposed as `certExpiryWarnDays` on the check DTOs and accepted on write.
- `runs.cert_days_remaining` (`int NULL`, populated on ssl runs) — the measured value. Exposed additively
  as `certDaysRemaining` (`number | null`; null for non-ssl runs) on `RunDto`, and as
  `lastCertDaysRemaining` (latest run's value) on the check summary.

We deliberately do **not** parse `error_message` in the API (fragile; breaks on wording changes) — it stays
the human-readable text alongside the structured field.

> The column shapes above are owned by the runner's `db/schema.sql` (enforced by the schema-parity CI gate),
> not by this doc. If they disagree, `db/schema.sql` wins.
