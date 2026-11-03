import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import sharp from 'sharp';

const directory = path.resolve(import.meta.dirname, '../assets');
const source = await fs.readFile(path.join(directory, 'cinna-disk-source.png'));
const portrait = await sharp(source)
  .resize(940, 940, { fit: 'contain', background: '#00000000' })
  .png()
  .toBuffer();
const background = Buffer.from(
  '<svg width="1024" height="1024"><rect width="1024" height="1024" rx="224" fill="#FFFCF6"/></svg>',
);
const master = await sharp(background)
  .composite([
    { input: portrait, left: 42, top: 42 },
    { input: background, blend: 'dest-in' },
  ])
  .png()
  .toBuffer();
await fs.writeFile(path.join(directory, 'icon-master.png'), master);
await sharp(master).resize(512, 512).toFile(path.join(directory, 'icon.png'));
await sharp(master).resize(32, 32).toFile(path.join(directory, 'tray.png'));
const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
const images = await Promise.all(
  sizes.map((size) => sharp(master).resize(size, size).png().toBuffer()),
);
const header = Buffer.alloc(6 + sizes.length * 16);
header.writeUInt16LE(1, 2);
header.writeUInt16LE(sizes.length, 4);
let offset = header.length;
images.forEach((image, index) => {
  const entry = 6 + index * 16;
  header[entry] = sizes[index] === 256 ? 0 : sizes[index];
  header[entry + 1] = header[entry];
  header.writeUInt16LE(1, entry + 4);
  header.writeUInt16LE(32, entry + 6);
  header.writeUInt32LE(image.length, entry + 8);
  header.writeUInt32LE(offset, entry + 12);
  offset += image.length;
});
await fs.writeFile(path.join(directory, 'icon.ico'), Buffer.concat([header, ...images]));
await fs.writeFile(
  path.join(directory, 'brand-manifest.json'),
  JSON.stringify(
    {
      product: 'DiskManager',
      variant: 'disk-analyzer',
      source: 'cinna-disk-source.png',
      sourceSha256: createHash('sha256').update(source).digest('hex'),
      masterSha256: createHash('sha256').update(master).digest('hex'),
      sizes,
    },
    null,
    2,
  ) + '\n',
);
await fs.mkdir(path.resolve(directory, '../public'), { recursive: true });
await fs.copyFile(path.join(directory, 'icon.png'), path.resolve(directory, '../public/icon.png'));
console.log('Exported DiskManager identity assets');
