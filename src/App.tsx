import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Activity,
  BrushCleaning,
  CircleAlert,
  Copy,
  HardDrive,
  History as HistoryIcon,
  ListChecks,
  Minus,
  Pin,
  PinOff,
  Settings as SettingsIcon,
  Square,
  X,
} from 'lucide-react';
import { actionLabels, bridge, bytes, call, jobLabels, message } from './shared/api';
import { Modal, VirtualList } from './shared/components';
import { TaskProgress } from './shared/TaskProgress';
import type { CachePlan, Job, OperationPlan, Overview, WindowPreferences } from './shared/types';
import { Analysis } from './features/Analysis';
import { History } from './features/History';
import { DuplicateFiles } from './features/Duplicates';
import { Cleanup } from './features/Cleanup';
import { Settings } from './features/Settings';
import { OperationHistory, Tasks } from './features/Activity';

type Plan = { kind: 'files'; plan: OperationPlan } | { kind: 'tool'; plan: CachePlan };
const navigation = [
  { id: 'analysis', name: '空间分析', icon: HardDrive },
  { id: 'history', name: '历史对比', icon: HistoryIcon },
  { id: 'duplicates', name: '重复文件', icon: Copy },
  { id: 'cleanup', name: '清理', icon: BrushCleaning },
  { id: 'operations', name: '操作记录', icon: ListChecks },
  { id: 'tasks', name: '后台任务', icon: Activity },
];

function PlanDialog({
  value,
  close,
  run,
}: {
  value: Plan;
  close: () => void;
  run: (operation: () => Promise<unknown>) => void;
}) {
  const [confirmation, setConfirmation] = useState('');
  const permanent = value.kind === 'files' && value.plan.action === 'delete';
  const advanced = value.kind === 'tool' && value.plan.target.kind === 'shared';
  const phrase = permanent ? '永久删除' : '清空依赖';
  const executable = value.kind === 'tool' || value.plan.items.some((item) => !item.error);
  const label = value.kind === 'tool' ? '清理缓存' : actionLabels[value.plan.action];
  return (
    <Modal title={label} onClose={close} wide>
      {value.kind === 'files' ? (
        <>
          <div className="plan-summary">
            <strong>{value.plan.items.length} 个项目</strong>
            <span>
              选中占用 {bytes(value.plan.items.reduce((sum, item) => sum + item.allocated, 0))}
            </span>
          </div>
          <VirtualList
            items={value.plan.items}
            rowHeight={64}
            className="plan-list"
            style={{ height: Math.min(280, Math.max(1, value.plan.items.length) * 64 + 2) }}
            label="待执行清单"
            render={(item) => (
              <div className="plan-row">
                <div className="record-name">
                  <span className="path" title={item.path}>
                    {item.path}
                  </span>
                  {item.destination && <small className="path">→ {item.destination}</small>}
                  {item.error && <small className="error-text">{item.error}</small>}
                </div>
                <span>{item.error ? '跳过' : bytes(item.allocated)}</span>
              </div>
            )}
          />
          {value.plan.warnings.map((warning) => (
            <p className="notice" key={warning}>
              {warning}
            </p>
          ))}
        </>
      ) : (
        <div className="field-stack">
          <strong>{value.plan.target.name}</strong>
          <p className="path selectable">{value.plan.target.root}</p>
          <p className="notice">缓存将永久删除。{value.plan.target.impact}</p>
        </div>
      )}
      {(permanent || advanced) && (
        <label className="field confirmation">
          输入“{phrase}”确认
          <input
            value={confirmation}
            onChange={(event) => setConfirmation(event.target.value)}
            autoComplete="off"
          />
        </label>
      )}
      <div className="modal-actions">
        <button onClick={close}>取消</button>
        <button
          className={permanent || advanced ? 'danger' : 'primary'}
          disabled={!executable || ((permanent || advanced) && confirmation !== phrase)}
          onClick={() =>
            run(async () => {
              const result = await bridge().execute(value.plan.id, value.kind);
              if (result) close();
            })
          }
        >
          {label}
        </button>
      </div>
    </Modal>
  );
}

