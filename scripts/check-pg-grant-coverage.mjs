#!/usr/bin/env node
// Postgres grant-coverage check — asserts every table the synthwatch-api role WRITES has the matching
// GRANT, catching the missing-grant class (dismiss-500 = missing DELETE on access_requests; approve-500 =
// missing UPDATE on reconcile_apply_plan) at CI instead of a runtime 500. The sibling of the Azure-RBAC
// check (scripts/check-grant-coverage.mjs); together they cover both grant planes.
//
// ★ Why this matters: a missing GRANT to "synthwatch-api" is INVISIBLE to admin psql + Testcontainers (both
// run as SUPERUSER, and the test snapshot has no synthwatch-api role) — so it only surfaces as a prod 500.
// This is a pure STATIC parse (no live DB, no Azure call): fail-closed, sub-second, runs on every PR.
//
//   REQUIRED  = infra/required-grants.json `postgres.writes` (the WRITE privileges the handlers need).
//   GRANTED   = `GRANT … TO "synthwatch-api"` parsed from the RUNNER repo's db/migrations/*.sql (the runner
//               owns schema/grants) ∪ `postgres.opsBaseline` (grants applied at role setup, not via a migration).
//   FAIL if any required (table, privilege) is in neither — naming the table + the missing privilege.
//
// Secondary auto-catch: scans THIS repo's Functions/*.cs for raw-SQL writes (INSERT INTO / DELETE FROM /
// UPDATE … SET) and fails if one names a table absent from `postgres.writes` — so a new raw write can't merge
// without being declared (EF change-tracking writes can't be parsed statically; those rely on the allowlist).
//
// Usage:  node scripts/check-pg-grant-coverage.mjs [<runner-migrations-dir>]
//   migrations dir resolves from: arg → $RUNNER_MIGRATIONS_DIR → ./runner-repo/db/migrations (CI checkout)
//   → ../synthwatch/db/migrations (local sibling). The CI workflow checks out craigoley/synthwatch first.

import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const DML = ['SELECT', 'INSERT', 'UPDATE', 'DELETE'];
const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const manifest = JSON.parse(readFileSync(join(root, 'infra/required-grants.json'), 'utf8'));
const pg = manifest.postgres ?? {};

// ── locate the runner repo's migrations (the GRANTED source) ──────────────────────────────────────────────
function resolveMigrationsDir() {
  const candidates = [
    process.argv[2],
    process.env.RUNNER_MIGRATIONS_DIR,
    join(root, 'runner-repo/db/migrations'),
    join(root, '../synthwatch/db/migrations'),
  ].filter(Boolean);
  for (const c of candidates) if (existsSync(c)) return c;
  console.error('::error::cannot find the runner migrations dir. Pass it as an arg, set $RUNNER_MIGRATIONS_DIR,');
  console.error('  or check out craigoley/synthwatch into ./runner-repo (the CI workflow does this).');
  console.error(`  tried: ${candidates.join(', ')}`);
  process.exit(2);
}
const migrationsDir = resolveMigrationsDir();

// ── GRANTED: parse `GRANT <privs> ON <tables> TO "synthwatch-api"` from every migration ────────────────────
// privs are UPPERCASE words+commas; tables are lowercase identifiers+commas — so `ON FUNCTION slo_status(…)`
// (uppercase FUNCTION) never matches the table group → function grants are skipped, as intended.
const GRANT_RE = /GRANT\s+(ALL(?:\s+PRIVILEGES)?|[A-Z][A-Z,\s]*?)\s+ON\s+([a-z_][a-z0-9_,\s]*?)\s+TO\s+"?synthwatch-api"?/gi;
const granted = new Map(); // table -> Set(privs)
const addGrant = (table, privs) => {
  const set = granted.get(table) ?? new Set();
  for (const p of privs) set.add(p);
  granted.set(table, set);
};
// Strip SQL comments first so a COMMENTED-OUT grant isn't falsely counted as granted (`-- GRANT … TO …`).
const stripSqlComments = (s) => s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/--[^\n]*/g, '');
for (const f of readdirSync(migrationsDir).filter((n) => n.endsWith('.sql')).sort()) {
  const sql = stripSqlComments(readFileSync(join(migrationsDir, f), 'utf8'));
  for (const m of sql.matchAll(GRANT_RE)) {
    const rawPrivs = m[1].toUpperCase().trim();
    const privs = /^ALL\b/.test(rawPrivs) ? DML.slice() : rawPrivs.split(',').map((p) => p.trim()).filter((p) => DML.includes(p));
    const tables = m[2].split(',').map((t) => t.trim()).filter(Boolean);
    for (const t of tables) addGrant(t, privs);
  }
}
// ∪ the documented ops-baseline (grants applied at role setup, not via a migration).
for (const [t, privs] of Object.entries(pg.opsBaseline ?? {})) {
  if (t.startsWith('$')) continue;
  addGrant(t, privs.map((p) => p.toUpperCase()));
}

