const {
  app,
  BrowserWindow,
  ipcMain,
  Menu,
  Tray,
  nativeImage,
  nativeTheme,
  dialog,
  shell,
  clipboard,
  session,
} = require('electron');
const path = require('node:path');
const fs = require('node:fs/promises');
const { NativeClient } = require('./native-client.cjs');
const { loadWindowPreferences } = require('./window-preferences.cjs');
const { TrayMenu } = require('./tray-menu.cjs');
const { within } = require('./async-timeout.cjs');

const methods = new Set([
  'meta',
  'overview',
  'scan.authorize',
  'scan.start',
  'scan.refresh',
  'query',
  'entry',
  'types',
  'tree',
  'treemap',
  'selection',
  'summary',
  'history.list',
  'history.open',
  'history.update',
  'history.diff',
  'duplicates.estimate',
  'duplicates.start',
  'duplicates.list',
  'operations.plan',
  'operations.history',
  'cache.list',
  'cache.preview',
  'rules.save',
  'rules.delete',
  'rules.preview',
  'preferences.save',
  'jobs.list',
  'jobs.result',
  'jobs.cancel',
]);
const qaIndex = process.argv.indexOf('--qa-data');
const qaData = qaIndex >= 0 ? process.argv[qaIndex + 1] : null;
const captureQa = !!qaData && process.argv.includes('--qa-capture');
const authorizationQaIndex = process.argv.indexOf('--qa-authorize');
const authorizationQaRoot =
  captureQa && authorizationQaIndex >= 0 ? process.argv[authorizationQaIndex + 1] : null;
app.disableHardwareAcceleration();
if (
  qaData &&
  (!path.isAbsolute(qaData) || !path.resolve(qaData).includes(`${path.sep}.runtime${path.sep}`))
)
  throw new Error('QA requires isolated .runtime app data');
app.setName('DiskManager');
app.setPath('userData', qaData || path.join(app.getPath('appData'), 'DiskManager'));
let win, tray, trayMenu, client, preferences;
let exiting = false,
  quitting = false,
  trayPending = false,
  confirmationPending = false;
let jobs = [];
function emit(name, data) {
  if (win && !win.isDestroyed()) win.webContents.send('disk:event', { name, data });
}
function restore() {
  trayMenu?.hide(false);
  if (win.isMinimized()) win.restore();
  win.show();
  win.focus();
}
function trusted(event) {
  if (event.sender !== win.webContents || event.senderFrame !== win.webContents.mainFrame)
    throw new Error('此页面无法访问本机功能。');
}
function validatedPath(value) {
  if (
    typeof value !== 'string' ||
    value.length > 32760 ||
    !path.isAbsolute(value) ||
    value.startsWith('\\\\') ||
    value.includes('\0')
  )
    throw new Error('路径无效。');
  return path.normalize(value);
}
function colors() {
  return nativeTheme.shouldUseDarkColors
    ? { background: '#1b1d20', text: '#f2efea' }
    : { background: '#fffcf6', text: '#46332a' };
}
async function hideToTray() {
  if (trayPending) return;
  trayPending = true;
  try {
    if (!tray || tray.isDestroyed()) throw new Error('系统托盘不可用，请从设置退出应用。');
    if (!preferences.get().trayNoticeSeen) {
      const result = await dialog.showMessageBox(win, {
        type: 'info',
        title: '进入系统托盘',
        message: '关闭窗口后，DiskManager 会在系统托盘继续运行。',
        detail: '点击托盘图标可重新打开。',
        buttons: ['进入托盘', '取消'],
        defaultId: 0,
        cancelId: 1,
        noLink: true,
      });
      if (result.response !== 0) return;
      await preferences.update({ trayNoticeSeen: true }, true);
    }
    win.hide();
  } finally {
    trayPending = false;
  }
}