export default function App() {
  const [page, setPage] = useState('analysis');
  const [overview, setOverview] = useState<Overview | null>(null);
  const [appearance, setAppearance] = useState<WindowPreferences>(
    () =>
      window.diskManager?.startup ?? {
        theme: 'system',
        nativeTheme: 'light',
        closeBehavior: 'tray',
      },
  );
  const [jobs, setJobs] = useState<Job[]>([]);
  const [version, setVersion] = useState(0);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState<{ message: string; duration: number } | null>(null);
  const [pinned, setPinned] = useState(localStorage.getItem('disk-sidebar-pinned') !== 'false');
  const [hover, setHover] = useState(false);
  const [caption, setCaption] = useState({ hovered: 0, pressed: 0 });
  const [maximized, setMaximized] = useState(false);
  const [plan, setPlan] = useState<Plan | null>(null);
  const seenJobs = useRef(new Set<string>());
  const leaveTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const overviewGeneration = useRef(0);
  const run = useCallback((operation: () => Promise<unknown>) => {
    void operation().catch((reason) => setError(message(reason)));
  }, []);
  const refresh = useCallback(() => {
    const generation = ++overviewGeneration.current;
    run(async () => {
      const value = await call<Overview>('overview');
      if (generation === overviewGeneration.current) {
        setOverview(value);
        setVersion((value) => value + 1);
      }
    });
  }, [run]);
  const applyJobs = useCallback(
    (value: Job[]) => {
      if (!Array.isArray(value)) return;
      setJobs(value);
      for (const job of value) {
        if (
          !['complete', 'failed', 'cancelled'].includes(job.state) ||
          seenJobs.current.has(job.id)
        )
          continue;
        seenJobs.current.add(job.id);
        if (job.kind === 'authorization') {
          refresh();
          continue;
        }
        if (job.state === 'failed') {
          setError(job.error ?? '任务失败。');
          continue;
        }
        if (job.kind === 'plan' && job.state === 'complete')
          run(async () => {
            const completed = await call<Job>('jobs.result', { id: job.id });
            setPlan({ kind: 'files', plan: completed.result as OperationPlan });
          });
        else if (job.kind === 'cache-preview' && job.state === 'complete')
          run(async () => {
            const completed = await call<Job>('jobs.result', { id: job.id });
            setPlan(completed.result as Plan);
          });
        else if (job.state === 'complete') {
          if (!['snapshot-save', 'refresh'].includes(job.kind))
            setNotice({
              message: `${jobLabels[job.kind] ?? '任务'}已完成${job.issues ? `，${job.issues} 项需查看` : ''}`,
              duration: 5000,
            });
          refresh();
        }
        if (job.error) setError(job.error);
      }
    },
    [refresh, run],
  );
  useEffect(() => {
    refresh();
    run(async () => {
      const initial = await call<Job[]>('jobs.list');
      initial
        .filter((job) => ['complete', 'failed', 'cancelled'].includes(job.state))
        .forEach((job) => seenJobs.current.add(job.id));
      setJobs(initial);
    });
    if (!window.diskManager) return;
    return bridge().subscribe((event) => {
      if (event.name === 'jobs') applyJobs(event.data as Job[]);
      if (event.name === 'preferences') setAppearance(event.data as WindowPreferences);
      if (event.name === 'caption') setCaption(event.data as typeof caption);
      if (event.name === 'window-state')
        setMaximized((event.data as { maximized: boolean }).maximized);
      if (event.name === 'error') setError(String(event.data));
      if (event.name === 'notice')
        setNotice(event.data == null ? null : { message: String(event.data), duration: 2000 });
      if (event.name === 'navigate') setPage(String(event.data));
    });
  }, [refresh, applyJobs, run]);
  const theme = appearance.theme === 'system' ? appearance.nativeTheme : appearance.theme;
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
  }, [theme]);
  useEffect(() => {
    localStorage.setItem('disk-sidebar-pinned', String(pinned));
  }, [pinned]);
  useEffect(() => {
    if (!notice) return;
    const timer = setTimeout(() => setNotice(null), notice.duration);
    return () => clearTimeout(timer);
  }, [notice]);
  const activeJobs = jobs.filter((job) => ['running', 'queued'].includes(job.state));
  const running = activeJobs.find((job) => job.kind !== 'snapshot-save') ?? activeJobs[0];
  const busy = activeJobs.some((job) => job.kind !== 'snapshot-save');
  const expanded = pinned || hover;
  return (
    <div className={`app-shell ${pinned ? 'pinned' : 'unpinned'}`}>
      <header className="titlebar">
        <img src="./icon.png" alt="" />
        <span>DiskManager</span>
        {window.diskManager?.startup.qa && <small>测试</small>}
        <div className="caption-buttons">
          {[
            { id: 8, action: 'minimize', label: '最小化', icon: <Minus /> },
            {
              id: 9,
              action: 'maximize',
              label: maximized ? '还原' : '最大化',
              icon: maximized ? <Copy /> : <Square />,
            },
            { id: 20, action: 'close', label: '关闭', icon: <X /> },
          ].map((button) => (
            <button
              key={button.id}
              tabIndex={-1}
              aria-label={button.label}
              className={`${button.action} ${caption.hovered === button.id ? 'hovered' : ''} ${caption.pressed === button.id ? 'pressed' : ''}`}
              onClick={() => bridge().window(button.action)}
            >
              {button.icon}
            </button>
          ))}
        </div>
      </header>
      <aside
        className={`sidebar ${expanded ? 'expanded' : ''}`}
        onMouseEnter={() => {
          if (leaveTimer.current) clearTimeout(leaveTimer.current);
          setHover(true);
        }}
        onMouseLeave={(event) => {
          if (event.currentTarget.querySelector(':focus-visible')) return;
          leaveTimer.current = setTimeout(() => setHover(false), 150);
        }}
        onFocus={(event) => {
          if (event.target.matches(':focus-visible')) setHover(true);
        }}
        onBlur={(event) => {
          if (!event.currentTarget.contains(event.relatedTarget)) setHover(false);
        }}
      >
        <nav aria-label="功能导航">
          {navigation.map((item) => (
            <button
              key={item.id}
              title={expanded ? undefined : item.name}
              className={`nav-item ${page === item.id ? 'selected' : ''}`}
              aria-current={page === item.id ? 'page' : undefined}
              onClick={() => setPage(item.id)}
            >
              <item.icon size={18} strokeWidth={1.65} />
              <span>{item.name}</span>
              {item.id === 'tasks' && busy && <i className="activity-dot" />}
            </button>
          ))}
        </nav>
        <div className="sidebar-footer">
          <button
            title={expanded ? undefined : '设置'}
            className={`nav-item ${page === 'settings' ? 'selected' : ''}`}
            aria-current={page === 'settings' ? 'page' : undefined}
            onClick={() => setPage('settings')}
          >
            <SettingsIcon size={18} strokeWidth={1.65} />
            <span>设置</span>
          </button>
          {expanded && (
            <button
              className="pin-button"
              aria-label={pinned ? '取消固定侧栏' : '固定侧栏'}
              onClick={() => {
                setPinned((value) => !value);
                setHover(false);
              }}
            >
              {pinned ? <PinOff size={17} /> : <Pin size={17} />}
            </button>
          )}
        </div>
      </aside>
      <main className="main-content">
        {error && (
          <div className="error-banner" role="alert">
            <CircleAlert size={18} />
            <span>{error}</span>
            <button className="icon-button" aria-label="关闭错误提示" onClick={() => setError('')}>
              <X size={16} />
            </button>
          </div>
        )}
        {overview?.notice && <div className="notice-banner">{overview.notice}</div>}
        {overview?.scanAuthorization &&
          ['denied', 'unavailable'].includes(overview.scanAuthorization.state) && (
            <div className="notice-banner authorization-notice" role="status">
              <span>{overview.scanAuthorization.message}</span>
              <button
                disabled={jobs.some(
                  (job) =>
                    job.kind === 'authorization' && ['running', 'queued'].includes(job.state),
                )}
                onClick={() => run(() => call('scan.authorize'))}
              >
                重新授权
              </button>
            </div>
          )}
        {overview ? (
          <>
            <section className="page-slot" hidden={page !== 'analysis'}>
              <Analysis
                overview={overview}
                version={version}
                run={run}
                theme={theme}
                busy={busy}
                refresh={refresh}
              />
            </section>
            <section className="page-slot" hidden={page !== 'history'}>
              <History
                version={version}
                run={run}
                openAnalysis={() => {
                  refresh();
                  setPage('analysis');
                }}
              />
            </section>
            <section className="page-slot" hidden={page !== 'duplicates'}>
              <DuplicateFiles overview={overview} version={version} run={run} busy={busy} />
            </section>
            <section className="page-slot" hidden={page !== 'cleanup'}>
              <Cleanup
                active={page === 'cleanup'}
                overview={overview}
                run={run}
                refresh={refresh}
                busy={busy}
              />
            </section>
            <section className="page-slot" hidden={page !== 'operations'}>
              <OperationHistory version={version} run={run} />
            </section>
            <section className="page-slot" hidden={page !== 'tasks'}>
              <Tasks jobs={jobs} run={run} />
            </section>
            <section className="page-slot" hidden={page !== 'settings'}>
              <Settings overview={overview} appearance={appearance} run={run} refresh={refresh} />
            </section>
          </>
        ) : (
          <div className="startup">
            <HardDrive size={40} strokeWidth={1.2} />
            <p>{error ? '本机服务连接失败' : '正在读取磁盘与本地记录…'}</p>
            {error && <button onClick={refresh}>重试连接</button>}
          </div>
        )}
        {running && (
          <div className="job-strip" role="status">
            <Activity size={16} />
            <button className="text-button" onClick={() => setPage('tasks')}>
              {jobLabels[running.kind] ?? running.kind}
            </button>
            <div className="job-strip-body">
              <TaskProgress job={running} compact />
              <span className="path" title={running.phase}>
                {running.phase}
              </span>
            </div>
            <button
              className="icon-button"
              aria-label="取消当前任务"
              disabled={running.kind === 'snapshot-save'}
              onClick={() => run(() => call('jobs.cancel', { id: running.id }))}
            >
              <X size={16} />
            </button>
          </div>
        )}
      </main>
      {notice && (
        <div className="toast" role="status">
          {notice.message}
        </div>
      )}
      {plan && <PlanDialog key={plan.plan.id} value={plan} close={() => setPlan(null)} run={run} />}
    </div>
  );
}
