using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DiskManager;

internal sealed record OperationRequest(string Action, string[] Paths, string? Destination = null, string? Name = null, bool DuplicateSelection = false);
internal sealed record OperationItem(string Path, string? Destination, long Size, long Allocated, string? Error);
internal sealed record OperationPlan(string Id, string Action, long Created, OperationItem[] Items, string[] Warnings);
internal sealed record OperationResult(string Path, string? Destination, string State, string? Error, int CompletedEntries = 0);

internal sealed class Operations(Workspace workspace, Duplicates duplicates)
{
    private sealed record Prepared(OperationPlan Preview, Dictionary<string, FileEntry> Manifest,
        List<(FileEntry Keeper, string Hash)> Keepers, string? CacheRoot);
    private readonly ConcurrentDictionary<string, Prepared> plans = new();
    private readonly ConcurrentDictionary<string, bool> executing = new();
    private readonly string logPath = Path.Combine(workspace.DirectoryPath, "operations.jsonl");
    private readonly string pendingDirectory = Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "pending-operations")).FullName;
    private sealed class Progress
    {
        public int Changed; public bool Published;
    }

    public bool Protected(string path, string? cacheRoot = null)
    {
        path = Paths.Normalize(path);
        if (File.Exists(path) || Directory.Exists(path))
        {
            path = WindowsFiles.Canonical(path);
        }

        if (path.Equals(Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] essential = [workspace.DirectoryPath, workspace.ApplicationDirectory, AppContext.BaseDirectory.TrimEnd('\\'),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)];
        if (Paths.Within(path, workspace.DirectoryPath) || Paths.Within(workspace.DirectoryPath, path) ||
            Paths.Within(path, AppContext.BaseDirectory.TrimEnd('\\')) || Paths.Within(AppContext.BaseDirectory.TrimEnd('\\'), path))
        {
            return true;
        }

        string windowsTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        string firstComponent = path[Path.GetPathRoot(path)!.Length..].Split('\\')[0];
        if (new[] { "$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", "$Bitmap", "$Boot", "$BadClus", "$Secure", "$UpCase", "$Extend" }
            .Contains(firstComponent, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (cacheRoot != null && Paths.Normalize(cacheRoot).Equals(windowsTemp, StringComparison.OrdinalIgnoreCase)
            && Paths.Within(path, windowsTemp) && !path.Equals(windowsTemp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return essential.Where(p => p.Length > 0).Any(p => Paths.Within(path, p) || Paths.Within(p, path)) ||
            path.Split('\\').Any(p => p.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || p.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase));
    }

    private void Verify(FileEntry expected, string? cacheRoot)
    {
        if (Protected(expected.Path, cacheRoot))
        {
            throw new IOException("此位置启用了只读保护。");
        }

        if (WindowsFiles.HasLinkAncestor(expected.Path))
        {
            throw new IOException("路径包含重解析链接，请定位到实际目录后操作。");
        }

        if (!Duplicates.SameFile(WindowsFiles.Inspect(expected.Path), expected))
        {
            throw new IOException("文件在预览后发生变化，请重新生成清单。");
        }
    }

    public OperationPlan Plan(OperationRequest request, string? cacheRoot = null, bool useIndex = true, CancellationToken cancellation = default, IEnumerable<string>? notes = null)
    {
        if (!new[] { "recycle", "delete", "move", "rename" }.Contains(request.Action))
        {
            throw new ArgumentException("操作类型无效。");
        }

        string[] paths = Paths.Reduce(request.Paths);
        if (paths.Length is < 1 or > 10000)
        {
            throw new ArgumentException("请选择 1 到 10000 个项目。");
        }

        if (request.Action == "rename" && paths.Length != 1)
        {
            throw new ArgumentException("重命名需要选择一个项目。");
        }

        var current = cacheRoot == null && useIndex ? workspace.Current() : null;
        if (current?.Metadata().Imported == true)
        {
            throw new InvalidOperationException("导入快照中的项目需要先在本机重新扫描。");
        }

        string? destination = request.Action == "move" ? Paths.Normalize(request.Destination ?? "") : null;
        if (destination != null && (!Directory.Exists(destination) || Protected(destination) || WindowsFiles.HasLinkAncestor(destination)))
        {
            throw new ArgumentException("目标目录不可用或受保护。");
        }

        if (request.Action == "rename" && (string.IsNullOrWhiteSpace(request.Name) || request.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            request.Name is "." or ".." || request.Name.EndsWith('.') || request.Name.EndsWith(' ')))
        {
            throw new ArgumentException("请输入有效的新名称。");
        }

        if (request.Action == "rename" && System.Text.RegularExpressions.Regex.IsMatch(request.Name!.Split('.')[0],
            "^(CON|PRN|AUX|NUL|CONIN\\$|CONOUT\\$|COM[1-9¹²³]|LPT[1-9¹²³])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            throw new ArgumentException("该名称是 Windows 保留的设备名。");
        }

        var manifest = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        var items = new List<OperationItem>();
        var warnings = new HashSet<string>(notes ?? []);
        foreach (string path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            long size = 0, allocated = 0;
            string? target = request.Action switch
            {
                "move" => Path.Combine(destination!, Path.GetFileName(path)),
                "rename" => Path.Combine(Path.GetDirectoryName(path)!, request.Name!),
                _ => null
            };
            string? error = null;
            try
            {
                if (current != null && current.Find(path) == null)
                {
                    throw new IOException("该项目不在当前扫描结果中。");
                }

                if (target != null && (File.Exists(target) || Directory.Exists(target) || Paths.Within(target, path)))
                {
                    throw new IOException("目标已存在或位于源目录内，请选择其他位置或名称。");
                }

                var root = WindowsFiles.Inspect(path);
                Verify(root, cacheRoot);
                var files = root.Directory ? WindowsFiles.Enumerate(path, message => throw new IOException(message), cancellation) : [root];
                foreach (var file in files)
                {
                    Verify(file, cacheRoot);
                    if (manifest.Count >= 1000000)
                    {
                        throw new IOException("本次清单超过一百万项，请分批选择。");
                    }

                    manifest[file.Path] = file;
                    size += file.Size;
                    if (file.Links <= 1)
                    {
                        allocated += file.Allocated;
                    }

                    if (file.OnlineOnly)
                    {
                        warnings.Add("选择中包含在线文件，移动或校验可能下载内容。");
                    }
                }
                foreach (string variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
                {
                    if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } cloud && Paths.Within(path, cloud))
                    {
                        warnings.Add("选择中包含 OneDrive 文件，删除与移动会同步到云端和其他设备。");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }
            items.Add(new(path, target, size, allocated, error));
        }
        if (request.Action == "recycle")
        {
            warnings.Add("移入回收站后仍占磁盘空间，可在系统回收站恢复或清空。");
        }

        var keepers = request.DuplicateSelection ? duplicates.KeepersFor(paths) : [];
        var plan = new OperationPlan(Guid.NewGuid().ToString("N"), request.Action, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), items.ToArray(), warnings.ToArray());
        foreach (var expired in plans.Where(p => p.Value.Preview.Created < DateTimeOffset.UtcNow.AddMinutes(-15).ToUnixTimeMilliseconds()).ToArray())
        {
            plans.TryRemove(expired.Key, out _);
        }

        if (plans.Count >= 20)
        {
            throw new InvalidOperationException("待确认清单较多，请完成或取消已有清单。");
        }

        plans[plan.Id] = new(plan, manifest, keepers, cacheRoot);
        return plan;
    }

    public OperationPlan Read(string id) => plans.TryGetValue(id, out var plan) ? plan.Preview : throw new ArgumentException("操作清单已失效。");

    public async Task Execute(string id, Job job)
    {
        if (!plans.TryRemove(id, out var plan) || plan.Preview.Created < DateTimeOffset.UtcNow.AddMinutes(-15).ToUnixTimeMilliseconds())
        {
            throw new InvalidOperationException("操作清单已失效，请重新预览。");
        }

        job.Total = plan.Preview.Items.Length;
        var results = new List<OperationResult>();
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var keeperLocks = new List<FileStream>();
        string pendingPath = Path.Combine(pendingDirectory, id + ".json");
        Paths.AtomicJson(pendingPath, plan.Preview);
        executing[id] = true;
        try
        {
            foreach (var keeper in plan.Keepers)
            {
                keeperLocks.Add(new FileStream(keeper.Keeper.Path, FileMode.Open, FileAccess.Read, FileShare.Read));
                if (Duplicates.Hash(keeper.Keeper, job.Cancellation.Token) != keeper.Hash)
                {
                    throw new IOException("保留副本已变化，请重新校验重复文件。");
                }
            }
            foreach (var item in plan.Preview.Items)
            {
                job.Cancellation.Token.ThrowIfCancellationRequested();
                job.Phase = item.Path;
                if (item.Error != null)
                {
                    results.Add(new(item.Path, item.Destination, "skipped", item.Error));
                    job.Completed++;
                    continue;
                }
                var progress = new Progress();
                try
                {
                    var entries = plan.Manifest.Values.Where(f => f.Path.Equals(item.Path, StringComparison.Ordinal) ||
                        f.Path.StartsWith(item.Path.TrimEnd('\\') + "\\", StringComparison.Ordinal)).ToArray();
                    VerifyTree(item.Path, entries, plan.CacheRoot);
                    using var contentLock = plan.Keepers.Count > 0 ? new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete) : null;
                    if (plan.Keepers.Count > 0)
                    {
                        string digest = Duplicates.Hash(plan.Manifest[item.Path], job.Cancellation.Token);
                        if (!plan.Keepers.Any(k => k.Hash == digest))
                        {
                            throw new IOException("待清理文件内容已变化。");
                        }
                    }
                    if (plan.Preview.Action == "recycle")
                    {
                        await Recycle.Send(item.Path, path =>
                        {
                            if (!plan.Manifest.TryGetValue(path, out var expected))
                            {
                                throw new IOException("项目已变化，请重新预览。");
                            }

                            Verify(expected, plan.CacheRoot);
                        }, job.Cancellation.Token);
                    }
                    else if (plan.Preview.Action == "delete")
                    {
                        DeleteTree(entries, plan.CacheRoot, job.Cancellation.Token, progress);
                    }
                    else
                    {
                        await Move(item, entries, plan.CacheRoot, job, progress);
                    }

                    results.Add(new(item.Path, item.Destination, "complete", null, progress.Changed));
                }
                catch (OperationCanceledException)
                {
                    results.Add(new(item.Path, item.Destination, progress.Changed > 0 || progress.Published ? "partial" : "cancelled",
                        $"操作已取消，已处理 {progress.Changed} 个条目。请重新扫描核对。", progress.Changed));
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results.Add(new(item.Path, item.Destination, progress.Changed > 0 || progress.Published ? "partial" : "failed", ex.Message, progress.Changed));
                    job.Issues++;
                }
                affected.Add(Path.GetDirectoryName(item.Path)!);
                if (item.Destination != null)
                {
                    affected.Add(Path.GetDirectoryName(item.Destination)!);
                }

                job.Completed++;
            }
        }
        finally
        {
            executing.TryRemove(id, out _);
            foreach (var handle in keeperLocks)
            {
                handle.Dispose();
            }

            var record = new
            {
                id,
                action = plan.Preview.Action,
                time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                results
            };
            await File.AppendAllTextAsync(logPath, Json.Text(record) + "\n");
            File.Delete(pendingPath);
            job.Result = record;
            if (affected.Count > 0 && !job.Cancellation.IsCancellationRequested && workspace.State.Current != null)
            {
                job.Phase = "更新受影响目录";
                try
                {
                    workspace.RefreshAffected(affected, job);
                }
                catch (Exception ex) { job.Error = "操作结果已记录，局部刷新失败 — " + ex.Message; }
            }
        }
    }

    private void VerifyTree(string root, FileEntry[] expected, string? cacheRoot)
    {
        foreach (var entry in expected)
        {
            Verify(entry, cacheRoot);
        }

        if (expected[0].Directory || Directory.Exists(root))
        {
            var known = expected.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var current in WindowsFiles.Enumerate(root, m => throw new IOException(m), CancellationToken.None))
            {
                if (!known.Remove(current.Path))
                {
                    throw new IOException("目录内容在预览后发生变化。");
                }
            }

            if (known.Count != 0)
            {
                throw new IOException("目录内容在预览后发生变化。");
            }
        }
    }

    private void DeleteTree(FileEntry[] entries, string? cacheRoot, CancellationToken cancellation, Progress progress,
        Dictionary<string, string>? copiedHashes = null)
    {
        foreach (var file in entries.Where(f => !f.Directory))
        {
            cancellation.ThrowIfCancellationRequested();
            using var contentLock = copiedHashes != null ? new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete) : null;
            Verify(file, cacheRoot);
            if (copiedHashes != null && Duplicates.Hash(file, cancellation) != copiedHashes[file.Path])
            {
                throw new IOException("源文件在复制后发生变化，已保留源文件与目标副本。");
            }

            WindowsFiles.DeleteVerified(file);
            progress.Changed++;
        }
        foreach (var directory in entries.Where(f => f.Directory).OrderByDescending(f => f.Path.Length))
        {
            cancellation.ThrowIfCancellationRequested();
            if (Protected(directory.Path, cacheRoot) || WindowsFiles.HasLinkAncestor(directory.Path))
            {
                throw new IOException("目录保护状态已变化。");
            }

            if (WindowsFiles.Inspect(directory.Path).Identity != directory.Identity)
            {
                throw new IOException("目录身份已变化。");
            }

            WindowsFiles.DeleteVerified(directory);
            progress.Changed++;
        }
    }

    private delegate uint CopyProgress(long total, long transferred, long streamSize, long streamTransferred,
        uint streamNumber, uint reason, IntPtr source, IntPtr destination, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CopyFileEx(string source, string destination, CopyProgress progress, IntPtr data, IntPtr cancel, uint flags);

    private async Task Move(OperationItem item, FileEntry[] entries, string? cacheRoot, Job job, Progress progress)
    {
        string destination = item.Destination!;
        string parent = Path.GetDirectoryName(destination)!;
        if (Protected(parent) || WindowsFiles.HasLinkAncestor(parent) || File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException("目标位置已变化或存在同名项目。");
        }

        bool directory = Directory.Exists(item.Path);
        if (Path.GetPathRoot(item.Path)!.Equals(Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            WindowsFiles.RenameVerified(entries.Single(entry => entry.Path.Equals(item.Path, StringComparison.Ordinal)), destination);
            progress.Changed++;
            return;
        }
        string staging = Path.Combine(parent, ".DiskManager-" + Guid.NewGuid().ToString("N") + ".partial");
        bool published = false;
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (directory)
            {
                Directory.CreateDirectory(staging);
            }

            foreach (var entry in entries.OrderBy(f => f.Path.Length))
            {
                job.Cancellation.Token.ThrowIfCancellationRequested();
                string target = directory ? Path.Combine(staging, Path.GetRelativePath(item.Path, entry.Path)) : staging;
                if (entry.Directory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Verify(entry, cacheRoot);
                string digest = Duplicates.Hash(entry, job.Cancellation.Token);
                CopyProgress copyProgress = (_, _, _, _, _, _, _, _, _) => job.Cancellation.IsCancellationRequested ? 1u : 0u;
                if (!CopyFileEx(WindowsFiles.ExtendedPath(entry.Path), WindowsFiles.ExtendedPath(target), copyProgress, IntPtr.Zero, IntPtr.Zero, 1))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }

                if (Duplicates.Hash(WindowsFiles.Inspect(target), job.Cancellation.Token) != digest)
                {
                    throw new IOException("目标文件校验失败，源文件已保留。");
                }

                Verify(entry, cacheRoot);
                hashes.Add(entry.Path, digest);
                await Task.Yield();
            }
            VerifyTree(item.Path, entries, cacheRoot);
            if (directory)
            {
                Directory.Move(staging, destination);
            }
            else
            {
                File.Move(staging, destination, false);
            }

            published = true;
            progress.Published = true;
            DeleteTree(entries, cacheRoot, job.Cancellation.Token, progress, hashes);
        }
        finally
        {
            if (!published)
            {
                // Only the unique staging tree created by this operation is eligible for cleanup.
                if (!Paths.Within(staging, parent) || !Path.GetFileName(staging).StartsWith(".DiskManager-", StringComparison.Ordinal))
                {
                    throw new IOException("暂存目录范围校验失败。");
                }

                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
                else if (File.Exists(staging))
                {
                    File.Delete(staging);
                }
            }
        }
    }

    public object[] History()
    {
        var records = File.Exists(logPath) ? File.ReadLines(logPath).TakeLast(100).Reverse()
            .Select(line => (object)JsonSerializer.Deserialize<JsonElement>(line)).ToList() : [];
        foreach (string file in Directory.EnumerateFiles(pendingDirectory, "*.json"))
        {
            var plan = JsonSerializer.Deserialize<OperationPlan>(File.ReadAllText(file), Json.Options);
            if (plan == null)
            {
                continue;
            }

            records.Insert(0, new
            {
                plan.Id,
                plan.Action,
                time = plan.Created,
                results = plan.Items.Select(item => new OperationResult(item.Path, item.Destination, executing.ContainsKey(plan.Id) ? "running" : "interrupted", executing.ContainsKey(plan.Id) ? "正在执行。" : "操作中断，请重新扫描核对实际状态。"))
            });
        }
        return records.ToArray();
    }
}
