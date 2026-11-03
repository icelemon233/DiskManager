import { useEffect, useMemo, useRef, useState } from 'react';
import {
  ArrowUp,
  ChevronDown,
  ChevronRight,
  Copy,
  File,
  Folder,
  FolderOpen,
  HardDrive,
  LayoutList,
  ListFilter,
  Maximize2,
  Minimize2,
  MoveRight,
  Pencil,
  Play,
  RefreshCw,
  Search,
  Trash2,
  X,
  Download,
  Grid2X2,
} from 'lucide-react';
import { bridge, bytes, call, date, filename } from '../shared/api';
import { Modal, Select, Splitter } from '../shared/components';
import { TreeTable } from '../shared/TreeTable';
import { TypesDialog } from '../shared/TypesDialog';
import { Treemap, extensionColor, type TreemapHandle } from '../shared/Treemap';
import type { Entry, Overview, Query, MapNode, Summary } from '../shared/types';

type Run = (operation: () => Promise<unknown>) => void;

export function Analysis({
  overview,
  version,
  run,
  theme,
  busy,
  refresh,
}: {
  overview: Overview;
  version: number;
  run: Run;
  theme: string;
  busy: boolean;
  refresh: () => void;
}) {
  const currentDriveRoot = overview.current?.roots[0]?.match(/^[A-Za-z]:\\/)?.[0];
  const [driveRoot, setDriveRoot] = useState(
    () => currentDriveRoot ?? overview.drives.find((drive) => drive.ready)?.root ?? '',
  );
  const selectedDrive = overview.drives.find(
    (drive) => drive.root.toLowerCase() === driveRoot.toLowerCase(),
  );
  const driveOptions = overview.drives.map((drive) => ({
    value: drive.root,
    label: `${drive.root.slice(0, 2)} ${drive.ready ? drive.label || '本地磁盘' : '未就绪'}`,
    disabled: !drive.ready,
  }));
  if (driveRoot && !selectedDrive)
    driveOptions.push({
      value: driveRoot,
      label: `${driveRoot.slice(0, 2)} 已断开`,
      disabled: true,
    });
  const [folder, setFolder] = useState<string | null>(null);
  const [layout, setLayout] = useState<'table' | 'map'>(() =>
    localStorage.getItem('disk-layout') === 'map' ? 'map' : 'table',
  );
  const [metric, setMetric] = useState<'allocated' | 'size'>('allocated');
  const [search, setSearch] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [filters, setFilters] = useState(false);
  const [exportDialog, setExportDialog] = useState(false);
  const [typesDialog, setTypesDialog] = useState(false);
  const [extension, setExtension] = useState('');
  const [minSize, setMinSize] = useState('');
  const [before, setBefore] = useState('');
  const [mode, setMode] = useState('contains');
  const [allFiles, setAllFiles] = useState(false);
  const [sort, setSort] = useState('allocated');
  const [descending, setDescending] = useState(true);
  const [mapNodes, setMapNodes] = useState<MapNode[]>([]);
  const summaryKey = `${overview.current?.id ?? ''}|${folder ?? ''}`;
  const [summaryResult, setSummaryResult] = useState<{ key: string; value: Summary } | null>(null);
  const summary = summaryResult?.key === summaryKey ? summaryResult.value : null;
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [selectionTotals, setSelectionTotals] = useState({
    count: 0,
    size: 0,
    allocated: 0,
    files: 0,
  });
  const [detail, setDetail] = useState<Entry | null>(null);
  const [rename, setRename] = useState<string | null>(null);
  const [newName, setNewName] = useState('');
  const [mapExpanded, setMapExpanded] = useState(false);
  const [mapVisible, setMapVisible] = useState(true);
  const [tableHeight, setTableHeight] = useState(
    Number(localStorage.getItem('disk-table-height')) || 310,
  );
  const map = useRef<TreemapHandle>(null);
  const selectedKey = [...selected].sort().join('|');
  useEffect(() => {
    if (!selected.size || !overview.current) {
      setSelectionTotals({ count: 0, size: 0, allocated: 0, files: 0 });
      setDetail(null);
      return;
    }
    let active = true;
    run(async () => {
      const value = await call<typeof selectionTotals>('selection', { paths: [...selected] });
      if (active) setSelectionTotals(value);
    });
    return () => {
      active = false;
    };
  }, [selectedKey, overview.current?.id, run]);
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearch(search), 220);
    return () => clearTimeout(timer);
  }, [search]);
  useEffect(() => {
    localStorage.setItem('disk-layout', layout);
  }, [layout]);
  useEffect(() => {
    localStorage.setItem('disk-table-height', String(tableHeight));
  }, [tableHeight]);
  const scopeKey = overview.current?.roots.join('|') ?? '';
  useEffect(() => {
    if (currentDriveRoot) setDriveRoot(currentDriveRoot);
  }, [scopeKey, currentDriveRoot]);
  useEffect(() => {
    if (!driveRoot) setDriveRoot(overview.drives.find((drive) => drive.ready)?.root ?? '');
  }, [overview.drives, driveRoot]);
  const scanRoots = useMemo(() => overview.current?.roots ?? [], [scopeKey]);
  const folderRoot = scanRoots.find((root) => folder?.toLowerCase().startsWith(root.toLowerCase()));
  const folderLabel =
    folderRoot && folder && folderRoot.length > 3
      ? filename(folderRoot) + folder.slice(folderRoot.length)
      : folder;
  useEffect(() => {
    setFolder(null);
    setSelected(new Set());
  }, [scopeKey]);
  useEffect(() => {
    setSelected(new Set());
    setDetail(null);
  }, [debouncedSearch, extension, minSize, before, allFiles, mode]);
  useEffect(() => {
    setSelected(new Set());
    setDetail(null);
  }, [overview.current?.id]);
  const filesMode = allFiles || !!(debouncedSearch || extension || minSize || before);
  const query = useMemo<Query>(
    () => ({
      parent: filesMode ? null : (folder ?? ''),
      scope: filesMode ? folder : null,
      search: debouncedSearch,
      extension,
      minSize: Number(minSize || 0) * 1024 ** 2,
      before: before ? new Date(`${before}T23:59:59`).getTime() : null,
      sort,
      descending,
      limit: 200,
      filesOnly: filesMode,
      mode,
      channel: 'analysis-table',
    }),
    [folder, filesMode, debouncedSearch, extension, minSize, before, sort, descending, mode],
  );
  useEffect(() => {
    if (!overview.current) return;
    let active = true;
    Promise.all([
      call<Summary>('summary', { scope: folder }).then((value) => {
        if (active) setSummaryResult({ key: summaryKey, value });
      }),
      call<MapNode[]>('treemap', { scope: folder, metric }).then((nodes) => {
        if (active) setMapNodes(nodes);
      }),
    ]).catch((error) => {
      if (active) run(() => Promise.reject(error));
    });
    return () => {
      active = false;
    };
  }, [folder, version, metric, overview.current?.id]);
  function open(row: Entry) {
    if (row.file.directory && row.file.path) {
      setFolder(row.file.path);
      setAllFiles(false);
      setSearch('');
    }
  }
  function select(row: Entry) {
    if (row.file.path) {
      setDetail(row);
      setSelected(new Set([row.file.path]));
    }
  }
  function plan(action: string, destination?: string, name?: string) {
    return call('operations.plan', { action, paths: [...selected], destination, name });
  }
  function scanDrive(root: string) {
    if (busy || !overview.drives.some((drive) => drive.root === root && drive.ready)) return;
    setDriveRoot(root);
    run(() => call('scan.start', { roots: [root], fast: true }));
  }
  function changeMode(files: boolean) {
    setAllFiles(files);
    setSearch('');
    setExtension('');
    setMinSize('');
    setBefore('');
    setFolder(null);
    setDetail(null);
    setSelected(new Set());
  }
  function sortColumn(column: string) {
    setSort(column);
    setDescending(sort === column ? !descending : true);
  }
  const totals = summary ?? (folder ? null : overview.current);
  const allocated = totals?.allocated ?? 0;
  const rootDrives = overview.drives.filter((d) =>
    scanRoots.some((root) => root.toLowerCase().startsWith(d.root.toLowerCase())),
  );
  const diskUsed = rootDrives.reduce((sum, drive) => sum + drive.total - drive.free, 0);
  const wholeVolumes = scanRoots.length > 0 && scanRoots.every((root) => root.length === 3);
  const mapElement = (
    <Treemap
      ref={map}
      nodes={mapNodes}
      metric={metric}
      selected={detail?.file.path}
      onSelect={select}
      onOpen={open}
      theme={theme}
    />
  );
  const inspector = detail ? (
    <aside className="inspector">
      <div className="detail-name">
        {detail.file.directory ? <Folder size={24} /> : <File size={24} />}
        <strong>{detail.file.name}</strong>
      </div>
      <p className="path selectable">{detail.file.path}</p>
      <dl>
        <dt>实际占用</dt>
        <dd>{bytes(detail.file.allocated || detail.totalAllocated)}</dd>
        <dt>文件大小</dt>
        <dd>{bytes(detail.totalSize)}</dd>
        <dt>修改时间</dt>
        <dd>{date(detail.file.modified)}</dd>
        <dt>文件数</dt>
        <dd>{detail.fileCount.toLocaleString()}</dd>
        {detail.file.links > 1 && (
          <>
            <dt>硬链接</dt>
            <dd>{detail.file.links}</dd>
          </>
        )}
      </dl>
      {detail.file.onlineOnly && <p className="notice">云端占位文件</p>}
      {detail.file.issue && <p className="notice">{detail.file.issue}</p>}
      {detail.file.directory && <button onClick={() => open(detail)}>进入目录</button>}
    </aside>
  ) : null;
  return (
    <div className="analysis-page">
      <div className="toolbar">
        <div className="drive-selector">
          <Select
            label={driveOptions.length ? '扫描磁盘' : '无可用磁盘'}
            value={selectedDrive?.root ?? driveRoot}
            onChange={setDriveRoot}
            options={driveOptions}
            disabled={busy}
            onOpenChange={(open) => {
              if (open) refresh();
            }}
            icon={<HardDrive size={18} />}
          />
        </div>
        <button
          className="primary"
          disabled={!selectedDrive?.ready || busy}
          onClick={() => scanDrive(selectedDrive!.root)}
        >
          <Play size={16} />
          {['denied', 'unavailable'].includes(overview.scanAuthorization?.state ?? '')
            ? '普通扫描'
            : '扫描'}
        </button>
        <div className="search-field">
          <Search size={17} />
          <input
            aria-label="搜索名称或路径"
            placeholder="搜索名称或路径"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
          {search && (
            <button className="icon-button" aria-label="清空搜索" onClick={() => setSearch('')}>
              <X size={14} />
            </button>
          )}
        </div>
        <button className={filters ? 'active' : ''} onClick={() => setFilters((value) => !value)}>
          <ListFilter size={17} />
          筛选
        </button>
        <div className="segmented" aria-label="布局">
          <button
            className={layout === 'table' ? 'active' : ''}
            aria-label="目录与文件表布局"
            title="目录与文件表"
            onClick={() => setLayout('table')}
          >
            <LayoutList size={18} />
          </button>
          <button
            className={layout === 'map' ? 'active' : ''}
            aria-label="矩形树图布局"
            title="矩形树图"
            onClick={() => setLayout('map')}
          >
            <Grid2X2 size={18} />
          </button>
        </div>
      </div>
      {filters && (
        <div className="filter-bar">
          <label>
            类型
            <input
              placeholder=".zip"
              value={extension === '<none>' ? '无扩展名' : extension}
              onChange={(e) => setExtension(e.target.value)}
            />
          </label>
          <label>
            至少 MiB
            <input
              type="number"
              min="0"
              value={minSize}
              onChange={(e) => setMinSize(e.target.value)}
            />
          </label>
          <label>
            修改早于
            <input type="date" value={before} onChange={(e) => setBefore(e.target.value)} />
          </label>
          <Select
            label="搜索方式"
            value={mode}
            onChange={setMode}
            options={[
              { value: 'contains', label: '包含文字' },
              { value: 'wildcard', label: '通配符' },
              { value: 'regex', label: '正则表达式' },
            ]}
          />
          <label className="check-label">
            <input
              type="checkbox"
              checked={allFiles}
              onChange={(e) => setAllFiles(e.target.checked)}
            />
            包含子目录
          </label>
          <button
            onClick={() => {
              setExtension('');
              setMinSize('');
              setBefore('');
              setSearch('');
              setAllFiles(false);
            }}
          >
            重置
          </button>
        </div>
      )}
      {!overview.current ? (
        <div className="analysis-empty">
          <HardDrive size={52} strokeWidth={1.2} />
          <h2>{overview.drives.length ? '扫描磁盘' : '未发现可用磁盘'}</h2>
          <div className="drive-list">
            {overview.drives.map((drive) => (
              <button
                key={drive.root}
                disabled={!drive.ready || busy}
                className="drive"
                aria-label={`扫描 ${drive.root.slice(0, 2)}`}
                onClick={() => scanDrive(drive.root)}
              >
                <span>
                  <HardDrive size={20} />
                  <span
                    className="ellipsis"
                    title={`${drive.root.slice(0, 2)} ${drive.label || '本地磁盘'}`}
                  >
                    {drive.root.slice(0, 2)} {drive.label || '本地磁盘'}
                  </span>
                  <Play size={16} className="push-right" />
                </span>
                <div className="capacity">
                  <i
                    style={{
                      width: `${drive.total ? ((drive.total - drive.free) / drive.total) * 100 : 0}%`,
                    }}
                  />
                </div>
                <small>
                  {drive.ready
                    ? `可用 ${bytes(drive.free)} / ${bytes(drive.total)} · ${drive.format}`
                    : '未就绪'}
                </small>
              </button>
            ))}
          </div>
        </div>
      ) : (
        <>
          <div className="analysis-summary">
            <div>
              <strong>
                {totals ? bytes(metric === 'allocated' ? allocated : totals.size) : '—'}
              </strong>
              <span>{totals?.count.toLocaleString() ?? '—'} 个文件</span>
            </div>
            {wholeVolumes && !folder && !overview.current.imported && (
              <span title="文件系统元数据、不可读内容及扫描期间的变化可能造成差额">
                卷当前已用 {bytes(diskUsed)} · 差额 {bytes(diskUsed - allocated)}
              </span>
            )}
            <Select
              label="统计口径"
              value={metric}
              onChange={(value) => {
                setMetric(value as 'allocated' | 'size');
                setSort(value);
              }}
              options={[
                { value: 'allocated', label: '实际占用' },
                { value: 'size', label: '文件大小' },
              ]}
            />
          </div>
          <div className="breadcrumb-row">
            <button
              className={layout === 'map' ? undefined : 'compact-types'}
              onClick={() => setTypesDialog(true)}
            >
              类型
            </button>
            <button
              className="icon-button"
              disabled={!folder}
              aria-label="返回上级"
              onClick={() =>
                setFolder(
                  scanRoots.includes(folder!)
                    ? null
                    : folder!.slice(0, Math.max(3, folder!.lastIndexOf('\\'))) || null,
                )
              }
            >
              <ArrowUp size={17} />
            </button>
            <button
              className="text-button"
              title={scanRoots.join('、')}
              onClick={() => setFolder(null)}
            >
              {scanRoots.length === 1 ? filename(scanRoots[0]) : '扫描范围'}
            </button>
            {folder && (
              <>
                <ChevronRight size={14} />
                <span className="path" title={folder}>
                  {folderLabel}
                </span>
              </>
            )}
            <button
              className="icon-button push-right"
              title="刷新当前目录"
              aria-label="刷新当前目录"
              disabled={busy || overview.current.imported}
              onClick={() =>
                run(() =>
                  folder
                    ? call('scan.refresh', { paths: [folder] })
                    : call('scan.start', { roots: scanRoots, fast: true }),
                )
              }
            >
              <RefreshCw size={16} />
            </button>
            <button title="导出扫描结果" onClick={() => setExportDialog(true)}>
              <Download size={16} />
              导出
            </button>
          </div>
          <div className="selection-bar">
            {selected.size > 0 && <span>已选 {selected.size} 项</span>}
            <button
              disabled={selected.size !== 1}
              onClick={() => run(() => bridge().reveal([...selected][0]))}
            >
              <FolderOpen size={16} />
              定位
            </button>
            <button
              disabled={selected.size !== 1}
              onClick={() => run(() => bridge().copy([...selected][0]))}
            >
              <Copy size={16} />
              复制路径
            </button>
            <button
              disabled={!selected.size || busy || overview.current.imported}
              onClick={() =>
                run(async () => {
                  const paths = await bridge().folders();
                  if (paths[0]) return plan('move', paths[0]);
                })
              }
            >
              <MoveRight size={16} />
              移动
            </button>
            <button
              disabled={selected.size !== 1 || busy || overview.current.imported}
              onClick={() => {
                setRename([...selected][0]);
                setNewName(filename([...selected][0]));
              }}
            >
              <Pencil size={16} />
              重命名
            </button>
            <button
              disabled={!selected.size || busy || overview.current.imported}
              onClick={() => run(() => plan('recycle'))}
            >
              <Trash2 size={16} />
              回收站
            </button>
            <button
              className="danger-text"
              disabled={!selected.size || busy || overview.current.imported}
              onClick={() => run(() => plan('delete'))}
            >
              永久删除
            </button>
          </div>
          {layout === 'map' ? (
            <div className={`map-layout${detail ? '' : ' single-panel'}`}>
              <section className="map-panel">{mapElement}</section>
              {inspector}
            </div>
          ) : (
            <div className={`table-layout ${mapExpanded ? 'map-expanded' : ''}`}>
              {!mapExpanded && (
                <section
                  className="table-region"
                  style={{
                    height: mapVisible ? tableHeight : '100%',
                    gridTemplateColumns: 'minmax(0,1fr) 220px',
                  }}
                >
                  <TreeTable
                    roots={scanRoots}
                    indexId={overview.current.id}
                    revision={version}
                    query={query}
                    filesMode={filesMode}
                    changeMode={changeMode}
                    metric={metric}
                    totals={{ size: totals?.size ?? 0, allocated }}
                    selected={selected}
                    setSelected={setSelected}
                    onSelect={(row) => {
                      select(row);
                      if (row.file.directory) setFolder(row.file.path);
                    }}
                    onOpen={open}
                    onSort={sortColumn}
                    run={run}
                  />
                  <div className="types-panel">
                    <div className="panel-heading">
                      文件类型
                      <button
                        className="text-button push-right"
                        onClick={() => setTypesDialog(true)}
                      >
                        全部
                      </button>
                    </div>
                    <div className="type-list">
                      {summary?.types.slice(0, 30).map((type) => (
                        <button
                          className="type-row"
                          key={type.extension}
                          onClick={() => {
                            setExtension(type.extension || '<none>');
                            setAllFiles(true);
                            setFilters(true);
                          }}
                        >
                          <i style={{ background: extensionColor(type.extension) }} />
                          <span>
                            {type.extension || '无扩展名'}
                            <small>
                              {bytes(metric === 'allocated' ? type.allocated : type.size)} ·{' '}
                              {type.count.toLocaleString()} 个
                            </small>
                          </span>
                        </button>
                      ))}
                    </div>
                  </div>
                </section>
              )}
              {mapVisible && !mapExpanded && (
                <Splitter
                  direction="horizontal"
                  value={tableHeight}
                  minimum={170}
                  maximum={520}
                  onChange={setTableHeight}
                  label="文件表高度"
                />
              )}
              <div className="map-heading">
                <button className="text-button" onClick={() => setMapVisible((value) => !value)}>
                  {mapVisible ? <ChevronDown size={15} /> : <ChevronRight size={15} />}空间分布
                </button>
                <button
                  className="icon-button push-right"
                  title="放大或还原树图"
                  aria-label="放大或还原树图"
                  onClick={() => {
                    setMapExpanded((value) => !value);
                    setMapVisible(true);
                  }}
                >
                  {mapExpanded ? <Minimize2 size={16} /> : <Maximize2 size={16} />}
                </button>
              </div>
              {mapVisible && <div className="map-bottom">{mapElement}</div>}
            </div>
          )}
          <div className="scan-status">
            {selected.size > 0 && (
              <span className="selected-totals">
                {selectionTotals.files.toLocaleString()} 个文件 · 大小 {bytes(selectionTotals.size)}{' '}
                · 占用 {bytes(selectionTotals.allocated)}
              </span>
            )}
            <span
              title={`${overview.current.method}${overview.current.timing?.saveMs != null ? ` · 历史保存 ${(overview.current.timing.saveMs / 1000).toFixed(2)} 秒` : ''}`}
            >
              {date(overview.current.created)}
              {overview.current.imported
                ? ' · 导入快照'
                : overview.current.status === 'updated'
                  ? ' · 已局部刷新'
                  : overview.current.status !== 'complete'
                    ? ' · 部分扫描'
                    : ''}
              {' · '}扫描 {(overview.current.durationMs / 1000).toFixed(2)} 秒
              {overview.saveState === 'pending' || overview.saveState === 'saving'
                ? ' · 正在保存'
                : overview.saveState === 'failed'
                  ? ' · 保存失败，可导出快照'
                  : ''}
            </span>
            {!!summary?.issues.length && (
              <details>
                <summary>读取问题 ({summary.issues.length})</summary>
                <div className="issue-popover">
                  {summary.issues.map((issue, index) => (
                    <p key={index}>{issue}</p>
                  ))}
                </div>
              </details>
            )}
          </div>
        </>
      )}
      {typesDialog && (
        <TypesDialog
          scope={folder}
          close={() => setTypesDialog(false)}
          select={(value) => {
            setExtension(value);
            setAllFiles(true);
            setFilters(true);
          }}
          run={run}
        />
      )}
      {exportDialog && (
        <Modal title="导出扫描结果" onClose={() => setExportDialog(false)}>
          <div className="field-stack">
            <button
              onClick={() =>
                run(async () => {
                  await bridge().export('csv', query);
                  setExportDialog(false);
                })
              }
            >
              当前筛选结果 CSV
            </button>
            <button
              onClick={() =>
                run(async () => {
                  await bridge().export('csv', { filesOnly: true });
                  setExportDialog(false);
                })
              }
            >
              全部文件 CSV
            </button>
            <button
              onClick={() =>
                run(async () => {
                  await bridge().export('snapshot', {});
                  setExportDialog(false);
                })
              }
            >
              当前扫描快照
            </button>
            <button
              disabled={!mapVisible && layout === 'table'}
              onClick={() =>
                run(async () => {
                  const image = map.current?.png();
                  if (image) await bridge().png(image);
                  setExportDialog(false);
                })
              }
            >
              树图 PNG
            </button>
          </div>
        </Modal>
      )}
      {rename && (
        <Modal title="重命名" onClose={() => setRename(null)}>
          <form
            onSubmit={(e) => {
              e.preventDefault();
              run(() => plan('rename', undefined, newName));
              setRename(null);
            }}
          >
            <p className="path">{rename}</p>
            <label className="field">
              <input
                aria-label="新名称"
                autoFocus
                value={newName}
                onChange={(e) => setNewName(e.target.value)}
                required
              />
            </label>
            <div className="modal-actions">
              <button type="button" onClick={() => setRename(null)}>
                取消
              </button>
              <button className="primary" type="submit">
                预览
              </button>
            </div>
          </form>
        </Modal>
      )}
    </div>
  );
}
