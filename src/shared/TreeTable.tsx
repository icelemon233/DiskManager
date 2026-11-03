import { useEffect, useMemo, useRef, useState, type Dispatch, type SetStateAction } from 'react';
import { ChevronDown, ChevronRight, Columns3, File, Folder } from 'lucide-react';
import { bytes, call, date } from './api';
import { Empty, Modal, Pager } from './components';
import type { Entry, Query, QueryResult } from './types';

interface TreeRow {
  entry: Entry;
  depth: number;
  parentSize: number;
  parentAllocated: number;
}
const columns = [
  { key: 'name', title: '名称', width: 290 },
  { key: 'percent', title: '父级占比', width: 112 },
  { key: 'allocated', title: '计入占用', width: 116 },
  { key: 'size', title: '文件大小', width: 116 },
  { key: 'count', title: '文件', width: 86 },
  { key: 'folders', title: '文件夹', width: 78 },
  { key: 'modified', title: '修改时间', width: 168 },
  { key: 'attributes', title: '属性', width: 130 },
];
function attributes(entry: Entry) {
  const value = entry.file.attributes;
  return [
    [1, '只读'],
    [2, '隐藏'],
    [4, '系统'],
    [0x800, '压缩'],
    [0x200, '稀疏'],
  ]
    .filter(([mask]) => (value & Number(mask)) !== 0)
    .map(([, label]) => label)
    .concat(entry.file.links > 1 ? ['硬链接'] : [], entry.file.onlineOnly ? ['云端'] : [])
    .join(' · ');
}
function savedColumns() {
  try {
    const value = JSON.parse(localStorage.getItem('disk-table-columns') || 'null');
    if (value && Array.isArray(value.visible) && value.widths && typeof value.widths === 'object')
      return value as { visible: string[]; widths: Record<string, number> };
  } catch {
    /* Use readable defaults when a preference cannot be decoded. */
  }
  return {
    visible: columns
      .filter((column) => !['modified', 'attributes'].includes(column.key))
      .map((column) => column.key),
    widths: {} as Record<string, number>,
  };
}

