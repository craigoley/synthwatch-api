#!/usr/bin/env node
// Grant-coverage check — asserts the Azure RBAC role assignments the synthwatch-api MI is GRANTED in
// infra/main.bicep EXACTLY match the set its endpoints REQUIRE in infra/required-grants.json.
//
// Catches the missing-grant class (dismiss-500 / reconcile / AOAI / email) at CI instead of a runtime 500:
//   • FAIL-CLOSED: a role the manifest requires but Bicep doesn't grant → exit 1 (a new endpoint that needs a
//     grant can't merge without it).
//   • Also fails on an UNDECLARED Bicep grant (granted but not justified in the manifest) → keeps the manifest
//     the honest single source of truth.
//
// Non-flaky by design: a pure static parse of the Bicep SOURCE + the manifest — NO live Azure RBAC query (which
// is subject to propagation lag). The Postgres-plane grants (runner-owned migrations) are documented in the
// manifest but not enforced here — asserting them needs the live DB / the runner repo.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const bicep = readFileSync(join(root, 'infra/main.bicep'), 'utf8');
const manifest = JSON.parse(readFileSync(join(root, 'infra/required-grants.json'), 'utf8'));

const MI = 'functionApp.identity.principalId'; // the synthwatch-api system-assigned MI in Bicep

// 1) roleVar -> GUID (the `var fooRoleId = '<guid>'` lines).
const roleVars = {};
for (const m of bicep.matchAll(/var\s+(\w+)\s*=\s*'([0-9a-fA-F-]{36})'/g)) {
  roleVars[m[1]] = m[2].toLowerCase();
}

// 2) Parse each `Microsoft.Authorization/roleAssignments` resource block scoped to the MI.
//    Split on top-level `resource ` so each block is bounded — no cross-block regex bleed.
const granted = [];
const problems = [];
for (const seg of bicep.split(/\nresource\s/)) {
  if (!seg.includes("'Microsoft.Authorization/roleAssignments")) continue;
  const sym = seg.match(/^(\w+)/)?.[1] ?? '(unknown)';
  const principal = seg.match(/principalId:\s*([\w.]+)/)?.[1];
  if (principal !== MI) continue; // a grant to some OTHER principal — not the API MI, ignore
  const scope = seg.match(/scope:\s*(\w+)/)?.[1];
  const roleVar = seg.match(/roleDefinitions',\s*(\w+)\)/)?.[1];
  const roleId = roleVars[roleVar];
  if (!scope || !roleVar) { problems.push(`assignment '${sym}': could not parse scope/roleDefinitionId`); continue; }
  if (!roleId) { problems.push(`assignment '${sym}': role var '${roleVar}' has no matching GUID var`); continue; }
  granted.push({ roleId, scope, sym });
}

// 3) Compare granted (Bicep) vs required (manifest), keyed on (roleId @ scope).
const key = (g) => `${g.roleId}@${g.scope}`;
const required = (manifest.azureRbac ?? []).map((r) => ({
  roleId: String(r.roleId).toLowerCase(), scope: r.scope, role: r.role, neededBy: r.neededBy,
}));

for (const r of required) {
  if (!/^[0-9a-f-]{36}$/.test(r.roleId)) problems.push(`manifest role '${r.role}' has a malformed roleId`);
}

const grantedKeys = new Set(granted.map(key));
const requiredKeys = new Set(required.map(key));
const missing = required.filter((r) => !grantedKeys.has(key(r)));     // required but NOT granted in Bicep
const undeclared = granted.filter((g) => !requiredKeys.has(key(g)));  // granted in Bicep but NOT in the manifest

console.log(`grant-coverage: ${required.length} required, ${granted.length} granted (MI role assignments in Bicep)`);

if (missing.length) {
  console.error('\n::error::MISSING GRANT — the API MI needs a role its endpoints require but Bicep does not grant:');
  for (const m of missing) console.error(`  - ${m.role} @ ${m.scope}  (needed by: ${m.neededBy})`);
}
if (undeclared.length) {
  console.error('\n::error::UNDECLARED GRANT — Bicep grants a role not justified in infra/required-grants.json:');
  for (const u of undeclared) console.error(`  - role ${u.roleId} @ ${u.scope}  (Bicep resource '${u.sym}')`);
}
if (problems.length) {
  console.error('\n::error::PARSE PROBLEMS:');
  for (const p of problems) console.error(`  - ${p}`);
}

if (missing.length || undeclared.length || problems.length) {
  console.error('\nFix: add the grant to infra/main.bicep AND declare it in infra/required-grants.json so the two match.');
  process.exit(1);
}

console.log('grant-coverage: OK — every required role is granted in Bicep, and every Bicep grant is declared.');
