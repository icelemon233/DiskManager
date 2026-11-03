import { useEffect, useState } from 'react';
import { FolderPlus, Save } from 'lucide-react';
import { bridge, call } from '../shared/api';
import { Select } from '../shared/components';
import type { Overview, Preferences, Theme, WindowPreferences } from '../shared/types';

export function Settings({
  overview,
  appearance,
  run,
  refresh,
}: {
  overview: Overview;
  appearance: WindowPreferences;
  run: (operation: () => Promise<unknown>) => void;
  refresh: () => void;
}) {
  const [draft, setDraft] = useState<Preferences>(overview.preferences);
  const [saved, setSaved] = useState(false);
  useEffect(() => setDraft(overview.preferences), [JSON.stringify(overview.preferences)]);
  return (
    <div className="page-body settings-page">
      <div className="page-heading">
        <h1>设置</h1>
      </div>
      <section className="settings-section">
        <h2>外观与窗口</h2>
        <div className="setting-row">
          <label>主题</label>
          <Select
            value={appearance.theme}
            label="主题"
            onChange={(theme) => run(() => bridge().preferences({ theme: theme as Theme }))}
            options={[
              { value: 'system', label: '跟随系统' },
              { value: 'light', label: '浅色' },
              { value: 'dark', label: '深色' },
            ]}
          />
        </div>
        <div className="setting-row">
          <label>关闭窗口时</label>
          <Select
            value={appearance.closeBehavior}
            label="关闭窗口时"
            onChange={(value) =>
              run(() => bridge().preferences({ closeBehavior: value as 'tray' | 'quit' }))
            }
            options={[
              { value: 'tray', label: '进入系统托盘' },
              { value: 'quit', label: '退出应用' },
            ]}
          />
        </div>
      </section>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          run(async () => {
            await call('preferences.save', draft);
            setSaved(true);
            refresh();
          });
        }}
        data-cinna-dirty={JSON.stringify(draft) !== JSON.stringify(overview.preferences)}
      >
        <section className="settings-section">
          <h2>历史记录</h2>
          <label className="setting-row">
            每个扫描范围保留次数
            <input
              type="number"
              min="1"
              max="1000"
              required
              value={draft.retainCount}
              onChange={(e) => {
                setSaved(false);
                setDraft({ ...draft, retainCount: Number(e.target.value) });
              }}
            />
          </label>
          <label className="setting-row">
            容量上限（GiB）
            <input
              type="number"
              min="0.25"
              max="1024"
              step="0.25"
              required
              value={draft.historyBudget / 1024 ** 3}
              onChange={(e) => {
                setSaved(false);
                setDraft({ ...draft, historyBudget: Number(e.target.value) * 1024 ** 3 });
              }}
            />
          </label>
          <p className="muted">固定记录不参与轮换。</p>
        </section>
        <section className="settings-section">
          <h2>重复文件</h2>
          <label className="field">
            优先保留目录（从上到下）
            <textarea
              rows={5}
              value={(draft.preferredDirectories ?? []).join('\n')}
              onChange={(e) => {
                setSaved(false);
                setDraft({
                  ...draft,
                  preferredDirectories: e.target.value.split('\n').filter(Boolean),
                });
              }}
            />
          </label>
          <button
            type="button"
            onClick={() =>
              run(async () => {
                const roots = await bridge().folders();
                setSaved(false);
                setDraft((value) => ({
                  ...value,
                  preferredDirectories: [
                    ...new Set([...(value.preferredDirectories ?? []), ...roots]),
                  ],
                }));
              })
            }
          >
            <FolderPlus size={17} />
            添加目录
          </button>
        </section>
        <div className="actions">
          <button type="submit" className="primary">
            <Save size={17} />
            保存
          </button>
          {saved && <span role="status">已保存</span>}
        </div>
      </form>
      <section className="settings-section about">
        <img src="./icon.png" alt="DiskManager" width="64" height="64" />
        <div>
          <strong>DiskManager</strong>
          <p>{__APP_VERSION__} · Cinna</p>
        </div>
        <button className="push-right" onClick={() => bridge().window('quit')}>
          退出应用
        </button>
      </section>
    </div>
  );
}