export function TreeTable({
  roots,
  indexId,
  revision,
  query,
  filesMode,
  changeMode,
  metric,
  totals,
  selected,
  setSelected,
  onSelect,
  onOpen,
  onSort,
  run,
}: {
  roots: string[];
  indexId: string;
  revision: number;
  query: Query;
  filesMode: boolean;
  changeMode: (files: boolean) => void;
  metric: 'allocated' | 'size';
  totals: { size: number; allocated: number };
  selected: Set<string>;
  setSelected: Dispatch<SetStateAction<Set<string>>>;
  onSelect: (entry: Entry) => void;
  onOpen: (entry: Entry) => void;
  onSort: (column: string) => void;
  run: (operation: () => Promise<unknown>) => void;
}) {
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set(roots));
  const [offset, setOffset] = useState(0);
  const [result, setResult] = useState<{ count: number; rows: TreeRow[] }>({ count: 0, rows: [] });
  const [loading, setLoading] = useState(false);
  const [settings, setSettings] = useState(savedColumns);
  const [chooser, setChooser] = useState(false);
  const [viewport, setViewport] = useState({ top: 0, height: 320 });
  const container = useRef<HTMLDivElement>(null);
  const drag = useRef<{ key: string; x: number; width: number } | null>(null);
  const rootKey = roots.join('|');
  const queryKey = JSON.stringify(
    filesMode ? query : { sort: query.sort, descending: query.descending },
  );
  const expandKey = [...expanded].sort().join('|');
  useEffect(() => {
    setExpanded(new Set(roots));
  }, [rootKey]);
  useEffect(() => {
    setOffset(0);
    if (container.current) container.current.scrollTop = 0;
  }, [indexId, filesMode, queryKey]);
  useEffect(() => {
    localStorage.setItem('disk-table-columns', JSON.stringify(settings));
  }, [settings]);
  useEffect(() => {
    let active = true;
    setLoading(true);
    run(async () => {
      try {
        if (filesMode) {
          const value = await call<QueryResult>('query', {
            ...query,
            parent: null,
            filesOnly: true,
            offset,
            limit: 200,
          });
          if (active)
            setResult({
              count: value.count,
              rows: value.rows.map((entry) => ({
                entry,
                depth: 0,
                parentSize: totals.size,
                parentAllocated: totals.allocated,
              })),
            });
        } else {
          const value = await call<{ count: number; rows: TreeRow[] }>('tree', {
            expanded: [...expanded],
            sort: query.sort,
            descending: query.descending,
            offset,
            limit: 200,
          });
          if (active) setResult(value);
        }
      } finally {
        if (active) setLoading(false);
      }
    });
    return () => {
      active = false;
    };
  }, [
    indexId,
    revision,
    filesMode,
    queryKey,
    expandKey,
    offset,
    totals.size,
    totals.allocated,
    run,
  ]);
  useEffect(() => {
    const element = container.current;
    if (!element) return;
    const observer = new ResizeObserver(() =>
      setViewport((value) => ({ ...value, height: element.clientHeight })),
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  const visible = useMemo(
    () =>
      columns.filter((column) => column.key === 'name' || settings.visible.includes(column.key)),
    [settings.visible],
  );
  const width = (key: string, fallback: number) =>
    Math.min(700, Math.max(key === 'name' ? 150 : 64, Number(settings.widths[key]) || fallback));
  const grid = `28px ${visible.map((column) => `${width(column.key, column.width)}px`).join(' ')}`;
  const fullWidth = 28 + visible.reduce((sum, column) => sum + width(column.key, column.width), 0);
  const start = Math.max(0, Math.floor((viewport.top - 36) / 34) - 4);
  const end = Math.min(result.rows.length, start + Math.ceil(viewport.height / 34) + 10);
  function toggle(path: string) {
    setExpanded((value) => {
      const next = new Set(value);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });
  }
  function keyboard(event: React.KeyboardEvent, position: number) {
    const row = result.rows[position];
    if (
      event.key === 'ArrowRight' &&
      row.entry.file.directory &&
      !expanded.has(row.entry.file.path)
    ) {
      event.preventDefault();
      toggle(row.entry.file.path);
    }
    if (event.key === 'ArrowLeft' && expanded.has(row.entry.file.path)) {
      event.preventDefault();
      toggle(row.entry.file.path);
    }
    if (event.key === 'Enter') onOpen(row.entry);
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const next = Math.max(
        0,
        Math.min(result.rows.length - 1, position + (event.key === 'ArrowDown' ? 1 : -1)),
      );
      onSelect(result.rows[next].entry);
      const element = container.current;
      if (
        element &&
        (next * 34 < element.scrollTop ||
          (next + 2) * 34 > element.scrollTop + element.clientHeight)
      )
        element.scrollTop = Math.max(0, next * 34 - element.clientHeight / 2);
      requestAnimationFrame(() =>
        container.current?.querySelector<HTMLElement>(`[data-position="${next}"]`)?.focus(),
      );
    }
  }
  function cell(row: TreeRow, key: string) {
    const entry = row.entry;
    const file = entry.file;
    if (key === 'name')
      return (
        <div className="tree-cell" style={{ paddingLeft: row.depth * 16 }}>
          {!filesMode && file.directory ? (
            <button
              className="tree-toggle"
              tabIndex={-1}
              aria-label={`${expanded.has(file.path) ? '收起' : '展开'} ${file.name}`}
              aria-expanded={expanded.has(file.path)}
              onClick={(event) => {
                event.stopPropagation();
                toggle(file.path);
              }}
            >
              {expanded.has(file.path) ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
            </button>
          ) : (
            <span className="tree-toggle-spacer" />
          )}
          {file.directory ? <Folder size={16} /> : <File size={16} />}
          <span className="ellipsis" title={file.path}>
            {file.name}
          </span>
        </div>
      );
    if (key === 'percent') {
      const denominator = metric === 'allocated' ? row.parentAllocated : row.parentSize;
      const value = denominator
        ? ((metric === 'allocated' ? entry.totalAllocated : entry.totalSize) / denominator) * 100
        : 0;
      return (
        <div className="percent-cell">
          <i style={{ width: `${Math.max(0, Math.min(100, value))}%` }} />
          <span>{value.toFixed(1)}%</span>
        </div>
      );
    }
    if (key === 'allocated')
      return <span className="number">{file.issue ? '—' : bytes(entry.totalAllocated)}</span>;
    if (key === 'size') return <span className="number">{bytes(entry.totalSize)}</span>;
    if (key === 'count') return <span className="number">{entry.fileCount.toLocaleString()}</span>;
    if (key === 'folders')
      return <span className="number">{entry.folderCount?.toLocaleString() ?? '—'}</span>;
    if (key === 'modified') return <span className="cell-date">{date(file.modified)}</span>;
    return (
      <span className="ellipsis" title={attributes(entry)}>
        {attributes(entry)}
      </span>
    );
  }
  return (
    <div className="tree-table" aria-busy={loading}>
      <div className="tree-table-toolbar">
        <div className="tabs">
          <button className={!filesMode ? 'selected' : ''} onClick={() => changeMode(false)}>
            目录树
          </button>
          <button className={filesMode ? 'selected' : ''} onClick={() => changeMode(true)}>
            文件排行
          </button>
        </div>
        <button
          className="icon-button"
          title="选择显示列"
          aria-label="选择显示列"
          onClick={() => setChooser(true)}
        >
          <Columns3 size={16} />
        </button>
      </div>
      <div
        className="tree-table-scroll"
        ref={container}
        onScroll={(event) =>
          setViewport({
            top: event.currentTarget.scrollTop,
            height: event.currentTarget.clientHeight,
          })
        }
      >
        <div
          style={{ width: fullWidth, minWidth: '100%' }}
          role="treegrid"
          aria-label={filesMode ? '文件排行' : '目录统计'}
          aria-rowcount={result.count + 1}
        >
          <div
            className="tree-grid-row tree-grid-header"
            role="row"
            style={{ gridTemplateColumns: grid }}
          >
            <span role="columnheader">
              <input
                type="checkbox"
                aria-label="选择当前页"
                checked={
                  result.rows.length > 0 &&
                  result.rows.every((row) => selected.has(row.entry.file.path))
                }
                onChange={(event) =>
                  setSelected(
                    event.target.checked
                      ? new Set(result.rows.map((row) => row.entry.file.path))
                      : new Set(),
                  )
                }
              />
            </span>
            {visible.map((column) => (
              <div
                role="columnheader"
                className="tree-column"
                key={column.key}
                aria-sort={
                  query.sort === column.key
                    ? query.descending
                      ? 'descending'
                      : 'ascending'
                    : 'none'
                }
              >
                <button
                  onClick={() =>
                    !['percent', 'attributes'].includes(column.key) && onSort(column.key)
                  }
                >
                  {column.key === 'percent' && filesMode ? '范围占比' : column.title}
                  {query.sort === column.key && (
                    <ChevronDown
                      size={12}
                      style={{ transform: query.descending ? undefined : 'rotate(180deg)' }}
                    />
                  )}
                </button>
                <span
                  className="column-resizer"
                  role="separator"
                  aria-label={`调整${column.title}列宽`}
                  aria-orientation="vertical"
                  tabIndex={0}
                  onKeyDown={(event) => {
                    if (['ArrowLeft', 'ArrowRight'].includes(event.key)) {
                      event.preventDefault();
                      setSettings((value) => ({
                        ...value,
                        widths: {
                          ...value.widths,
                          [column.key]:
                            width(column.key, column.width) +
                            (event.key === 'ArrowLeft' ? -12 : 12),
                        },
                      }));
                    }
                  }}
                  onPointerDown={(event) => {
                    drag.current = {
                      key: column.key,
                      x: event.clientX,
                      width: width(column.key, column.width),
                    };
                    event.currentTarget.setPointerCapture(event.pointerId);
                  }}
                  onPointerMove={(event) => {
                    if (drag.current) {
                      const value = drag.current;
                      setSettings((previous) => ({
                        ...previous,
                        widths: {
                          ...previous.widths,
                          [value.key]: Math.max(64, value.width + event.clientX - value.x),
                        },
                      }));
                    }
                  }}
                  onPointerUp={() => {
                    drag.current = null;
                  }}
                  onLostPointerCapture={() => {
                    drag.current = null;
                  }}
                />
              </div>
            ))}
          </div>
          <div style={{ height: result.rows.length * 34, position: 'relative' }}>
            <div style={{ position: 'absolute', top: start * 34, left: 0, right: 0 }}>
              {result.rows.slice(start, end).map((row, index) => (
                <div
                  key={row.entry.file.path}
                  data-position={start + index}
                  role="row"
                  aria-level={row.depth + 1}
                  aria-rowindex={offset + start + index + 2}
                  aria-selected={selected.has(row.entry.file.path)}
                  aria-expanded={
                    row.entry.file.directory && !filesMode
                      ? expanded.has(row.entry.file.path)
                      : undefined
                  }
                  className={`tree-grid-row ${selected.has(row.entry.file.path) ? 'selected' : ''} ${row.entry.file.attributes & 4 ? 'system-entry' : ''}`}
                  style={{ gridTemplateColumns: grid }}
                  tabIndex={selected.has(row.entry.file.path) || start + index === 0 ? 0 : -1}
                  onClick={() => onSelect(row.entry)}
                  onDoubleClick={() => {
                    if (row.entry.file.directory && !expanded.has(row.entry.file.path))
                      toggle(row.entry.file.path);
                    onOpen(row.entry);
                  }}
                  onKeyDown={(event) => keyboard(event, start + index)}
                >
                  <span role="gridcell">
                    <input
                      type="checkbox"
                      aria-label={`选择 ${row.entry.file.name}`}
                      checked={selected.has(row.entry.file.path)}
                      onClick={(event) => event.stopPropagation()}
                      onChange={(event) =>
                        setSelected((value) => {
                          const next = new Set(value);
                          if (event.target.checked) next.add(row.entry.file.path);
                          else next.delete(row.entry.file.path);
                          return next;
                        })
                      }
                    />
                  </span>
                  {visible.map((column) => (
                    <div role="gridcell" key={column.key}>
                      {cell(row, column.key)}
                    </div>
                  ))}
                </div>
              ))}
            </div>
          </div>
        </div>
        {!loading && result.count === 0 && <Empty>没有匹配的项目</Empty>}
      </div>
      <Pager
        count={result.count}
        offset={offset}
        onChange={(value) => {
          setOffset(value);
          if (container.current) container.current.scrollTop = 0;
        }}
      />
      {chooser && (
        <Modal title="表格列" onClose={() => setChooser(false)}>
          <div className="field-stack">
            {columns.map((column) => (
              <label className="check-label" key={column.key}>
                <input
                  type="checkbox"
                  disabled={column.key === 'name'}
                  checked={column.key === 'name' || settings.visible.includes(column.key)}
                  onChange={(event) =>
                    setSettings((value) => ({
                      ...value,
                      visible: event.target.checked
                        ? [...value.visible, column.key]
                        : value.visible.filter((key) => key !== column.key),
                    }))
                  }
                />
                {column.title}
              </label>
            ))}
            <button
              onClick={() =>
                setSettings({
                  visible: columns
                    .filter((column) => !['modified', 'attributes'].includes(column.key))
                    .map((column) => column.key),
                  widths: {},
                })
              }
            >
              恢复默认列
            </button>
          </div>
        </Modal>
      )}
    </div>
  );
}
