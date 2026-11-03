import { useEffect, useState } from 'react';
import { bytes, call } from './api';
import { Empty, Modal, Pager, VirtualList } from './components';

interface TypeRow {
  extension: string;
  count: number;
  size: number;
  allocated: number;
}
export function TypesDialog({
  scope,
  close,
  select,
  run,
}: {
  scope: string | null;
  close: () => void;
  select: (extension: string) => void;
  run: (operation: () => Promise<unknown>) => void;
}) {
  const [offset, setOffset] = useState(0);
  const [loading, setLoading] = useState(true);
  const [result, setResult] = useState<{ count: number; rows: TypeRow[] }>({ count: 0, rows: [] });
  useEffect(() => {
    let active = true;
    setLoading(true);
    run(async () => {
      try {
        const value = await call<{ count: number; rows: TypeRow[] }>('types', { scope, offset });
        if (active) setResult(value);
      } finally {
        if (active) setLoading(false);
      }
    });
    return () => {
      active = false;
    };
  }, [scope, offset, run]);
  return (
    <Modal title="文件类型" onClose={close} wide>
      {scope && <p className="path">{scope}</p>}
      <div className="type-stat-row table-head">
        <span>类型</span>
        <span>文件数</span>
        <span>文件大小</span>
        <span>计入占用</span>
      </div>
      <VirtualList
        items={result.rows}
        rowHeight={40}
        className="type-stat-list"
        label="文件类型统计"
        render={(row) => (
          <button
            className="type-stat-row"
            onClick={() => {
              select(row.extension || '<none>');
              close();
            }}
          >
            <span>{row.extension || '无扩展名'}</span>
            <span className="number">{row.count.toLocaleString()}</span>
            <span className="number">{bytes(row.size)}</span>
            <span className="number">{bytes(row.allocated)}</span>
          </button>
        )}
      />
      {loading && <p role="status">正在加载…</p>}
      {!loading && result.count === 0 && <Empty>暂无数据</Empty>}
      <Pager count={result.count} offset={offset} onChange={setOffset} />
    </Modal>
  );
}
