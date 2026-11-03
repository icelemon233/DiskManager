import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { NativeClient } = require('../desktop/native-client.cjs');
const root = path.resolve(import.meta.dirname, '..');
const client = new NativeClient(
  path.join(root, 'native/publish'),
  path.join(root, '.runtime/cache-discovery'),
);
try {
  const targets = await client.call('cache.list');
  await fs.writeFile(
    path.join(root, '.runtime/cache-discovery.json'),
    JSON.stringify(targets, null, 2),
  );
  console.log(
    JSON.stringify(
      targets.map((target) => ({ name: target.name, kind: target.kind, tool: !!target.tool })),
      null,
      2,
    ),
  );
} finally {
  await client.close();
}
