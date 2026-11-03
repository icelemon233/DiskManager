using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace DiskManager;

internal sealed record Request(string Id, string Method, JsonElement Params);
internal sealed record JobStarted(string JobId);

internal static class Program
{
    private static readonly object OutputLock = new();
    private static void Send(object value)
    {
        lock (OutputLock)
        {
            try
            {
                Console.WriteLine(Json.Text(value));
            }
            catch (IOException) { }
        }
    }

    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (args.FirstOrDefault() == "--mft-session")
            {
                await ScanBroker.RunSession(args);
                return 0;
            }
            if (args.FirstOrDefault() == "--mft-pipe")
            {
                await ScanBroker.RunHelper(args);
                return 0;
            }
            if (args.FirstOrDefault() == "--self-test")
            {
                await SelfTest.Run();
                return 0;
            }
            if (args.FirstOrDefault() == "--benchmark" && args.Length == 2)
            {
                Benchmark.Run(args[1]);
                return 0;
            }
            if (args.FirstOrDefault() == "--reindex-benchmark" && args.Length == 3)
            {
                Benchmark.Reindex(args[1], args[2]);
                return 0;
            }
            if (args.FirstOrDefault() == "--validate-index" && args.Length == 3)
            {
                Benchmark.Validate(args[1], args[2]);
                return 0;
            }
            if (args.FirstOrDefault() == "--memory-benchmark" && args.Length == 3)
            {
                Benchmark.Memory(args[1], args[2]);
                return 0;
            }
            if (args.FirstOrDefault() == "--ensure-exit")
            {
                int installDirectory = Array.IndexOf(args, "--install-dir");
                return await InstallLifecycle.EnsureExit(installDirectory >= 0 && installDirectory + 1 < args.Length ? args[installDirectory + 1] : "");
            }
            int option = Array.IndexOf(args, "--data-dir");
            string directory = option >= 0 && option + 1 < args.Length ? args[option + 1] :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskManager");
            int applicationOption = Array.IndexOf(args, "--application-dir");
            using var workspace = new Workspace(directory, applicationOption >= 0 && applicationOption + 1 < args.Length ? args[applicationOption + 1] : null);
            var worker = new Worker(workspace, Send);
            using var timerCancellation = new CancellationTokenSource();
            var updates = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
                try
                {
                    while (await timer.WaitForNextTickAsync(timerCancellation.Token))
                    {
                        if (worker.Busy)
                        {
                            Send(new
                            {
                                @event = "jobs",
                                data = worker.Jobs()
                            });
                        }
                    }
                }
                catch (OperationCanceledException) { }
            });
            var requests = new List<Task>();
            while (await Console.In.ReadLineAsync() is { } line)
            {
                if (line.Length > 8000000)
                {
                    Send(new
                    {
                        error = "请求过大。"
                    });
                    continue;
                }
                Request? request;
                try
                {
                    request = JsonSerializer.Deserialize<Request>(line, Json.Options);
                }
                catch (JsonException) { continue; }
                if (request == null || string.IsNullOrEmpty(request.Id))
                {
                    continue;
                }

                requests.RemoveAll(t => t.IsCompleted);
                requests.Add(Task.Run(async () =>
                {
                    try
                    {
                        Send(new
                        {
                            request.Id,
                            result = await worker.Call(request.Method, request.Params)
                        });
                    }
                    catch (Exception ex) { Send(new { request.Id, error = ex.Message }); }
                }));
            }
            await Task.WhenAll(requests);
            await worker.Close();
            timerCancellation.Cancel();
            await updates;
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}

internal sealed class Worker
{
    private readonly Workspace workspace;
    private readonly Duplicates duplicates;
    private readonly Operations operations;
    private readonly Caches caches;
    private readonly Action<object> send;
    private readonly ConcurrentDictionary<string, Job> jobs = new();
    private readonly ConcurrentDictionary<string, Task> tasks = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> queries = new();
    private readonly object queryLock = new();
    private readonly object authorizationLock = new();
    private readonly List<Job> updateSaves = [];
    private bool closing;
    public bool Busy => jobs.Values.Any(j => j.State is "running" or "queued");

    public Worker(Workspace workspace, Action<object> send)
    {
        this.workspace = workspace;
        this.send = send;
        duplicates = new(workspace);
        operations = new(workspace, duplicates);
        caches = new(workspace, operations);
        workspace.PersistenceRequested = QueueSave;
    }

