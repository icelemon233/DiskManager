import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { NativeClient } = require('../desktop/native-client.cjs');
const root = path.resolve(import.meta.dirname, '..');
const drive = process.argv[2] || 'D:\\';
if (!/^[A-Za-z]:\\$/.test(drive)) throw new Error('Specify a local drive root');
const suffix = drive[0].toUpperCase();
const client = new NativeClient(
  path.join(root, 'native/publish'),
  path.join(root, '.runtime', `volume-data-${suffix}`),
);
client.on('offline', () => {});
let lastPhase = '';
client.on('event', (event) => {
  if (event.name !== 'jobs') return;
  const job = event.data.find((job) => job.state === 'running');
  if (job && job.phase !== lastPhase && !job.phase.startsWith('扫描 ')) {
    lastPhase = job.phase;
    console.log(JSON.stringify({ phase: job.phase, count: job.completed }));
  }
});
try {
  const started = Date.now();
  const { jobId } = await client.call('scan.start', { roots: [drive], fast: true });
  while (true) {
    await new Promise((resolve) => setTimeout(resolve, 1000));
    const job = (await client.call('jobs.list')).find((job) => job.id === jobId);
    if (!job || ['running', 'queued'].includes(job.state)) continue;
    const result = await client.call('jobs.result', { id: jobId });
    const summary = job.state === 'complete' ? await client.call('summary') : null;
    const report = {
      elapsedMs: Date.now() - started,
      state: job.state,
      error: job.error,
      scan: result.result,
      totals: summary && { count: summary.count, size: summary.size, allocated: summary.allocated },
      issues: summary?.issues.slice(0, 8),
    };
    await fs.writeFile(
      path.join(root, '.runtime', `real-scan-${suffix}-results.json`),
      JSON.stringify(report, null, 2),
    );
    console.log(JSON.stringify(report, null, 2));
    break;
  }
} finally {
  await client.close();
}
