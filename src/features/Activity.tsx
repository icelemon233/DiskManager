import { useEffect, useState } from 'react';
import { RefreshCw, X } from 'lucide-react';
import { actionLabels, call, date, jobLabels } from '../shared/api';
import { Empty } from '../shared/components';
import { TaskProgress } from '../shared/TaskProgress';
import type { Job, OperationLog } from '../shared/types';
const resultLabels: Record<string, string> = {
  complete: '完成',
  skipped: '跳过',
  failed: '失败',
  partial: '部分完成',
  interrupted: '已中断',
  cancelled: '已取消',
  running: '进行中',
};

export function Tasks({
  jobs,
  run,
}: {
  jobs: Job[];
  run: (operation: () => Promise<unknown>) => void;
}) {
  const labels: Record<string, string> = {
    running: '进行中',
    queued: '等待中',
    complete: '已完成',
    failed: '失败',
    cancelled: '已取消',
  };
  return (
    <div className="page-body">
      <div className="page-heading">
        <h1>后台任务</h1>
      </div>
      <div className="task-list">
        {jobs.map((job) => (
          <section className="task-row panel" key={job.id}>
            <header>
              <strong>{jobLabels[job.kind] ?? job.kind}</strong>
              <span className={`status-label ${job.state}`}>{labels[job.state]}</span>
              <span className="push-right muted">{date(job.started)}</span>
              {job.kind !== 'snapshot-save' && ['running', 'queued'].includes(job.state) && (
                <button
                  className="icon-button"
                  aria-label="取消任务"
                  onClick={() => run(() => call('jobs.cancel', { id: job.id }))}
                >
                  <X size={17} />
                </button>
              )}
            </header>
            <TaskProgress job={job} />
            {job.error ? (
              <p className="error-text task-error">{job.error}</p>
            ) : (
              ['running', 'queued'].includes(job.state) && (
                <p className="path muted" title={job.phase}>
                  {job.phase}
                </p>
              )
            )}
          </section>
        ))}
      </div>
      {jobs.length === 0 && <Empty>暂无任务</Empty>}
    </div>
  );
}

export function OperationHistory({
  version,
  run,
}: {
  version: number;
  run: (operation: () => Promise<unknown>) => void;
}) {
  const [records, setRecords] = useState<OperationLog[]>([]);
  async function refresh() {
    setRecords(await call('operations.history'));
  }
  useEffect(() => {
    run(refresh);
  }, [version]);
  return (
    <div className="page-body">
      <div className="page-heading">
        <h1>操作记录</h1>
        <button onClick={() => run(refresh)}>
          <RefreshCw size={17} />
          刷新
        </button>
      </div>
      <div className="operation-list">
        {records.map((record) => (
          <details className="operation-record panel" key={record.id}>
            <summary>
              <strong>
                {actionLabels[record.action] ?? record.action}
                {record.name && ` · ${record.name}`}
              </strong>
              <span>{date(record.time)}</span>
              <span>
                {record.results
                  ? `${record.results.filter((result) => result.state === 'complete').length} / ${record.results.length} 项完成`
                  : '完成'}
              </span>
            </summary>
            {record.root && <p className="path">{record.root}</p>}
            {record.output && <pre>{record.output}</pre>}
            {record.results?.map((result, index) => (
              <div className="operation-detail" key={index}>
                <span className={`status-label ${result.state}`}>
                  {resultLabels[result.state] ?? result.state}
                </span>
                <div className="record-name">
                  <span className="path">{result.path}</span>
                  {result.destination && <small className="path">→ {result.destination}</small>}
                  {result.error && <small className="error-text">{result.error}</small>}
                </div>
              </div>
            ))}
          </details>
        ))}
      </div>
      {records.length === 0 && <Empty>暂无操作记录</Empty>}
    </div>
  );
}
