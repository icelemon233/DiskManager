import { useEffect, useState } from 'react';
import { CheckCheck, Copy, FolderOpen, Play, Trash2 } from 'lucide-react';
import { bridge, bytes, call } from '../shared/api';
import { Empty, Pager } from '../shared/components';
import type { DuplicateResult, Overview } from '../shared/types';

export function DuplicateFiles({
  overview,
  version,
  run,
  busy,
}: {
  overview: Overview;
  version: number;
  run: (operation: () => Promise<unknown>) => void;
  busy: boolean;
}) {
  const [result, setResult] = useState<DuplicateResult>({
    session: '',
    count: 0,
    rows: [],
    skipped: [],
  });
  const [offset, setOffset] = useState(0);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [online, setOnline] = useState(false);
  const [estimate, setEstimate] = useState({ files: 0, download: 0 });
  useEffect(() => {
    run(async () => {
      const value = await call<DuplicateResult>('duplicates.list', { offset });
      setResult(value);
      setSelected(new Set());
    });
  }, [version, offset]);
  useEffect(() => {
    if (overview.current) run(async () => setEstimate(await call('duplicates.estimate')));
  }, [overview.current?.id]);
  function suggest() {
    setSelected(
      new Set(
        result.rows.flatMap((group) =>
          group.files.filter((file) => file.path !== group.suggestedKeep).map((file) => file.path),
        ),
      ),
    );
  }
  return (
    <div className="page-body">
      <div className="page-heading">
        <h1>重复文件</h1>
        <button
          className="primary"
          disabled={!overview.current || busy || overview.current.imported}
          onClick={() => run(() => call('duplicates.start', { downloadOnline: online }))}
        >
          <Play size={17} />
          校验内容
        </button>
      </div>
      {overview.current && (
        <div className="inline-summary">
          <span>{overview.current.roots.join('、')}</span>
        </div>
      )}
      {estimate.files > 0 && (
        <label className="check-label notice">
          <input type="checkbox" checked={online} onChange={(e) => setOnline(e.target.checked)} />
          下载并校验 {estimate.files.toLocaleString()} 个在线文件，预计下载{' '}
          {bytes(estimate.download)}
        </label>
      )}
      <div className="selection-bar">
        <span>
          {result.count.toLocaleString()} 个重复组
          {selected.size > 0 && ` · 已选 ${selected.size} 项`}
        </span>
        <button disabled={result.rows.length === 0} onClick={suggest}>
          <CheckCheck size={17} />
          按建议选择本页
        </button>
        <button disabled={!selected.size} onClick={() => setSelected(new Set())}>
          清空选择
        </button>
        <button
          disabled={!selected.size || busy}
          onClick={() =>
            run(() =>
              call('operations.plan', {
                action: 'recycle',
                paths: [...selected],
                duplicateSelection: true,
              }),
            )
          }
        >
          <Trash2 size={17} />
          移入回收站
        </button>
        <button
          className="danger-text"
          disabled={!selected.size || busy}
          onClick={() =>
            run(() =>
              call('operations.plan', {
                action: 'delete',
                paths: [...selected],
                duplicateSelection: true,
              }),
            )
          }
        >
          永久删除
        </button>
      </div>
      <div className="duplicate-groups">
        {result.rows.map((group) => (
          <section className="duplicate-group panel" key={group.id}>
            <header>
              <strong>
                {group.files.length} 份 · {bytes(group.size)}
              </strong>
              <span>可清理 {bytes(group.recoverable)}</span>
            </header>
            {group.files.map((file) => (
              <div className="duplicate-file" key={file.path}>
                <input
                  type="checkbox"
                  aria-label={`选择 ${file.path}`}
                  checked={selected.has(file.path)}
                  onChange={(e) =>
                    setSelected((value) => {
                      const next = new Set(value);
                      if (e.target.checked) next.add(file.path);
                      else next.delete(file.path);
                      return next;
                    })
                  }
                />
                <div className="record-name">
                  <span className="path" title={file.path}>
                    {file.path}
                  </span>
                  {(file.path === group.suggestedKeep || file.links > 1) && (
                    <small>
                      {[
                        file.path === group.suggestedKeep ? '建议保留' : '',
                        file.links > 1 ? `${file.links} 个硬链接` : '',
                      ]
                        .filter(Boolean)
                        .join(' · ')}
                    </small>
                  )}
                </div>
                <span className="number">{bytes(file.allocated)}</span>
                <button
                  className="icon-button"
                  aria-label="定位文件"
                  title="定位文件"
                  onClick={() => run(() => bridge().reveal(file.path))}
                >
                  <FolderOpen size={16} />
                </button>
                <button
                  className="icon-button"
                  aria-label="复制路径"
                  title="复制路径"
                  onClick={() => run(() => bridge().copy(file.path))}
                >
                  <Copy size={16} />
                </button>
              </div>
            ))}
          </section>
        ))}
        {result.count === 0 && (
          <Empty>
            {!overview.current
              ? '先扫描磁盘或文件夹'
              : overview.current.imported
                ? '重新扫描后可校验内容'
                : result.session
                  ? '未发现重复文件'
                  : '未开始校验'}
          </Empty>
        )}
      </div>
      <Pager offset={offset} count={result.count} onChange={setOffset} size={50} />
      {result.skipped.length > 0 && (
        <details className="issues">
          <summary>跳过的项目 ({result.skipped.length})</summary>
          {result.skipped.map((line, index) => (
            <p className="path" key={index}>
              {line}
            </p>
          ))}
        </details>
      )}
    </div>
  );
}
