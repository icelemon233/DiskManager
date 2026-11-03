import fs from 'node:fs/promises';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const root = path.resolve(import.meta.dirname, '..');
const destination = path.join(root, 'licenses', 'app');
await fs.mkdir(destination, { recursive: true });
const product = JSON.parse(await fs.readFile(path.join(root, 'package.json'), 'utf8'));
const packages = new Map();
async function manifestFor(name, from) {
  let directory = from;
  while (true) {
    const candidate = path.join(directory, 'node_modules', name, 'package.json');
    try {
      await fs.access(candidate);
      return await fs.realpath(candidate);
    } catch {}
    const parent = path.dirname(directory);
    if (parent === directory) return null;
    directory = parent;
  }
}
async function visit(name, from) {
  const manifest = (await manifestFor(name, from)) ?? (await manifestFor(name, root));
  if (!manifest) return;
  const content = JSON.parse(await fs.readFile(manifest, 'utf8'));
  const key = `${content.name}@${content.version}`;
  if (packages.has(key)) return;
  const directory = path.dirname(manifest);
  const licenses = (await fs.readdir(directory)).filter((file) =>
    /^(licen[sc]e|copying|notice)(\.|$)/i.test(file),
  );
  const target = path.join(destination, key.replace(/[^A-Za-z0-9_.-]/g, '_'));
  await fs.mkdir(target, { recursive: true });
  for (const file of licenses)
    await fs.copyFile(path.join(directory, file), path.join(target, file));
  if (!licenses.length) {
    const cached = path.join(target, 'LICENSE');
    try {
      await fs.access(cached);
    } catch {
      const repository =
        typeof content.repository === 'string' ? content.repository : content.repository?.url;
      const match = /github\.com[/:]([A-Za-z0-9_.-]+)\/([A-Za-z0-9_.-]+)/.exec(repository ?? '');
      if (!match) throw new Error(`Missing upstream license location for ${key}`);
      const name = match[2].replace(/\.git$/, '');
      execFileSync(
        'pwsh',
        [
          '-NoProfile',
          '-File',
          path.join(root, 'scripts/fetch-license.ps1'),
          '-Uri',
          `https://api.github.com/repos/${match[1]}/${name}/license`,
          '-Destination',
          cached,
          '-Mode',
          'github-license',
        ],
        { windowsHide: true, stdio: 'inherit' },
      );
    }
    licenses.push('LICENSE');
  }
  packages.set(key, {
    name: content.name,
    version: content.version,
    license: content.license,
    files: licenses,
  });
  for (const dependency of Object.keys({ ...content.dependencies, ...content.peerDependencies }))
    await visit(dependency, directory);
}
for (const name of Object.keys(product.dependencies)) await visit(name, root);

const nativeAssets = JSON.parse(
  await fs.readFile(path.join(root, 'native/obj/project.assets.json'), 'utf8'),
);
const nativeRoot = nativeAssets.project.restore.packagesPath;
const nativePackages = Object.keys(nativeAssets.libraries).map((key) => {
  const slash = key.lastIndexOf('/');
  return { name: key.slice(0, slash), version: key.slice(slash + 1) };
});
for (const framework of Object.values(nativeAssets.project.frameworks)) {
  for (const dependency of framework.downloadDependencies ?? []) {
    if (dependency.name === 'Microsoft.NETCore.App.Runtime.win-x64')
      nativePackages.push({
        name: dependency.name,
        version: dependency.version.match(/\d+\.\d+\.\d+/)[0],
      });
  }
}
for (const dependency of nativePackages) {
  const directory = path.join(nativeRoot, dependency.name.toLowerCase(), dependency.version);
  const files = await fs.readdir(directory);
  const licenses = files.filter((file) => /license|notice/i.test(file) && !file.endsWith('.nupkg'));
  const target = path.join(destination, `native_${dependency.name}-${dependency.version}`);
  await fs.mkdir(target, { recursive: true });
  for (const file of licenses)
    if ((await fs.stat(path.join(directory, file))).isFile())
      await fs.copyFile(path.join(directory, file), path.join(target, file));
  // NuGet packages with SPDX expression licenses retain their original metadata as well.
  for (const file of files.filter((file) => file.endsWith('.nuspec')))
    await fs.copyFile(path.join(directory, file), path.join(target, file));
}
const texts = {
  'native-MIT.txt': 'https://raw.githubusercontent.com/dotnet/runtime/main/LICENSE.TXT',
  'sqlitepclraw-Apache-2.0.txt':
    'https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/master/LICENSE.TXT',
};
for (const [name, url] of Object.entries(texts)) {
  const target = path.join(destination, name);
  try {
    await fs.access(target);
  } catch {
    execFileSync(
      'pwsh',
      [
        '-NoProfile',
        '-File',
        path.join(root, 'scripts/fetch-license.ps1'),
        '-Uri',
        url,
        '-Destination',
        target,
      ],
      { windowsHide: true, stdio: 'inherit' },
    );
  }
}
await fs.writeFile(
  path.join(destination, 'packages.json'),
  JSON.stringify({ javascript: [...packages.values()], native: nativePackages }, null, 2) + '\n',
);
console.log(
  `Collected notices for ${packages.size} JavaScript packages and ${nativePackages.length} native packages`,
);
