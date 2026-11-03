const { BrowserWindow, ipcMain, nativeTheme, screen } = require('electron');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const path = require('node:path');
const { placeMenu } = require('./tray-menu-layout.cjs');
const { within } = require('./async-timeout.cjs');
const contract = require('./window-shell.json').trayMenu;
const icons = require('./tray-menu-icons.json');
const runFile = promisify(execFile);

class TrayMenu {
  constructor({ items, restoreFocus, reportError }) {
    this.items = items;
    this.restoreFocus = restoreFocus;
    this.reportError = reportError;
    this.pending = false;
    this.sequence = 0;
    this.anchor = screen.getCursorScreenPoint();
    this.window = new BrowserWindow({
      title: 'DiskManager · 托盘菜单',
      width: 1,
      height: 1,
      show: false,
      frame: false,
      transparent: true,
      resizable: false,
      movable: false,
      minimizable: false,
      maximizable: false,
      fullscreenable: false,
      skipTaskbar: true,
      alwaysOnTop: true,
      hasShadow: false,
      roundedCorners: false,
      webPreferences: {
        preload: path.join(__dirname, 'tray-menu-preload.cjs'),
        contextIsolation: true,
        sandbox: true,
        nodeIntegration: false,
        webviewTag: false,
        spellcheck: false,
        backgroundThrottling: false,
      },
    });
    this.window.setMenu(null);
    this.window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    this.window.webContents.on('will-navigate', (event) => event.preventDefault());
    this.window.on('blur', () => this.hide(false));
    this.stateHandler = async (event) => {
      if (!this.trusted(event)) throw new Error('不允许此页面读取托盘菜单');
      return this.state();
    };
    this.messageHandler = (event, action, value) => {
      if (!this.trusted(event)) return;
      if (action === 'layout') this.layout(value);
      else if (action === 'dismiss') this.hide(value === 'keyboard');
      else if (action === 'command' && this.window.isVisible()) {
        const item = this.items.find((item) => item.id === value);
        if (!item?.click) return;
        this.hide(false);
        Promise.resolve().then(item.click).catch(this.reportError);
      }
    };
    ipcMain.handle('toolbox:tray-state', this.stateHandler);
    ipcMain.on('toolbox:tray', this.messageHandler);
    this.themeChanged = () => {
      if (this.window.isVisible() || this.pending) void this.render().catch(this.reportError);
    };
    nativeTheme.on('updated', this.themeChanged);
    this.ready = this.window.loadFile(path.join(__dirname, 'tray-menu.html'));
  }

  trusted(event) {
    return (
      event.sender === this.window.webContents &&
      event.senderFrame === this.window.webContents.mainFrame
    );
  }

  async state() {
    const sequence = this.sequence;
    const display = screen.getDisplayNearestPoint(this.anchor);
    let textScale = 1;
    if (process.platform === 'win32') {
      try {
        const { stdout } = await runFile(
          'reg.exe',
          ['query', 'HKCU\\Software\\Microsoft\\Accessibility', '/v', 'TextScaleFactor'],
          { windowsHide: true, timeout: 2000 },
        );
        const match = /TextScaleFactor\s+REG_DWORD\s+0x([\da-f]+)/i.exec(stdout);
        if (match) textScale = Math.max(1, parseInt(match[1], 16) / 100);
      } catch {
        /* The registry value is absent at Windows' default text size. */
      }
    }
    return {
      sequence,
      theme: nativeTheme.shouldUseDarkColors ? 'dark' : 'light',
      highContrast: nativeTheme.shouldUseHighContrastColors,
      contract,
      icons,
      textScale,
      maximumWidth: display.workArea.width,
      maximumHeight: display.workArea.height,
      items: this.items.map(({ id, label, icon }) => ({ id, label, icon })),
    };
  }

  async render() {
    const state = await this.state();
    if (!this.window.isDestroyed() && state.sequence === this.sequence)
      this.window.webContents.send('toolbox:tray-render', state);
  }

  async popup(bounds) {
    if (this.window.isVisible() || this.pending) {
      this.hide(false);
      return true;
    }
    this.previous = BrowserWindow.getFocusedWindow();
    const cursor = screen.getCursorScreenPoint();
    // Keyboard activation may leave the pointer on another monitor.
    this.anchor =
      bounds?.width > 0 && bounds?.height > 0
        ? { x: bounds.x + Math.floor(bounds.width / 2), y: bounds.y }
        : cursor;
    this.pending = true;
    this.sequence++;
    const opened = new Promise((resolve) => { this.opened = resolve; });
    try {
      return await within((async () => {
        await this.ready;
        await this.render();
        return opened;
      })(), 1500);
    } finally {
      if (this.pending) this.hide(false);
      this.opened = null;
    }
  }

  layout(value) {
    if (!value || value.sequence !== this.sequence || (!this.pending && !this.window.isVisible()))
      return;
    if (![value.width, value.height].every((number) => Number.isFinite(number) && number > 0))
      return;
    const area = screen.getDisplayNearestPoint(this.anchor).workArea;
    this.window.setBounds(placeMenu(value, this.anchor, area));
    if (this.pending) {
      this.pending = false;
      this.window.show();
      this.window.focus();
      this.window.webContents.send('toolbox:tray-focus');
      this.opened?.(true);
    }
  }

  hide(returnFocus) {
    this.opened?.(false);
    this.pending = false;
    this.sequence++;
    if (!this.window.isDestroyed()) this.window.hide();
    if (returnFocus) {
      if (this.previous && !this.previous.isDestroyed() && this.previous.isVisible())
        this.previous.focus();
      else this.restoreFocus();
    }
  }

  dispose() {
    nativeTheme.off('updated', this.themeChanged);
    ipcMain.removeHandler('toolbox:tray-state');
    ipcMain.off('toolbox:tray', this.messageHandler);
    if (!this.window.isDestroyed()) this.window.destroy();
  }
}

module.exports = { TrayMenu };
