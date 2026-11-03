const { contextBridge, ipcRenderer } = require('electron');
const startup = ipcRenderer.sendSync('disk:startup');
contextBridge.exposeInMainWorld('diskManager', {
  startup,
  call: (method, params = {}) => ipcRenderer.invoke('disk:call', method, params),
  folders: () => ipcRenderer.invoke('disk:folders'),
  reveal: (path) => ipcRenderer.invoke('disk:reveal', path),
  copy: (path) => ipcRenderer.invoke('disk:copy', path),
  execute: (id, kind) => ipcRenderer.invoke('disk:execute', id, kind),
  export: (kind, query) => ipcRenderer.invoke('disk:export', kind, query),
  import: () => ipcRenderer.invoke('disk:import'),
  png: (image) => ipcRenderer.invoke('disk:png', image),
  preferences: (patch) => ipcRenderer.invoke('disk:preferences', patch),
  window: (action) => ipcRenderer.send('disk:window', action),
  subscribe: (callback) => {
    const listener = (_event, value) => callback(value);
    ipcRenderer.on('disk:event', listener);
    return () => ipcRenderer.removeListener('disk:event', listener);
  },
});
