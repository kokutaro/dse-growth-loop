// The suites mutate fixed Demo identities. Restart their in-memory API for each test
// so upload/write limits and another test's withdrawn profile cannot contaminate it.
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { mkdirSync, writeFileSync } from 'node:fs';
const require = createRequire(import.meta.url);
const cli = require.resolve('@playwright/test/cli');
const filters = process.argv.slice(2);
const listed = spawnSync(process.execPath, [cli, 'test', ...filters, '--list', '--reporter=json'], { encoding: 'utf8' });
if (listed.status !== 0) { process.stderr.write(listed.stderr); process.stdout.write(listed.stdout); process.exit(listed.status ?? 1); }
const report = JSON.parse(listed.stdout);
function specs(suites) { return suites.flatMap(s => [...(s.specs ?? []), ...specs(s.suites ?? [])]); }
const all = specs(report.suites);
if (all.length === 0) {
  process.stderr.write('No E2E tests selected. Check file and --grep filters.\n');
  process.exit(1);
}
let failed = 0;
const results = [];
for (const [i, spec] of all.entries()) {
  process.stdout.write(`\n[isolated ${i + 1}/${all.length}] ${spec.title}\n`);
  const exactTitle = spec.title.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$';
  const output = `test-results/isolated/${i + 1}`;
  const started = Date.now();
  const result = spawnSync(process.execPath, [cli, 'test', spec.file, '--grep', exactTitle, '--reporter=line', '--output', output], { stdio: 'inherit', env: { ...process.env, DSE_ANALYSIS_FIXTURES: spec.file.endsWith('business-analysis.spec.ts') ? 'true' : 'false' } });
  results.push({ file: spec.file, line: spec.line, title: spec.title, exitCode: result.status, durationMs: Date.now() - started, output });
  if (result.status !== 0) failed++;
}
process.stdout.write(`\nIsolated E2E: ${all.length - failed} passed, ${failed} failed (${all.length} tests).\n`);
mkdirSync('test-results', { recursive: true });
writeFileSync('test-results/isolated-summary.json', JSON.stringify({ passed: all.length - failed, failed, results }, null, 2) + '\n');
process.exit(failed ? 1 : 0);
