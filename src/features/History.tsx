import { useEffect, useState } from 'react';
import { ArrowRightLeft, Download, FolderOpen, Pin, RefreshCw, Trash2 } from 'lucide-react';
import { bridge, bytes, call, date } from '../shared/api';
import { Empty, Modal, Pager, VirtualList } from '../shared/components';
import type { DiffRow, Snapshot } from '../shared/types';

export function History({
  version,
  run,
  openAnalysis,
}: {
  version: number;
  run: (operation: () => Promise<unknown>) => void;
  openAnalysis: () => void;
}) {
  const [records, setRecords] = useState<Snapshot[]>([]);
  const [selected, setSelected] = useState<string[]>([]);
  const [diff, setDiff] = useState<{ count: number; rows: DiffRow[] } | null>(null);
  const [offset, setOffset] = useState(0);
  const [pair, setPair] = useState<string[] | null>(null);
  const [rename, setRename] = useState<Snapshot | null>(null);
  const [label, setLabel] = useState('');
  const [remove, setRemove] = useState<Snapshot | null>(null);
  async function refresh() {
    setRecords(await call<Snapshot[]>('history.list'));
  }
  useEffect(() => {
    run(refresh);
  }, [version]);
  useEffect(() => {
    if (!pair) return;
    let active = true;
    call<{ count: number; rows: DiffRow[] }>('history.diff', {
      older: pair[0],
      newer: pair[1],
      offset,
    })
      .then((value) => {
        if (active) setDiff(value);
      })
      .catch((error) => {
        if (active) run(() => Promise.reject(error));
      });
    return () => {
      active = false;
    };
  }, [pair, offset]);
  const labels: Record<string, string> = {
    added: '新增',
    removed: '移除',
    changed: '变化',
    moved: '移动 / 重命名',
  };
  return (
    <div className="page-body">
      <div className="page-heading">
        <h1>历史对比</h1>
        <div className="actions">
          <button onClick={() => run(() => bridge().import())}>
            <FolderOpen size={17} />
            打开快照
          </button>
          <button onClick={() => run(refresh)}>
            <RefreshCw size={17} />
            刷新
          </button>
          <button
            className="primary"
            disabled={selected.length !== 2}
            onClick={() => {
              const ordered = records
                .filter((record) => selected.includes(record.id))
                .sort((a, b) => a.created - b.created);
              setPair(ordered.map((record) => record.id));
              setOffset(0);
            }}
          >
            <ArrowRightLeft size={17} />
            比较所选
          </button>
        </div>
      </div>
      <div className="history-table panel">
        <div className="history-row table-head">
          <span />
          <span>扫描记录</span>
          <span>文件数</span>
          <span>实际占用</span>
          <span>操作</span>
        </div>
        <VirtualList
          items={records}
          rowHeight={72}
          label="历史扫描记录"
          render={(record) => (
            <div className="history-row">
              <input
                type="checkbox"
                aria-label={`比较 ${record.label}`}
                checked={selected.includes(record.id)}
                onChange={(e) =>
                  setSelected((value) =>
                    e.target.checked
                      ? [...value.slice(-1), record.id]
                      : value.filter((id) => id !== record.id),
                  )
                }
              />
              <div className="record-name">
                <button
                  className="text-button ellipsis"
                  title={record.label}
                  onClick={() => {
                    setRename(record);
                    setLabel(record.label);
                  }}
                >
                  {record.pinned && <Pin size={14} />}
                  {record.label}
                </button>
                <small>
                  {date(record.created)}
                  {record.imported && ' · 导入快照'}
                </small>
              </div>
              <span className="number">{record.count.toLocaleString()}</span>
              <span className="number">{bytes(record.allocated)}</span>
              <div className="actions compact">
                <button
                  className="icon-button"
                  aria-label={`查看 ${record.label}`}
                  title="查看"
                  onClick={() =>
                    run(async () => {
                      await call('history.open', { id: record.id });
                      openAnalysis();
                    })
                  }
                >
                  <FolderOpen size={16} />
                </button>
                <button
                  className="icon-button"
                  aria-label="导出快照"
                  title="导出快照"
                  onClick={() => run(() => bridge().export('snapshot', { snapshot: record.id }))}
                >
                  <Download size={16} />
                </button>
                <button
                  className={`icon-button ${record.pinned ? 'active' : ''}`}
                  aria-label={record.pinned ? '取消固定' : '固定记录'}
                  title={record.pinned ? '取消固定' : '固定记录'}
                  onClick={() =>
                    run(async () => {
                      await call('history.update', { id: record.id, pinned: !record.pinned });
                      await refresh();
                    })
                  }
                >
                  <Pin size={16} />
                </button>
                <button
                  className="icon-button danger-text"
                  aria-label="移除历史记录"
                  title="移除历史记录"
                  onClick={() => setRemove(record)}
                >
                  <Trash2 size={16} />
                </button>
              </div>
            </div>
          )}
        />
        {records.length === 0 && <Empty>完整扫描后自动保存记录</Empty>}
      </div>
      {pair && (
        <section className="diff-section panel">
          <div className="panel-heading">
            <h2>差异</h2>
            <span
              title={`${records.find((record) => record.id === pair[0])?.label ?? ''} → ${records.find((record) => record.id === pair[1])?.label ?? ''}`}
            >
              {date(records.find((record) => record.id === pair[0])?.created ?? 0)} →{' '}
              {date(records.find((record) => record.id === pair[1])?.created ?? 0)}
            </span>
          </div>
          <div className="diff-row table-head">
            <span>变化</span>
            <span>路径</span>
            <span>占用变化</span>
          </div>
          <VirtualList
            items={diff?.rows ?? []}
            rowHeight={60}
            label="扫描差异"
            render={(row) => (
              <div className="diff-row">
                <span className={`status-label ${row.kind}`}>{labels[row.kind]}</span>
                <div className="record-name">
                  <span className="path" title={row.path}>
                    {row.path}
                  </span>
                  {row.oldPath && row.oldPath !== row.path && (
                    <small className="path" title={row.oldPath}>
                      原路径 {row.oldPath}
                    </small>
                  )}
                </div>
                <span className={`number ${row.delta > 0 ? 'growth' : ''}`}>
                  {row.delta > 0 ? '+' : ''}
                  {bytes(row.delta)}
                </span>
              </div>
            )}
          />
          {diff?.count === 0 && <Empty>未发现差异</Empty>}
          <Pager count={diff?.count ?? 0} offset={offset} onChange={setOffset} />
        </section>
      )}
      {rename && (
        <Modal title="记录名称" onClose={() => setRename(null)}>
          <form
            onSubmit={(e) => {
              e.preventDefault();
              run(async () => {
                await call('history.update', { id: rename.id, label });
                await refresh();
              });
              setRename(null);
            }}
          >
            <label className="field">
              <input
                aria-label="记录名称"
                autoFocus
                required
                maxLength={200}
                value={label}
                onChange={(e) => setLabel(e.target.value)}
              />
            </label>
            <div className="modal-actions">
              <button type="button" onClick={() => setRename(null)}>
                取消
              </button>
              <button type="submit" className="primary">
                保存
              </button>
            </div>
          </form>
        </Modal>
      )}
      {remove && (
        <Modal title="移除历史记录" onClose={() => setRemove(null)}>
          <p>
            {remove.label} · {date(remove.created)}
          </p>
          <p>将删除这份本地快照。</p>
          <div className="modal-actions">
            <button onClick={() => setRemove(null)}>取消</button>
            <button
              className="danger"
              onClick={() => {
                run(async () => {
                  await call('history.update', { id: remove.id, delete: true });
                  await refresh();
                });
                setRemove(null);
              }}
            >
              移除记录
            </button>
          </div>
        </Modal>
      )}
    </div>
  );
}
