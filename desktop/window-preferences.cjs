const fs = require('node:fs/promises');
const path = require('node:path');

const themes = new Set(['system', 'light', 'dark']);
const closeBehaviors = new Set(['tray', 'quit']);

function validatePreferences(value, allowNotice = false) {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new Error('窗口偏好格式不正确');
  const allowed = allowNotice
    ? ['theme', 'closeBehavior', 'trayNoticeSeen']
    : ['theme', 'closeBehavior'];
  if (
    Object.keys(value).some((key) => !allowed.includes(key)) ||
    ('theme' in value && !themes.has(value.theme)) ||
    ('closeBehavior' in value && !closeBehaviors.has(value.closeBehavior)) ||
    ('trayNoticeSeen' in value && typeof value.trayNoticeSeen !== 'boolean')
  )
    throw new Error('窗口偏好值不正确');
}

async function loadWindowPreferences(directory) {
  const file = path.join(directory, 'window-preferences.json');
  let current = { theme: 'system', closeBehavior: 'tray', trayNoticeSeen: false };
  let readError = '';
  let pending = Promise.resolve();
  try {
    const saved = JSON.parse(await fs.readFile(file, 'utf8'));
    validatePreferences(saved, true);
    current = { ...current, ...saved };
  } catch (error) {
    if (error.code !== 'ENOENT')
      readError = '窗口偏好无法读取，原文件已保留。请检查用户数据目录后重新启动应用。';
  }
  return {
    get: () => ({ ...current, ...(readError ? { error: readError } : {}) }),
    update(patch, allowNotice = false) {
      const update = pending
        .catch(() => {})
        .then(async () => {
          if (readError) throw new Error(readError);
          validatePreferences(patch, allowNotice);
          const next = { ...current, ...patch };
          if (JSON.stringify(next) === JSON.stringify(current)) return { ...current };
          const temporary = `${file}.${process.pid}.tmp`;
          try {
            await fs.mkdir(directory, { recursive: true });
            await fs.writeFile(temporary, JSON.stringify(next, null, 2), 'utf8');
            await fs.rename(temporary, file);
          } catch {
            await fs.rm(temporary, { force: true }).catch(() => {});
            throw new Error('窗口偏好保存失败，原设置已保留。请检查用户数据目录是否可写。');
          }
          current = next;
          return { ...current };
        });
      pending = update;
      return update;
    },
  };
}

module.exports = { loadWindowPreferences };
