using System.Diagnostics;
using System.Text.Json;

namespace DiskManager;

internal sealed record WorkspaceState(Preferences Preferences, List<SnapshotInfo> History,
    List<Rule> Rules, string? Current = null, int Schema = 1, List<string>? Garbage = null);

internal sealed partial class Workspace : IDisposable
{
    public string DirectoryPath
    {
        get;
    }
    public string ApplicationDirectory
    {
        get;
    }
    public string IndexDirectory
    {
        get;
    }
    public WorkspaceState State
    {
        get; private set;
    }
    private readonly object stateLock = new();
    private readonly string statePath;
    private readonly FileStream workspaceLock;
    private AnalysisSnapshot? liveIndex;
    private readonly ScanBroker scanBroker = new();
    private readonly SemaphoreSlim persistenceGate = new(1, 1);
    private readonly HashSet<string> inFlight = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> saveStates = new();
    public Action<AnalysisSnapshot>? PersistenceRequested
    {
        get; set;
    }
    public string? Notice
    {
        get; private set;
    }

    public Workspace(string directory, string? applicationDirectory = null)
    {
        DirectoryPath = Path.GetFullPath(directory);
        ApplicationDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory).TrimEnd('\\');
        IndexDirectory = Path.Combine(DirectoryPath, "indexes");
        Directory.CreateDirectory(IndexDirectory);
        workspaceLock = new FileStream(Path.Combine(DirectoryPath, "workspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        statePath = Path.Combine(DirectoryPath, "state.json");
        State = File.Exists(statePath)
            ? JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(statePath), Json.Options)
                ?? throw new InvalidDataException("本地配置无法读取，原文件已保留。")
            : new(new(), [], []);
        if (State.Schema != 1)
        {
            throw new InvalidDataException("本地配置版本不受支持，原文件已保留。");
        }

        if (State.History == null || State.Rules == null || State.Preferences == null ||
            State.History.Any(s => !Guid.TryParseExact(s.Id, "N", out _) || s.Roots == null) ||
            State.Garbage?.Any(id => !Guid.TryParseExact(id, "N", out _)) == true ||
            State.Current != null && !Guid.TryParseExact(State.Current, "N", out _))
        {
            throw new InvalidDataException("本地配置格式无效，原文件已保留。");
        }

        CleanupRetired();
        string pending = Path.Combine(DirectoryPath, "pending-operations");
        if (Directory.Exists(pending) && Directory.EnumerateFiles(pending, "*.json").Any())
        {
            Notice = "上次文件操作中断，请重新扫描相关目录并查看操作记录。";
        }
    }

    public void Dispose() => workspaceLock.Dispose();

    public Task AuthorizeScanning(Job job) => scanBroker.Authorize(job, retry: true);
    public Task CloseScanner() => scanBroker.Close();

    private void TrackIndex(string id)
    {
        lock (stateLock)
        {
            inFlight.Add(id);
            Update(state => state with { Garbage = [.. state.Garbage ?? [], id] });
        }
    }

    private void FinishIndex(string id)
    {
        lock (stateLock)
        {
            inFlight.Remove(id);
        }

        CleanupRetired();
    }

    private void CleanupRetired()
    {
        lock (stateLock)
        {
            if (State.Garbage is not { Count: > 0 })
            {
                return;
            }

            var remaining = new List<string>();
            foreach (string id in State.Garbage.Distinct())
            {
                if (inFlight.Contains(id))
                {
                    remaining.Add(id);
                    continue;
                }
                if (State.Current == id || State.History.Any(s => s.Id == id))
                {
                    continue;
                }

                try
                {
                    File.Delete(DatabasePath(id));
                    File.Delete(DatabasePath(id) + "-journal");
                }
                catch (IOException) { remaining.Add(id); }
                catch (UnauthorizedAccessException) { remaining.Add(id); }
            }
            Update(state => state with { Garbage = remaining });
            if (remaining.Count > 0)
            {
                Notice = "部分旧索引仍被占用，释放后会再次清理。";
            }
        }
    }

    public void Update(Func<WorkspaceState, WorkspaceState> change)
    {
        lock (stateLock)
        {
            var next = change(State);
            Paths.AtomicJson(statePath, next);
            State = next;
        }
    }

