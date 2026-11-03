import fs from 'node:fs/promises';
import path from 'node:path';
import { performance } from 'node:perf_hooks';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { NativeClient } = require('../desktop/native-client.cjs');
const root = path.resolve(import.meta.dirname, '..');
const client = new NativeClient(
  path.join(root, 'native/publish'),
  path.join(root, '.runtime', 'volume-data'),
);
const cases = [
  ['root folders', 'query', { parent: 'D:\\', limit: 200 }],
  ['largest files', 'query', { filesOnly: true, limit: 200 }],
  ['name search', 'query', { search: 'package.json', filesOnly: true, limit: 200 }],
  ['extension filter', 'query', { extension: '.zip', filesOnly: true, limit: 200 }],
  ['summary', 'summary', {}],
];
try {
  const results = [];
  for (const [name, method, params] of cases) {
    const samples = [];
    let count;
    for (let repeat = 0; repeat < 3; repeat++) {
      const start = performance.now();
      const result = await client.call(method, params);
      samples.push(Math.round(performance.now() - start));
      count = result.count;
    }
    results.push({ name, samplesMs: samples, count });
    console.log(JSON.stringify(results.at(-1)));
  }
  await fs.writeFile(
    path.join(root, '.runtime/query-benchmark.json'),
    JSON.stringify({ rows: (await client.call('overview')).current.count, results }, null, 2),
  );
} finally {
  await client.close();
}
