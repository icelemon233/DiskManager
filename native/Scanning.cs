using System.Diagnostics;

namespace DiskManager;

internal sealed partial class Workspace
{
    public async Task Scan(ScanRequest request, Job job)
    {
        var snapshot = await Analyze(request, job);
        await Persist(snapshot, new Job("snapshot-save"));
    }

    public async Task<AnalysisSnapshot> Analyze(ScanRequest request, Job job)
    {
        string[] roots = Paths.Reduce(request.Roots);
        if (roots.Length is < 1 or > 32 || roots.Any(path => !Directory.Exists(path)))
        {
            throw new ArgumentException("请选择 1 到 32 个可访问的本地磁盘或文件夹。");
        }

        foreach (string root in roots)
        {
            if (new DriveInfo(Path.GetPathRoot(root)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                throw new ArgumentException("请选择本地磁盘或外接盘。");
            }

            if (WindowsFiles.HasLinkAncestor(root))
            {
                throw new ArgumentException("请选择链接指向的实际目录。");
            }
        }
        var timer = Stopwatch.StartNew();
        var entries = new List<FileEntry>();
        var issues = new List<string>();
        var methods = new HashSet<string>();
        void Issue(string message)
        {
            job.Issues++;
            if (issues.Count < 10000)
            {
                issues.Add(message);
            }
        }
        foreach (string root in roots)
        {
            job.Cancellation.Token.ThrowIfCancellationRequested();
            if (request.Fast && root.Length == 3 && new DriveInfo(root).DriveFormat == "NTFS")
            {
                int before = entries.Count;
                long completed = job.Completed;
                try
                {
                    await scanBroker.Read(root, DirectoryPath, entries.Add, job, Issue);
                    methods.Add("MFT");
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    entries.RemoveRange(before, entries.Count - before);
                    job.Completed = completed;
                    if (issues.Count < 10000)
                    {
                        issues.Add($"{root} 快速扫描回退 — {ex.Message}");
                    }

                    methods.Add("目录回退");
                }
            }
            methods.Add("目录遍历");
            foreach (var entry in WindowsFiles.Enumerate(root, Issue, job.Cancellation.Token))
            {
                if (Paths.Within(entry.Path, DirectoryPath))
                {
                    continue;
                }

                entries.Add(entry.Path == root ? entry with
                {
                    Parent = ""
                } : entry);
                job.Completed++;
                if ((job.Completed & 255) == 0)
                {
                    job.Phase = $"扫描 {entry.Parent}";
                }
            }
        }
        long readMs = Math.Max(0, timer.ElapsedMilliseconds - job.PermissionMs);
        job.Phase = "汇总目录与文件占用";
        var info = new SnapshotInfo(Guid.NewGuid().ToString("N"), string.Join("、", roots), roots,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0, 0, 0, job.Issues == 0 ? "complete" : "partial",
            string.Join(" + ", methods), 0);
        var snapshot = AnalysisSnapshot.Build(entries, info, issues, job.Cancellation.Token);
        long readyMs = Math.Max(0, timer.ElapsedMilliseconds - job.PermissionMs);
        info = snapshot.Metadata() with
        {
            DurationMs = readyMs,
            Timing = new(job.PermissionMs, readMs, readyMs - readMs, readyMs, Mft: job.MftStatistics.ToArray())
        };
        snapshot = snapshot.WithMetadata(info);
        Publish(snapshot);
        job.Result = info;
        return snapshot;
    }

    private void Publish(AnalysisSnapshot snapshot)
    {
        lock (stateLock)
        {
            saveStates[snapshot.Metadata().Id] = "pending";
            Volatile.Write(ref liveIndex, snapshot);
        }
    }

    public async Task Persist(AnalysisSnapshot snapshot, Job job)
    {
        await persistenceGate.WaitAsync(job.Cancellation.Token);
        string id = snapshot.Metadata().Id;
        try
        {
            if (File.Exists(DatabasePath(id)) && (State.Current == id || State.History.Any(item => item.Id == id)))
            {
                saveStates[id] = "saved";
                return;
            }
            if (snapshot.Metadata().Status == "updated" && Volatile.Read(ref liveIndex)?.Metadata().Id != id)
            {
                saveStates.TryRemove(id, out _);
                return;
            }
            var timer = Stopwatch.StartNew();
            job.Phase = "保存分析记录";
            saveStates[id] = "saving";
            TrackIndex(id);
            var store = new IndexStore(DatabasePath(id));
            if (File.Exists(store.FilePath))
            {
                File.Delete(store.FilePath);
            }

            store.Create();
            using (var writer = new IndexStore.Writer(store))
            {
                foreach (var row in snapshot.All())
                {
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    writer.Add(row.File);
                    job.Completed++;
                }
            }
            job.Phase = "建立历史查询索引";
            var info = snapshot.Metadata();
            store.FinalizeIndex(info, snapshot.Issues, job.Cancellation.Token);
            info = info with
            {
                Timing = info.Timing == null ? null : info.Timing with
                {
                    SaveMs = timer.ElapsedMilliseconds
                }
            };
            WriteMetadata(store, info);
            store.Seal();
            lock (stateLock)
            {
                bool current = liveIndex?.Metadata().Id == id;
                long pinnedBytes = State.History.Where(item => item.Pinned).Sum(item => new FileInfo(DatabasePath(item.Id)).Length);
                bool keepHistory = info.Status == "complete" && pinnedBytes + new FileInfo(store.FilePath).Length <= State.Preferences.HistoryBudget;
                string? previous = current ? State.Current : null;
                Update(state => state with
                {
                    Current = current ? id : state.Current,
                    History = keepHistory ? [.. state.History.Where(item => item.Id != id), info] : state.History,
                    Garbage = [.. (state.Garbage ?? []).Where(item => item != id),
                        .. previous != null ? new[] { previous } : Array.Empty<string>(),
                        .. !current && !keepHistory ? new[] { id } : Array.Empty<string>()]
                });
                if (current)
                {
                    Volatile.Write(ref liveIndex, snapshot.WithMetadata(info));
                }

                saveStates[id] = "saved";
                Prune();
                if (!keepHistory && info.Status == "complete")
                {
                    Notice = "历史存储预算不足，当前扫描已保留。可调整容量或导出快照。";
                }
            }
            job.Result = info;
        }
        catch
        {
            saveStates[id] = "failed";
            throw;
        }
        finally
        {
            FinishIndex(id);
            persistenceGate.Release();
        }
    }

    public void RefreshAffected(IEnumerable<string> paths, Job job)
    {
        var previous = Current();
        var info = previous.Metadata();
        if (info.Imported)
        {
            return;
        }

        string[] roots = Paths.Reduce(paths).Where(path => info.Roots.Any(root => Paths.Within(path, root))).ToArray();
        if (roots.Length == 0)
        {
            return;
        }

        bool Affected(string path) => roots.Any(root => path == root || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.Ordinal));
        var entries = previous.All().Where(row => !Affected(row.File.Path)).Select(row => row.File).ToList();
        var issues = new List<string>();
        foreach (string root in roots.Where(Directory.Exists))
        {
            foreach (var file in WindowsFiles.Enumerate(root, issues.Add, job.Cancellation.Token))
            {
                entries.Add(info.Roots.Contains(file.Path, StringComparer.Ordinal) ? file with
                {
                    Parent = ""
                } : file);
            }
        }

        var snapshot = AnalysisSnapshot.Build(entries, info with
        {
            Id = Guid.NewGuid().ToString("N"),
            Status = "updated",
            Method = "局部刷新",
            Pinned = false,
            Timing = null
        }, issues, job.Cancellation.Token);
        Publish(snapshot);
        if (PersistenceRequested != null)
        {
            PersistenceRequested(snapshot);
        }
        else
        {
            Persist(snapshot, new Job("snapshot-save")).GetAwaiter().GetResult();
        }
    }

    public async Task EnsureSaved()
    {
        var snapshot = Volatile.Read(ref liveIndex);
        if (snapshot != null && saveStates.GetValueOrDefault(snapshot.Metadata().Id) != "saved")
        {
            await Persist(snapshot, new Job("snapshot-save"));
        }
    }
}
