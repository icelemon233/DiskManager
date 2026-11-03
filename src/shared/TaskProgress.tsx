import { jobLabels } from './api';
import type { Job } from './types';

export function TaskProgress({ job, compact = false }: { job: Job; compact?: boolean }) {
  const completed = Number.isFinite(job.completed) ? Math.max(0, job.completed) : 0;
  const total = job.total != null && Number.isFinite(job.total) && job.total > 0 ? job.total : null;
  const active = job.state === 'running' || job.state === 'queued';
  const percent =
    job.state === 'complete'
      ? 100
      : total == null
        ? null
        : Math.min(100, Math.floor((completed / total) * 1000) / 10);
  const percentage = percent == null ? '' : `${percent.toLocaleString('zh-CN')}%`;
  const counts =
    total != null && job.state !== 'complete'
      ? `${completed.toLocaleString()} / ${total.toLocaleString()} 项`
      : completed > 0 || job.total === 0
        ? `${completed.toLocaleString()} 项`
        : '';
  if (!active && !counts && !job.issues) return null;

  return (
    <div className={`task-progress${compact ? ' compact' : ''}`}>
      <div className="progress-main">
        <div
          className={`progress-track${percent == null && job.state === 'running' ? ' indeterminate' : ''}`}
          data-state={job.state}
          role="progressbar"
          aria-label={`${jobLabels[job.kind] ?? job.kind}进度`}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={percent ?? undefined}
          aria-valuetext={
            percent == null ? job.phase : `${percentage}${counts ? `，${counts}` : ''}`
          }
        >
          <span
            className="progress-fill"
            style={percent == null ? undefined : { width: `${percent}%` }}
          />
        </div>
        {percentage && <strong className="progress-value">{percentage}</strong>}
      </div>
      {(counts || job.issues > 0) && (
        <div className="progress-meta">
          {counts && <span>{counts}</span>}
          {job.issues > 0 && (
            <span className="progress-issues">{job.issues.toLocaleString()} 项需查看</span>
          )}
        </div>
      )}
    </div>
  );
}
