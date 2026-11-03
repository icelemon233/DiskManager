const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('cinnaTray', {
  state: () => ipcRenderer.invoke('toolbox:tray-state'),
  layout: (value) => ipcRenderer.send('toolbox:tray', 'layout', value),
  dismiss: (reason) => ipcRenderer.send('toolbox:tray', 'dismiss', reason),
  command: (id) => ipcRenderer.send('toolbox:tray', 'command', id),
  onRender: (callback) => ipcRenderer.on('toolbox:tray-render', (_event, value) => callback(value)),
  onFocus: (callback) => ipcRenderer.on('toolbox:tray-focus', () => callback()),
});