    public string DatabasePath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new ArgumentException("记录标识无效。");
        }

        return Path.Combine(IndexDirectory, id + ".db");
    }

    public IAnalysisIndex Current(string? snapshot = null)
    {
        var live = Volatile.Read(ref liveIndex);
        return live != null && (snapshot == null || snapshot == live.Metadata().Id) ? live : Stored(snapshot);
    }

    public IndexStore Stored(string? snapshot = null)
    {
        string id = snapshot ?? State.Current ?? throw new InvalidOperationException("请先扫描磁盘或打开历史记录。");
        if (snapshot != null && snapshot != State.Current && !State.History.Any(h => h.Id == snapshot))
        {
            throw new ArgumentException("历史记录不存在。");
        }

        var store = new IndexStore(DatabasePath(id));
        if (!File.Exists(store.FilePath))
        {
            throw new FileNotFoundException("扫描索引不存在，请重新扫描。");
        }

        return store;
    }

    public object Overview()
    {
        var drives = DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable).Select(d =>
        {
            try
            {
                return (object)new
                {
                    root = d.Name,
                    label = d.VolumeLabel,
                    format = d.DriveFormat,
                    total = d.TotalSize,
                    free = d.AvailableFreeSpace,
                    ready = d.IsReady
                };
            }
            catch (IOException) { return new { root = d.Name, label = "", format = "", total = 0L, free = 0L, ready = false }; }
        }).ToArray();
        SnapshotInfo? current = Volatile.Read(ref liveIndex)?.Metadata();
        if (current == null && State.Current != null && File.Exists(DatabasePath(State.Current)))
        {
            current = State.History.FirstOrDefault(s => s.Id == State.Current) ?? Current().Metadata();
        }

        return new
        {
            drives,
            scanAuthorization = scanBroker.Authorization,
            current,
            saveState = current == null ? "saved" : saveStates.GetValueOrDefault(current.Id, "saved"),
            State.Preferences,
            State.Rules,
            Notice
        };
    }

    public void OpenHistory(string id)
    {
        Stored(id);
        lock (stateLock)
        {
            Volatile.Write(ref liveIndex, null);
            Update(state => state with { Current = id });
        }
    }

    public static void WriteMetadata(IndexStore store, SnapshotInfo metadata)
    {
        using var connection = store.Open(true);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO metadata(key,value) VALUES('scan',$value)";
        command.Parameters.AddWithValue("$value", Json.Text(metadata));
        command.ExecuteNonQuery();
    }

    public void Prune()
    {
        lock (stateLock)
        {
            var history = State.History.OrderByDescending(s => s.Created).ToList();
            var kept = new List<SnapshotInfo>();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            long bytes = history.Where(s => s.Pinned || s.Id == State.Current).Sum(s => new FileInfo(DatabasePath(s.Id)).Length);
            foreach (var snapshot in history)
            {
                string scope = string.Join('|', snapshot.Roots.Order(StringComparer.OrdinalIgnoreCase));
                counts.TryGetValue(scope, out int count);
                long size = new FileInfo(DatabasePath(snapshot.Id)).Length;
                bool protectedRecord = snapshot.Pinned || snapshot.Id == State.Current;
                if (protectedRecord || count < State.Preferences.RetainCount && bytes + size <= State.Preferences.HistoryBudget)
                {
                    kept.Add(snapshot);
                    counts[scope] = count + 1;
                    if (!protectedRecord)
                    {
                        bytes += size;
                    }
                }
            }
            var removed = State.History.Where(s => kept.All(k => k.Id != s.Id)).ToArray();
            Update(state => state with { History = kept, Garbage = [.. state.Garbage ?? [], .. removed.Select(s => s.Id)] });
            CleanupRetired();
            Notice = bytes > State.Preferences.HistoryBudget ? "固定记录及当前索引已达到历史容量上限，请调整保留设置。" : null;
        }
    }

    public void UpdateHistory(string id, string? label, bool? pinned, bool delete)
    {
        if (!State.History.Any(h => h.Id == id))
        {
            throw new ArgumentException("历史记录不存在。");
        }

        if (delete && State.Current == id)
        {
            throw new InvalidOperationException("请先切换当前扫描，再移除此记录。");
        }

        Update(state => state with
        {
            History = delete ? state.History.Where(h => h.Id != id).ToList() :
            state.History.Select(h => h.Id == id ? h with { Label = label?.Trim() is { Length: > 0 } text ? text[..Math.Min(text.Length, 200)] : h.Label, Pinned = pinned ?? h.Pinned } : h).ToList(),
            Garbage = delete ? [.. state.Garbage ?? [], id] : state.Garbage
        });
        Prune();
    }
}
