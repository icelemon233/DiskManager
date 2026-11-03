const { spawn } = require('node:child_process');
const { createInterface } = require('node:readline');
const { EventEmitter } = require('node:events');
const { randomUUID } = require('node:crypto');
const path = require('node:path');

class NativeClient extends EventEmitter {
  constructor(root, directory, applicationDirectory = root) {
    super();
    this.pending = new Map();
    this.child = spawn(
      path.join(root, 'DiskManager.Native.exe'),
      ['--data-dir', directory, '--application-dir', applicationDirectory],
      {
        windowsHide: true,
        stdio: ['pipe', 'pipe', 'pipe'],
      },
    );
    let diagnostic = '';
    this.child.stderr.on('data', (chunk) => {
      diagnostic = (diagnostic + chunk).slice(-4000);
    });
    createInterface({ input: this.child.stdout }).on('line', (line) => {
      let message;
      try {
        message = JSON.parse(line.replace(/^\uFEFF/, ''));
      } catch {
        return;
      }
      if (message.event) {
        this.emit('event', { name: message.event, data: message.data });
        return;
      }
      const pending = this.pending.get(message.id);
      if (!pending) return;
      clearTimeout(pending.timer);
      this.pending.delete(message.id);
      if (message.error) pending.reject(new Error(message.error));
      else pending.resolve(message.result);
    });
    const fail = (error) => {
      for (const pending of this.pending.values()) {
        clearTimeout(pending.timer);
        pending.reject(error);
      }
      this.pending.clear();
      this.emit('offline', error.message);
    };
    this.child.on('error', fail);
    this.child.on('exit', (code) =>
      fail(new Error(diagnostic || `本机服务已关闭（${code}）。请重新打开应用。`)),
    );
  }
  call(method, params = {}) {
    if ((this.closing && method !== 'shutdown.prepare') || !this.child.stdin.writable)
      return Promise.reject(new Error('本机服务正在退出。'));
    const id = randomUUID();
    return new Promise((resolve, reject) => {
      const timer = setTimeout(
        () => {
          this.pending.delete(id);
          reject(new Error('请求超时，请查看后台任务状态。'));
        },
        method === 'shutdown.prepare' ? 600000 : 150000,
      );
      this.pending.set(id, { resolve, reject, timer });
      this.child.stdin.write(JSON.stringify({ id, method, params }) + '\n');
    });
  }
  close() {
    if (this.closePromise) return this.closePromise;
    this.closing = true;
    this.closePromise = (async () => {
      if (this.child.exitCode !== null) return;
      try {
        await this.call('shutdown.prepare');
        await new Promise((resolve, reject) => {
          this.child.once('exit', (code) =>
            code === 0 ? resolve() : reject(new Error('本机服务退出异常，请检查操作记录。')),
          );
          this.child.stdin.end();
        });
      } catch (error) {
        this.closing = false;
        this.closePromise = null;
        throw error;
      }
    })();
    return this.closePromise;
  }
}
module.exports = { NativeClient };
