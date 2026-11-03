import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { randomUUID } from 'node:crypto';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { NativeClient } = require('../desktop/native-client.cjs');
const root = path.resolve(import.meta.dirname, '..');
const runId = randomUUID();
const testRoot = path.join(root, '.runtime', `integration-${runId}`);
const fixture = path.join(testRoot, '中文 fixture');
const uiData = process.argv.includes('--seed-ui')
  ? path.join(root, '.runtime', 'ui-data')
  : path.join(testRoot, 'state');
await fs.mkdir(fixture, { recursive: true });
await fs.mkdir(uiData, { recursive: true });
const checks = [];
const client = new NativeClient(path.join(root, 'native/publish'), uiData);
client.on('offline', () => {});
async function job(method, params = {}, expectedState = 'complete') {
  const { jobId } = await client.call(method, params);
  const deadline = Date.now() + 120000;
  while (Date.now() < deadline) {
    const record = (await client.call('jobs.list')).find((item) => item.id === jobId);
    if (record && !['running', 'queued'].includes(record.state)) {
      assert.equal(record.state, expectedState, `${method}: ${record.error}`);
      return client.call('jobs.result', { id: jobId });
    }
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  throw new Error(`${method} timed out`);
}
function checked(name) {
  checks.push(name);
  console.log(`PASS ${name}`);
}
async function waitForPersistence() {
  while (
    (await client.call('jobs.list')).some(
      (job) => job.kind === 'snapshot-save' && ['running', 'queued'].includes(job.state),
    )
  )
    await new Promise((resolve) => setTimeout(resolve, 25));
  const failure = (await client.call('jobs.list')).find(
    (job) => job.kind === 'snapshot-save' && job.state === 'failed',
  );
  assert.equal(failure, undefined, failure?.error);
}
try {
  for (const [index, folder] of ['Projects', 'Media', 'Archives', 'Downloads', '文档'].entries()) {
    const directory = path.join(fixture, folder);
    await fs.mkdir(directory);
    for (let file = 1; file <= 12; file++) {
      const extension = ['.zip', '.bin', '.mp4', '.txt'][file % 4];
      const content = Buffer.alloc((index + 1) * file * 4096, index * 20 + file);
      await fs.writeFile(
        path.join(directory, `sample-${String(file).padStart(2, '0')}${extension}`),
        content,
      );
    }
  }
  const original = path.join(fixture, 'Projects', 'sample-01.bin');
  const duplicate = path.join(fixture, 'Downloads', 'duplicate.bin');
  const alias = path.join(fixture, 'Projects', 'hardlink.bin');
  await fs.copyFile(original, duplicate);
  await fs.link(original, alias);
  const first = (await job('scan.start', { roots: [fixture], fast: false })).result;
  assert.equal(first.count, 62);
  let summary = await client.call('summary');
  let roots = await client.call('query', { parent: '' });
  assert.equal(roots.count, 1);
  assert.equal(roots.rows[0].file.path, fixture);
  assert.equal(roots.rows[0].totalAllocated, summary.allocated);
  checked('real directory scan, root aggregation and Unicode');
  assert.ok(first.timing && first.timing.saveMs === null);
  assert.equal(roots.rows[0].folderCount, 5);
  const tree = await client.call('tree', {
    expanded: [fixture],
    sort: 'allocated',
    descending: true,
  });
  assert.equal(tree.count, 6);
  assert.equal(tree.rows[1].depth, 1);
  const map = await client.call('treemap', { scope: fixture, metric: 'allocated' });
  assert.ok(map.some((node) => node.children.length > 0));
  assert.equal(
    map.reduce((sum, node) => sum + node.entry.totalAllocated, 0),
    summary.allocated,
  );
  const selection = await client.call('selection', { paths: [fixture, original] });
  assert.equal(selection.allocated, summary.allocated);
  checked('interactive snapshot, hierarchical table, map and non-overlapping selection');
  const hardlinks = (await client.call('query', { search: 'hardlink', filesOnly: true })).rows;
  assert.equal(hardlinks.length, 1);
  assert.equal(hardlinks[0].file.links, 2);
  const grouped = (
    await client.call('query', { parent: path.join(fixture, 'Projects') })
  ).rows.filter((row) => row.file.identity === hardlinks[0].file.identity);
  assert.equal(grouped.length, 2);
  assert.equal(grouped.filter((row) => row.totalAllocated === 0).length, 1);
  checked('hard-link allocation counted once');
  assert.equal(
    (await client.call('query', { search: '*sample-01*', mode: 'wildcard', filesOnly: true }))
      .count,
    5,
  );
  assert.equal(
    (await client.call('query', { search: 'sample-0[12]\\.bin$', mode: 'regex', filesOnly: true }))
      .count,
    5,
  );
  assert.equal((await client.call('query', { extension: '.zip', filesOnly: true })).count, 15);
  checked('wildcard, regex and extension filters');
  await job('duplicates.start', {});
  const groups = await client.call('duplicates.list');
  assert.equal(groups.count, 1);
  assert.equal(groups.rows[0].files.length, 2);
  await assert.rejects(
    job('operations.plan', {
      action: 'delete',
      paths: groups.rows[0].files.map((file) => file.path),
      duplicateSelection: true,
    }),
    /至少需要保留/,
  );
  checked('content duplicates and last-copy protection');
  const changedPlan = (await job('operations.plan', { action: 'delete', paths: [duplicate] }))
    .result;
  await fs.appendFile(duplicate, 'changed after preview');
  const changed = await job('operations.execute', { id: changedPlan.id });
  assert.equal(changed.result.results[0].state, 'failed');
  assert.ok(await fs.stat(duplicate));
  checked('changed-file deletion rejected');
  const rename = (
    await job('operations.plan', { action: 'rename', paths: [duplicate], name: 'renamed.bin' })
  ).result;
  assert.equal(rename.items[0].error, null);
  await job('operations.execute', { id: rename.id });
  const renamed = path.join(path.dirname(duplicate), 'renamed.bin');
  assert.ok(await fs.stat(renamed));
  await assert.rejects(fs.stat(duplicate), { code: 'ENOENT' });
  assert.equal((await client.call('query', { search: 'renamed.bin' })).count, 1);
  checked('rename with affected-directory refresh');
  const second = (await job('scan.start', { roots: [fixture], fast: false })).result;
  await waitForPersistence();
  const diff = await client.call('history.diff', { older: first.id, newer: second.id });
  assert.ok(diff.rows.some((row) => row.kind === 'moved' && row.path === renamed));
  assert.equal(
    (await client.call('query', { snapshot: first.id, search: 'duplicate.bin' })).count,
    1,
  );
  checked('immutable snapshots and identity-based rename diff');
  const destination = path.join(os.tmpdir(), `DiskManager-Cross-${runId}`);
  await fs.mkdir(destination);
  const move = (await job('operations.plan', { action: 'move', paths: [renamed], destination }))
    .result;
  assert.equal(move.items[0].error, null);
  const moved = await job('operations.execute', { id: move.id });
  assert.equal(moved.result.results[0].state, 'complete', moved.result.results[0].error);
  assert.ok(await fs.stat(path.join(destination, 'renamed.bin')));
  await assert.rejects(fs.stat(renamed), { code: 'ENOENT' });
  checked('cross-volume copy, verification and source removal');
  const protectedPlan = (
    await job('operations.plan', { action: 'delete', paths: [path.parse(fixture).root] })
  ).result;
  assert.ok(protectedPlan.items[0].error);
  checked('volume root rejected');
  const csv = path.join(testRoot, 'filtered.csv');
  await job('export.start', {
    destination: csv,
    kind: 'csv',
    query: { extension: '.zip', filesOnly: true },
  });
  assert.equal((await fs.readFile(csv, 'utf8')).trim().split(/\r?\n/).length, 16);
  const snapshot = path.join(testRoot, 'roundtrip.diskmanager');
  await job('export.start', {
    destination: snapshot,
    kind: 'snapshot',
    query: { snapshot: first.id },
  });
  const imported = (await job('import.start', { source: snapshot })).result;
  assert.equal(imported.imported, true);
  assert.equal((await client.call('summary')).count, first.count);
  await assert.rejects(job('operations.plan', { action: 'delete', paths: [original] }), /重新扫描/);
  checked('CSV filter export, snapshot round trip and imported-record protection');
  const finalScan = (await job('scan.start', { roots: [fixture], fast: false })).result;
  const saved = await client.call('rules.save', {
    id: '',
    name: 'Fixture binaries',
    roots: [fixture],
    pattern: '*.bin',
    extension: '.bin',
    minSize: 0,
    maxSize: null,
    olderDays: null,
    exclusions: [path.join(fixture, 'Projects')],
  });
  const rulePlan = (await job('rules.preview', { id: saved.id })).result;
  assert.ok(rulePlan.items.length > 0);
  assert.ok(rulePlan.items.every((item) => !item.path.startsWith(path.join(fixture, 'Projects'))));
  checked('custom rules and exclusions');
  const operationLog = await client.call('operations.history');
  assert.ok(operationLog.length >= 3);
  checked('per-item operation records');
  await client.close();
  const reopened = new NativeClient(path.join(root, 'native/publish'), uiData);
  reopened.on('offline', () => {});
  try {
    assert.equal((await reopened.call('overview')).current.id, finalScan.id);
    assert.equal((await reopened.call('summary')).count, finalScan.count);
    assert.ok((await reopened.call('history.list')).some((item) => item.id === finalScan.id));
  } finally {
    await reopened.close();
  }
  checked('background save completes on close and restores after restart');
  await fs.writeFile(
    path.join(root, '.runtime', 'integration-results.json'),
    JSON.stringify(
      {
        runId,
        testRoot,
        fixture,
        uiData,
        crossVolumeFixture: destination,
        checks,
        passed: true,
        finished: new Date().toISOString(),
      },
      null,
      2,
    ),
  );
  console.log(JSON.stringify({ passed: checks.length, fixture, uiData }));
} finally {
  await client.close();
}