    private JobStarted Start(string kind, Func<Job, Task> action, IDisposable? lease = null, bool serialized = true)
    {
        if (closing && kind != "snapshot-save")
        {
            throw new InvalidOperationException("应用正在保存并退出。");
        }

        if (kind != "snapshot-save" && jobs.Values.Count(job => job.State is "running" or "queued") >= 32)
        {
            lease?.Dispose();
            throw new InvalidOperationException("已有较多后台任务，请等待或取消部分任务。");
        }
        foreach (var old in jobs.Values.Where(j => j.State is "complete" or "failed" or "cancelled").OrderByDescending(j => j.Started).Skip(40).ToArray())
        {
            jobs.TryRemove(old.Id, out _);
            tasks.TryRemove(old.Id, out _);
            old.Cancellation.Dispose();
        }
        var job = new Job(kind) { State = "queued", Phase = "等待当前任务" };
        jobs[job.Id] = job;
        tasks[job.Id] = Task.Run(async () =>
        {
            bool acquired = false;
            try
            {
                if (serialized)
                {
                    await writeGate.WaitAsync(job.Cancellation.Token);
                    acquired = true;
                }
                job.State = "running";
                await action(job);
                job.State = "complete";
                job.Phase = "已完成";
            }
            catch (OperationCanceledException) { job.State = "cancelled"; job.Phase = "已取消"; }
            catch (Exception ex) { job.State = "failed"; job.Error = ex.Message; }
            finally
            {
                lease?.Dispose();
                if (acquired)
                {
                    writeGate.Release();
                }

                send(new
                {
                    @event = "jobs",
                    data = Jobs()
                });
            }
        });
        return new(job.Id);
    }

    private void QueueSave(AnalysisSnapshot snapshot)
    {
        lock (updateSaves)
        {
            bool updated = snapshot.Metadata().Status == "updated";
            if (updated)
            {
                foreach (var previous in updateSaves.Where(job => job.State is "queued" or "running"))
                {
                    previous.Cancellation.Cancel();
                }

                updateSaves.Clear();
            }
            var started = Start("snapshot-save", job => workspace.Persist(snapshot, job), serialized: false);
            if (updated)
            {
                updateSaves.Add(jobs[started.JobId]);
            }
        }
    }

    public object[] Jobs() => jobs.Values.OrderByDescending(j => j.Started).Take(30).Select(j => j.Public()).ToArray();
    private static string Text(JsonElement p, string key, string fallback = "") => p.TryGetProperty(key, out var value) ? value.GetString() ?? fallback : fallback;
    private static int Number(JsonElement p, string key, int fallback = 0) => p.TryGetProperty(key, out var value) ? value.GetInt32() : fallback;

