import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';

const root = path.resolve(import.meta.dirname, '..');
const directory = path.join(root, 'desktop/win32');
const digest = (data) => createHash('sha256').update(data).digest('hex');
export async function verifyWindowFrame() {
  const record = JSON.parse(
    await fs.readFile(path.join(directory, 'window-frame-build.json'), 'utf8'),
  );
  for (const [file, hash] of [
    ['window-frame.c', record.sourceSha256],
    ['window-frame.node', record.moduleSha256],
  ]) {
    if (digest(await fs.readFile(path.join(directory, file))) !== hash)
      throw new Error(
        'Native window module is stale. Run node scripts/build-window-frame.mjs with ZIG set to the Zig 0.15.2 compiler.',
      );
  }
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const compiler = process.env.ZIG || 'zig';
  const version = execFileSync(compiler, ['version'], { encoding: 'utf8' }).trim();
  if (version !== '0.15.2') throw new Error('Use Zig 0.15.2 for the Windows frame module');
  execFileSync(
    compiler,
    [
      'cc',
      '-target',
      'x86_64-windows-gnu',
      '-shared',
      '-O2',
      '-s',
      '-fno-sanitize=undefined',
      path.join(directory, 'window-frame.c'),
      '-lcomctl32',
      '-ldwmapi',
      '-luser32',
      '-o',
      path.join(directory, 'window-frame.node'),
    ],
    { stdio: 'inherit' },
  );
  const record = {
    target: 'Windows x64 / Node-API 8',
    compiler: `Zig ${version}`,
    sourceSha256: digest(await fs.readFile(path.join(directory, 'window-frame.c'))),
    moduleSha256: digest(await fs.readFile(path.join(directory, 'window-frame.node'))),
  };
  await fs.rm(path.join(directory, 'window-frame.lib'), { force: true });
  await fs.writeFile(
    path.join(directory, 'window-frame-build.json'),
    JSON.stringify(record, null, 2) + '\n',
  );
  await verifyWindowFrame();
  console.log('Built and verified Windows native resize module');
}