// ── REQUIRED: the write privileges the handlers need ──────────────────────────────────────────────────────
const required = (pg.writes ?? []);
const missing = [];
for (const r of required) {
  const have = granted.get(r.table) ?? new Set();
  for (const priv of r.privileges.map((p) => p.toUpperCase())) {
    if (!have.has(priv)) missing.push({ table: r.table, priv, neededBy: r.neededBy });
  }
}

// ── secondary auto-catch: every raw-SQL write table in the API must be declared in `writes` ───────────────
function rawWriteTables() {
  const found = new Map(); // table -> Set(verb)
  const dirs = ['Functions', 'Infrastructure', 'Data'].map((d) => join(root, d)).filter(existsSync);
  const walk = (d) => readdirSync(d, { withFileTypes: true }).flatMap((e) => {
    const p = join(d, e.name);
    return e.isDirectory() ? walk(p) : p.endsWith('.cs') ? [p] : [];
  });
  for (const dir of dirs) for (const file of walk(dir)) {
    // Neutralize `ON CONFLICT … DO UPDATE SET` so it isn't read as a table UPDATE.
    const cs = readFileSync(file, 'utf8').replace(/DO\s+UPDATE\s+SET/gi, 'DO_CONFLICT');
    for (const [re, verb] of [
      [/INSERT\s+INTO\s+([a-z_][a-z0-9_]*)/gi, 'INSERT'],
      [/DELETE\s+FROM\s+([a-z_][a-z0-9_]*)/gi, 'DELETE'],
      [/\bUPDATE\s+([a-z_][a-z0-9_]*)\s+SET/gi, 'UPDATE'],
    ]) {
      for (const m of cs.matchAll(re)) {
        const set = found.get(m[1]) ?? new Set();
        set.add(verb);
        found.set(m[1], set);
      }
    }
  }
  return found;
}
const declared = new Set(required.map((r) => r.table));
const undeclaredWrites = [];
for (const [table, verbs] of rawWriteTables()) {
  if (!declared.has(table)) undeclaredWrites.push({ table, verbs: [...verbs].sort().join(',') });
}

// ── report (mirrors check-grant-coverage.mjs: ::error:: annotations, fail-closed) ─────────────────────────
console.log(
  `pg-grant-coverage: ${required.length} write-table(s) required; ` +
    `${granted.size} table(s) granted (migrations:${migrationsDir.replace(root + '/', '')} ∪ opsBaseline)`,
);

if (missing.length) {
  console.error('\n::error::MISSING POSTGRES GRANT — the API writes a table the synthwatch-api role lacks the privilege on:');
  for (const m of missing) {
    console.error(`  - ${m.priv} on ${m.table}  (needed by: ${m.neededBy})`);
  }
  console.error('  → add a runner migration: GRANT <priv> ON <table> TO "synthwatch-api";  (this is the dismiss-500 / approve-500 class)');
}
if (undeclaredWrites.length) {
  console.error('\n::error::UNDECLARED WRITE — the API raw-SQL writes a table not listed in infra/required-grants.json `postgres.writes`:');
  for (const u of undeclaredWrites) console.error(`  - ${u.table} (${u.verbs}) — add it to postgres.writes (and grant it) so coverage is enforced.`);
}

if (missing.length || undeclaredWrites.length) {
  process.exit(1);
}
console.log('pg-grant-coverage: OK — every required write privilege is granted, and every raw-SQL write is declared.');
