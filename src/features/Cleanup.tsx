import { useEffect, useState } from 'react';
import { FolderPlus, Plus, RefreshCw, Search, Trash2 } from 'lucide-react';
import { bridge, call } from '../shared/api';
import { Empty, Modal } from '../shared/components';
import type { CacheTarget, Overview, Rule } from '../shared/types';

export function Cleanup({
  active,
  overview,
  run,
  refresh,
  busy,
}: {
  active: boolean;
  overview: Overview;
  run: (operation: () => Promise<unknown>) => void;
  refresh: () => void;
  busy: boolean;
}) {
  const [targets, setTargets] = useState<CacheTarget[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [loading, setLoading] = useState(false);
  const [advanced, setAdvanced] = useState(false);
  const [tab, setTab] = useState<'cache' | 'rules'>('cache');
  const [edit, setEdit] = useState<Rule | null>(null);
  async function discover() {
    setLoading(true);
    try {
      setTargets(await call('cache.list'));
      setLoaded(true);
    } finally {
      setLoading(false);
    }
  }
  useEffect(() => {
    if (active && !loaded && !loading) run(discover);
  }, [active]);
  const labels: Record<string, string> = {
    files: '临时与浏览器缓存',
    download: '下载缓存',
    prune: '未使用缓存',
    shared: '共享依赖',
  };
  return (
    <div className="page-body">
      <div className="page-heading">
        <h1>清理</h1>
        <div className="actions">
          {tab === 'cache' ? (
            <button disabled={loading} onClick={() => run(discover)}>
              <RefreshCw size={17} />
              刷新
            </button>
          ) : (
            <button
              className="primary"
              onClick={() =>
                setEdit({
                  id: '',
                  name: '',
                  roots: [],
                  pattern: '*',
                  extension: '',
                  minSize: 0,
                  maxSize: null,
                  olderDays: null,
                  exclusions: [],
                })
              }
            >
              <Plus size={17} />
              新建规则
            </button>
          )}
        </div>
      </div>
      <div className="tabs">
        <button className={tab === 'cache' ? 'selected' : ''} onClick={() => setTab('cache')}>
          缓存
        </button>
        <button className={tab === 'rules' ? 'selected' : ''} onClick={() => setTab('rules')}>
          整理规则
        </button>
      </div>
      {tab === 'cache' ? (
        <>
          <label className="check-label">
            <input
              type="checkbox"
              checked={advanced}
              onChange={(e) => setAdvanced(e.target.checked)}
            />
            显示共享依赖
          </label>
          {loading && <p role="status">正在查找缓存…</p>}
          <div className="cache-list">
            {Object.entries(labels)
              .filter(([kind]) => kind !== 'shared' || advanced)
              .map(([kind, label]) => {
                const items = targets.filter((target) => target.kind === kind);
                return items.length > 0 ? (
                  <section key={kind}>
                    <h2>{label}</h2>
                    {items.map((target) => (
                      <div className="cache-row" key={target.id}>
                        <div className="record-name">
                          <strong>{target.name}</strong>
                          <span className="path" title={target.root}>
                            {target.root}
                          </span>
                        </div>
                        <button
                          disabled={busy}
                          onClick={() => run(() => call('cache.preview', { id: target.id }))}
                        >
                          <Search size={16} />
                          预览
                        </button>
                      </div>
                    ))}
                  </section>
                ) : null;
              })}
          </div>
          {loaded && targets.length === 0 && <Empty>未发现缓存</Empty>}
        </>
      ) : (
        <div className="rule-list">
          {overview.rules.map((rule) => (
            <div className="rule-row panel" key={rule.id}>
              <div className="record-name">
                <button className="text-button" onClick={() => setEdit(rule)}>
                  {rule.name}
                </button>
                <span className="path" title={rule.roots.join('、')}>
                  {rule.roots.join('、')}
                </span>
                <small>
                  {rule.pattern}
                  {rule.extension && ` · ${rule.extension}`}
                  {rule.olderDays != null && ` · ${rule.olderDays} 天以前`}
                </small>
              </div>
              <button
                disabled={busy}
                onClick={() => run(() => call('rules.preview', { id: rule.id }))}
              >
                <Search size={16} />
                预览
              </button>
              <button
                className="icon-button"
                aria-label={`删除规则 ${rule.name}`}
                onClick={() =>
                  run(async () => {
                    await call('rules.delete', { id: rule.id });
                    refresh();
                  })
                }
              >
                <Trash2 size={16} />
              </button>
            </div>
          ))}
          {overview.rules.length === 0 && <Empty>暂无规则</Empty>}
        </div>
      )}
      {edit && (
        <Modal title={edit.id ? '编辑规则' : '新建规则'} onClose={() => setEdit(null)} wide>
          <form
            className="field-stack"
            onSubmit={(e) => {
              e.preventDefault();
              run(async () => {
                await call('rules.save', edit);
                refresh();
                setEdit(null);
              });
            }}
            data-cinna-dirty="true"
          >
            <label className="field">
              规则名称
              <input
                required
                maxLength={200}
                value={edit.name}
                onChange={(e) => setEdit({ ...edit, name: e.target.value })}
              />
            </label>
            <label className="field">
              目录，每行一个
              <textarea
                required
                rows={3}
                value={edit.roots.join('\n')}
                onChange={(e) => setEdit({ ...edit, roots: e.target.value.split('\n') })}
              />
            </label>
            <button
              type="button"
              onClick={() =>
                run(async () => {
                  const roots = await bridge().folders();
                  setEdit(
                    (value) =>
                      value && {
                        ...value,
                        roots: [...new Set([...value.roots.filter(Boolean), ...roots])],
                      },
                  );
                })
              }
            >
              <FolderPlus size={17} />
              添加目录
            </button>
            <div className="form-grid">
              <label className="field">
                文件名通配符
                <input
                  value={edit.pattern}
                  onChange={(e) => setEdit({ ...edit, pattern: e.target.value })}
                  required
                  placeholder="*.zip"
                />
              </label>
              <label className="field">
                扩展名
                <input
                  value={edit.extension}
                  onChange={(e) => setEdit({ ...edit, extension: e.target.value })}
                  placeholder=".zip"
                />
              </label>
              <label className="field">
                至少 MiB
                <input
                  type="number"
                  min="0"
                  step="any"
                  value={edit.minSize / 1024 ** 2}
                  onChange={(e) =>
                    setEdit({ ...edit, minSize: Math.round(Number(e.target.value) * 1024 ** 2) })
                  }
                />
              </label>
              <label className="field">
                至多 MiB
                <input
                  type="number"
                  min="0"
                  step="any"
                  value={edit.maxSize == null ? '' : edit.maxSize / 1024 ** 2}
                  onChange={(e) =>
                    setEdit({
                      ...edit,
                      maxSize: e.target.value
                        ? Math.round(Number(e.target.value) * 1024 ** 2)
                        : null,
                    })
                  }
                />
              </label>
              <label className="field">
                修改距今至少（天）
                <input
                  type="number"
                  min="0"
                  value={edit.olderDays ?? ''}
                  onChange={(e) =>
                    setEdit({ ...edit, olderDays: e.target.value ? Number(e.target.value) : null })
                  }
                />
              </label>
            </div>
            <label className="field">
              排除目录，每行一个
              <textarea
                rows={2}
                value={edit.exclusions.join('\n')}
                onChange={(e) =>
                  setEdit({ ...edit, exclusions: e.target.value.split('\n').filter(Boolean) })
                }
              />
            </label>
            <div className="modal-actions">
              <button type="button" onClick={() => setEdit(null)}>
                取消
              </button>
              <button type="submit" className="primary">
                保存
              </button>
            </div>
          </form>
        </Modal>
      )}
    </div>
  );
}