    public async Task<object?> Call(string method, JsonElement p)
    {
        switch (method)
        {
            case "meta":
                return new
                {
                    version = typeof(Program).Assembly.GetName().Version?.ToString(3),
                    product = "DiskManager",
                    dataDirectory = workspace.DirectoryPath
                };
            case "overview":
                return workspace.Overview();
            case "scan.authorize":
                lock (authorizationLock)
                {
                    var authorizationJob = jobs.Values.FirstOrDefault(job => job.Kind == "authorization" && job.State is "running" or "queued");
                    return authorizationJob == null ? Start("authorization", workspace.AuthorizeScanning, serialized: false) : new JobStarted(authorizationJob.Id);
                }
            case "scan.start":
                return Start("scan", async job => QueueSave(await workspace.Analyze(Json.Read<ScanRequest>(p), job)));
            case "scan.refresh":
                return Start("refresh", job =>
                Task.Run(() => workspace.RefreshAffected(Json.Read<string[]>(p.GetProperty("paths")), job)));
            case "query":
                return Query(Json.Read<QueryRequest>(p));
            case "summary":
                return workspace.Current(Text(p, "snapshot") is { Length: > 0 } id ? id : null).Summary(Text(p, "scope") is { Length: > 0 } scope ? scope : null);
            case "entry":
                return workspace.Current().Find(Text(p, "path"));
            case "types":
                return workspace.Current().Types(Text(p, "scope") is { Length: > 0 } typeScope ? typeScope : null, Number(p, "offset"));
            case "tree":
                return AnalysisViews.Tree(workspace.Current(), Json.Read<TreeRequest>(p));
            case "treemap":
                return AnalysisViews.Map(workspace.Current(), Text(p, "scope") is { Length: > 0 } mapScope ? mapScope : null, Text(p, "metric", "allocated"));
            case "selection":
                return AnalysisViews.Selection(workspace.Current(), Json.Read<string[]>(p.GetProperty("paths")));
            case "history.list":
                return workspace.State.History.OrderByDescending(h => h.Created).ToArray();
            case "history.open":
                string selected = Text(p, "id");
                workspace.OpenHistory(selected);
                return workspace.Overview();
            case "history.update":
                workspace.UpdateHistory(Text(p, "id"), Text(p, "label"), p.TryGetProperty("pinned", out var pinned) ? pinned.GetBoolean() : null,
                    p.TryGetProperty("delete", out var delete) && delete.GetBoolean());
                return workspace.State.History;
            case "history.diff":
                return workspace.Difference(Text(p, "older"), Text(p, "newer"), Text(p, "search"), Number(p, "offset"));
            case "duplicates.estimate":
                return duplicates.Estimate(Json.Read<DuplicateRequest>(p));
            case "duplicates.start":
                return Start("duplicates", job => Task.Run(() => duplicates.Run(Json.Read<DuplicateRequest>(p), job)));
            case "duplicates.list":
                return duplicates.List(Number(p, "offset"));
            case "operations.plan":
                return Start("plan", job =>
            {
                job.Result = operations.Plan(Json.Read<OperationRequest>(p), cancellation: job.Cancellation.Token);
                return Task.CompletedTask;
            });
            case "operations.read":
                return operations.Read(Text(p, "id"));
            case "operations.execute":
                return Start("operations", job => operations.Execute(Text(p, "id"), job));
            case "operations.history":
                return operations.History();
            case "cache.list":
                return await caches.List();
            case "cache.preview":
                return Start("cache-preview", job => { job.Result = caches.Preview(Text(p, "id"), job.Cancellation.Token); return Task.CompletedTask; });
            case "cache.plan":
                return caches.Read(Text(p, "id"));
            case "cache.execute":
                return Start("cache", job => caches.Execute(Text(p, "id"), job));
            case "rules.save":
                return caches.SaveRule(Json.Read<Rule>(p));
            case "rules.delete":
                workspace.Update(state => state with { Rules = state.Rules.Where(r => r.Id != Text(p, "id")).ToList() });
                return true;
            case "rules.preview":
                return Start("plan", job => { job.Result = caches.PreviewRule(Text(p, "id"), job.Cancellation.Token); return Task.CompletedTask; });
            case "preferences.save":
                var preferences = Json.Read<Preferences>(p);
                if (preferences.RetainCount is < 1 or > 1000 || preferences.HistoryBudget < 268435456 || preferences.HistoryBudget > 1099511627776)
                {
                    throw new ArgumentException("历史条数或容量上限无效。");
                }

                workspace.Update(state => state with { Preferences = preferences });
                workspace.Prune();
                return preferences;
            case "export.start":
                string destination = Paths.Normalize(Text(p, "destination"));
                if (operations.Protected(destination))
                {
                    throw new ArgumentException("请选择文档、下载或其他用户目录保存报告。");
                }

                var exportQuery = Json.Read<QueryRequest>(p.GetProperty("query"));
                var exportStore = workspace.Current(exportQuery.Snapshot);
                var exportLease = exportStore.FilePath == null ? null : new FileStream(exportStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Start("export", job => workspace.Export(destination, Text(p, "kind"), exportQuery, job, exportStore), exportLease);
            case "export.validate":
                if (operations.Protected(Paths.Normalize(Text(p, "destination"))))
                {
                    throw new ArgumentException("请选择文档、下载或其他用户目录保存报告。");
                }

                return true;
            case "import.start":
                return Start("import", job => workspace.Import(Text(p, "source"), job));
            case "jobs.list":
                return Jobs();
            case "jobs.result":
                return jobs.TryGetValue(Text(p, "id"), out var completed) ? completed.Public(true) : throw new ArgumentException("任务结果已过期。");
            case "jobs.cancel":
                if (jobs.TryGetValue(Text(p, "id"), out var active))
                {
                    if (active.Kind == "snapshot-save")
                    {
                        throw new InvalidOperationException("分析记录正在后台保存，可继续使用应用。");
                    }

                    active.Cancellation.Cancel();
                }

                return true;
            case "shutdown.prepare":
                await Close();
                return true;
            default:
                throw new ArgumentException("不受支持的操作。");
        }
    }

    public async Task Close()
    {
        closing = true;
        foreach (var job in jobs.Values.Where(j => j.Kind != "snapshot-save" && j.State is "running" or "queued"))
        {
            job.Cancellation.Cancel();
        }

        while (tasks.Values.Any(task => !task.IsCompleted))
        {
            await Task.WhenAll(tasks.Values.ToArray());
        }

        try
        {
            await workspace.EnsureSaved();
            await workspace.CloseScanner();
        }
        catch { closing = false; throw; }
    }

    private object Query(QueryRequest request)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string? channel = request.Channel is { Length: > 0 and <= 100 } ? request.Channel : null;
        if (channel != null)
        {
            lock (queryLock)
            {
                if (queries.TryGetValue(channel, out var old))
                {
                    old.Cancel();
                }

                queries[channel] = cancellation;
            }
        }

        try
        {
            return workspace.Current(request.Snapshot).Query(request, cancellation.Token);
        }
        finally
        {
            if (channel != null)
            {
                lock (queryLock)
                {
                    if (queries.TryGetValue(channel, out var current) && current == cancellation)
                    {
                        queries.Remove(channel);
                    }
                }
            }
        }
    }
}