if (!app.requestSingleInstanceLock()) app.quit();
else {
  app.on('second-instance', (_event, argv) =>
    argv.includes('--request-exit') ? app.quit() : restore(),
  );
  app
    .whenReady()
    .then(async () => {
      if (process.argv.includes('--request-exit')) {
        exiting = true;
        app.quit();
        return;
      }
      app.setAppUserModelId(qaData ? 'local.cinna.diskmanager.qa' : 'local.cinna.diskmanager');
      preferences = await loadWindowPreferences(app.getPath('userData'));
      nativeTheme.themeSource = preferences.get().theme;
      const root = app.getAppPath();
      client = new NativeClient(
        app.isPackaged
          ? path.join(process.resourcesPath, 'native')
          : path.join(root, 'native/publish'),
        app.getPath('userData'),
        app.isPackaged ? path.dirname(process.execPath) : path.join(root, 'native/publish'),
      );
      win = new BrowserWindow({
        width: 1440,
        height: 940,
        minWidth: 920,
        minHeight: 680,
        title: 'DiskManager',
        show: false,
        frame: true,
        thickFrame: true,
        roundedCorners: true,
        titleBarStyle: 'hidden',
        titleBarOverlay: false,
        backgroundColor: colors().background,
        icon: path.join(root, 'assets/icon.png'),
        autoHideMenuBar: true,
        webPreferences: {
          preload: path.join(__dirname, 'preload.cjs'),
          contextIsolation: true,
          nodeIntegration: false,
          sandbox: true,
          webviewTag: false,
          spellcheck: false,
          backgroundThrottling: false,
        },
      });
      if (qaData) console.log('QA window created');
      require('./win32/window-frame.node').attach(win.getNativeWindowHandle(), 44, 46);
      win.hookWindowMessage(0x8443, (parameter) => {
        const state = parameter.readUInt32LE(0);
        emit('caption', { hovered: state & 255, pressed: (state >> 8) & 255 });
      });
      for (const event of ['maximize', 'unmaximize'])
        win.on(event, () => emit('window-state', { maximized: win.isMaximized() }));
      Menu.setApplicationMenu(null);
      session.defaultSession.setPermissionRequestHandler((_webContents, _permission, callback) =>
        callback(false),
      );
      win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
      win.webContents.on('will-navigate', (event) => event.preventDefault());
      client.on('event', (event) => {
        if (event.name === 'jobs') jobs = event.data;
        emit(event.name, event.data);
      });
      client.on('offline', (message) => {
        if (!quitting) emit('error', message);
      });
      ipcMain.on('disk:startup', (event) => {
        try {
          trusted(event);
          event.returnValue = {
            ...preferences.get(),
            nativeTheme: nativeTheme.shouldUseDarkColors ? 'dark' : 'light',
            qa: !!qaData,
          };
        } catch {
          event.returnValue = null;
        }
      });
      ipcMain.handle('disk:call', (event, method, params) => {
        trusted(event);
        if (
          quitting ||
          !methods.has(method) ||
          !params ||
          typeof params !== 'object' ||
          Array.isArray(params) ||
          JSON.stringify(params).length > 8000000
        )
          throw new Error('请求无效或应用正在退出。');
        return client.call(method, params);
      });
      ipcMain.handle('disk:folders', async (event) => {
        trusted(event);
        const result = await dialog.showOpenDialog(win, {
          title: '选择扫描或整理位置',
          properties: ['openDirectory', 'multiSelections'],
        });
        return result.canceled ? [] : result.filePaths;
      });
      ipcMain.handle('disk:reveal', (event, value) => {
        trusted(event);
        shell.showItemInFolder(validatedPath(value));
      });
      ipcMain.handle('disk:copy', (event, value) => {
        trusted(event);
        emit('notice', null);
        const copiedPath = validatedPath(value);
        clipboard.writeText(copiedPath);
        if (clipboard.readText() !== copiedPath) throw new Error('复制失败，请重试。');
        emit('notice', '路径已复制');
      });
      ipcMain.handle('disk:execute', async (event, id, kind) => {
        trusted(event);
        if (
          confirmationPending ||
          quitting ||
          !['files', 'tool'].includes(kind) ||
          typeof id !== 'string'
        )
          throw new Error('请先完成当前确认。');
        confirmationPending = true;
        try {
          const plan = await client.call(kind === 'tool' ? 'cache.plan' : 'operations.read', {
            id,
          });
          const labels = {
            recycle: '移入回收站',
            delete: '永久删除',
            move: '移动',
            rename: '重命名',
          };
          const title = kind === 'tool' ? `清理 ${plan.target.name}` : labels[plan.action];
          const details =
            kind === 'tool'
              ? `${plan.target.root}\n\n${plan.target.impact}`
              : plan.items
                  .filter((item) => !item.error)
                  .slice(0, 12)
                  .map((item) => item.path + (item.destination ? ` → ${item.destination}` : ''))
                  .join('\n') +
                `\n\n共 ${plan.items.filter((item) => !item.error).length} 个项目\n${plan.warnings.join('\n')}`;
          const choice = await dialog.showMessageBox(win, {
            type: 'warning',
            title,
            message: `确认${title}`,
            detail: details,
            buttons: ['取消', title],
            defaultId: 0,
            cancelId: 0,
            noLink: true,
          });
          if (choice.response !== 1) return null;
          return await client.call(kind === 'tool' ? 'cache.execute' : 'operations.execute', {
            id,
          });
        } finally {
          confirmationPending = false;
        }
      });
      ipcMain.handle('disk:export', async (event, kind, query) => {
        trusted(event);
        if (!['csv', 'snapshot'].includes(kind)) throw new Error('导出类型无效。');
        query = {
          ...query,
          snapshot: query.snapshot || (await client.call('overview')).current?.id,
        };
        if (!query.snapshot) throw new Error('请先扫描或打开快照。');
        const extension = kind === 'csv' ? 'csv' : 'diskmanager';
        const selected = await dialog.showSaveDialog(win, {
          title: '导出扫描结果',
          defaultPath: `DiskManager-${new Date().toISOString().slice(0, 10)}.${extension}`,
          filters: [
            { name: kind === 'csv' ? 'CSV 明细' : 'DiskManager 快照', extensions: [extension] },
          ],
        });
        if (selected.canceled) return null;
        return client.call('export.start', { destination: selected.filePath, kind, query });
      });
      ipcMain.handle('disk:import', async (event) => {
        trusted(event);
        const selected = await dialog.showOpenDialog(win, {
          title: '打开扫描快照',
          filters: [{ name: 'DiskManager 快照', extensions: ['diskmanager'] }],
          properties: ['openFile'],
        });
        if (selected.canceled) return null;
        return client.call('import.start', { source: selected.filePaths[0] });
      });
      ipcMain.handle('disk:png', async (event, image) => {
        trusted(event);
        if (
          typeof image !== 'string' ||
          !image.startsWith('data:image/png;base64,') ||
          image.length > 40000000
        )
          throw new Error('图片数据无效。');
        const selected = await dialog.showSaveDialog(win, {
          title: '导出矩形树图',
          defaultPath: 'DiskManager-treemap.png',
          filters: [{ name: 'PNG 图片', extensions: ['png'] }],
        });
        if (selected.canceled) return null;
        const bytes = Buffer.from(image.slice('data:image/png;base64,'.length), 'base64');
        await client.call('export.validate', { destination: selected.filePath });
        if (bytes.subarray(0, 8).toString('hex') !== '89504e470d0a1a0a')
          throw new Error('图片格式无效。');
        const temp = selected.filePath + `.${process.pid}.tmp`;
        try {
          await fs.writeFile(temp, bytes);
          await fs.rename(temp, selected.filePath);
        } finally {
          await fs.unlink(temp).catch(() => {});
        }
        return selected.filePath;
      });
      ipcMain.handle('disk:preferences', async (event, patch) => {
        trusted(event);
        const updated = await preferences.update(patch);
        nativeTheme.themeSource = updated.theme;
        emit('preferences', {
          ...updated,
          nativeTheme: nativeTheme.shouldUseDarkColors ? 'dark' : 'light',
        });
        return updated;
      });
      ipcMain.on('disk:window', (event, action) => {
        trusted(event);
        if (action === 'minimize') win.minimize();
        if (action === 'maximize') {
          if (win.isMaximized()) win.unmaximize();
          else win.maximize();
        }
        if (action === 'close') win.close();
        if (action === 'quit') app.quit();
      });
      nativeTheme.on('updated', () => {
        win.setBackgroundColor(colors().background);
        emit('preferences', {
          ...preferences.get(),
          nativeTheme: nativeTheme.shouldUseDarkColors ? 'dark' : 'light',
        });
      });
      try {
        tray = new Tray(nativeImage.createFromPath(path.join(root, 'assets/tray.png')));
        tray.setToolTip('DiskManager');
        trayMenu = new TrayMenu({
          restoreFocus: () => tray.focus(),
          reportError: (error) => emit('error', error.message),
          items: [
            { id: 'open', icon: 'app-window', label: '打开 DiskManager', click: restore },
            {
              id: 'tasks',
              icon: 'activity',
              label: '后台任务',
              click: () => {
                restore();
                emit('navigate', 'tasks');
              },
            },
            {},
            { id: 'quit', icon: 'power', label: '退出应用', click: () => app.quit() },
          ],
        });
        if (qaData) console.log('QA tray loading');
        trayMenu.ready.catch((error) => emit('error', error.message));
        const fallbackMenu = Menu.buildFromTemplate([
          { label: '打开 DiskManager', click: restore },
          { type: 'separator' },
          { label: '退出应用', click: () => app.quit() },
        ]);
        tray.on('click', restore);
        tray.on('double-click', restore);
        tray.on('right-click', async (_event, bounds) => {
          const shown = await trayMenu.popup(bounds).catch(() => false);
          if (!shown && !exiting && !quitting) tray.popUpContextMenu(fallbackMenu);
        });
      } catch (error) {
        emit('error', error.message);
      }
      win.on('close', (event) => {
        if (exiting) return;
        event.preventDefault();
        if (preferences.get().closeBehavior === 'tray')
          hideToTray().catch((error) => emit('error', error.message));
        else app.quit();
      });
      if (qaData) console.log('QA renderer loading');
      if (!captureQa || authorizationQaRoot)
        await client.call('scan.authorize').catch((error) => emit('error', error.message));
      await win.loadFile(path.join(root, 'dist/index.html'));
      if (qaData) console.log('QA renderer loaded');
      if (captureQa) {
        try {
          win.showInactive();
          console.log(
            await require('./qa.cjs').runWindowQa(
              win,
              client,
              qaData,
              nativeTheme,
              trayMenu,
              authorizationQaRoot,
            ),
          );
        } finally {
          await client.close();
          trayMenu?.dispose();
          tray?.destroy();
          exiting = true;
          app.quit();
        }
      } else if (!process.argv.includes('--qa-hidden')) win.show();
    })
    .catch(async (error) => {
      if (captureQa)
        await fs.writeFile(path.join(qaData, 'qa-error.log'), error.stack || error.message);
      else dialog.showErrorBox('DiskManager 启动失败', error.message);
      await client?.close().catch(() => {});
      exiting = true;
      if (captureQa) app.exit(1);
      else app.quit();
    });
  app.on('before-quit', (event) => {
    if (exiting) return;
    event.preventDefault();
    if (quitting) return;
    quitting = true;
    (async () => {
      try {
        const unsaved =
          win &&
          !win.isDestroyed() &&
          (await within(
            win.webContents.executeJavaScript(
              "!!document.querySelector('form[data-cinna-dirty=true],dialog[open]')",
            ),
            3000,
          ).catch(() => null));
        if (unsaved || unsaved === null) {
          const choice = await dialog.showMessageBox(win, {
            type: 'question',
            message:
              unsaved === null
                ? '界面没有响应，仍要保存后台结果并退出？'
                : '关闭应用并放弃当前未保存的输入？',
            detail:
              unsaved === null ? '尚未提交的界面输入可能丢失。文件操作会完成必要收尾后退出。' : '',
            buttons: ['继续编辑', '关闭应用'],
            defaultId: 0,
            cancelId: 0,
          });
          if (choice.response !== 1) {
            quitting = false;
            return;
          }
        }
        if (
          client?.child.exitCode === null &&
          jobs.some((job) => ['running', 'queued'].includes(job.state))
        ) {
          const choice = await dialog.showMessageBox(win, {
            type: 'question',
            message: '取消后台任务并退出？',
            detail: '正在执行的缓存工具会完成当前操作后退出。',
            buttons: ['继续运行', '取消任务并退出'],
            defaultId: 0,
            cancelId: 0,
          });
          if (choice.response !== 1) {
            quitting = false;
            return;
          }
        }
        await client?.close();
        trayMenu?.dispose();
        tray?.destroy();
        exiting = true;
        app.quit();
      } catch (error) {
        quitting = false;
        emit('error', error.message);
      }
    })();
  });
}
