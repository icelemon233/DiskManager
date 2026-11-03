import type { Bridge } from './types';
export function bridge(): Bridge {
  if (!window.diskManager) throw new Error('请从 DiskManager 桌面应用打开，本机服务连接不可用。');
  return window.diskManager;
}
export async function call<T>(method: string, params: object = {}): Promise<T> {
  return (await bridge().call(method, params)) as T;
}
export function bytes(value: number): string {
  if (!Number.isFinite(value)) return '—';
  const magnitude = Math.abs(value);
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB'];
  const index = magnitude < 1 ? 0 : Math.min(5, Math.floor(Math.log(magnitude) / Math.log(1024)));
  return `${(value / 1024 ** index).toLocaleString('zh-CN', { maximumFractionDigits: index ? 2 : 0 })} ${units[index]}`;
}
export function date(value: number): string {
  return value > 0 ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—';
}
export function filename(value: string): string {
  return value.split('\\').filter(Boolean).at(-1) || value;
}
export const actionLabels: Record<string, string> = {
  recycle: '移入回收站',
  delete: '永久删除',
  move: '移动',
  rename: '重命名',
  cache: '清理缓存',
};
export const jobLabels: Record<string, string> = {
  authorization: '管理员授权',
  'snapshot-save': '保存分析记录',
  scan: '扫描',
  refresh: '局部刷新',
  duplicates: '内容查重',
  plan: '生成操作清单',
  'cache-preview': '分析缓存',
  cache: '清理缓存',
  operations: '文件操作',
  export: '导出',
  import: '导入',
};
export function message(error: unknown): string {
  return error instanceof Error
    ? error.message.replace(/^Error invoking remote method '[^']+': Error: /, '')
    : String(error);
}
