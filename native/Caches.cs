using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace DiskManager;

internal sealed record CacheTarget(string Id, string Name, string Kind, string Root, string Impact, string? Tool = null,
    string[]? Arguments = null, string? Process = null);
internal sealed record CachePlan(string Id, CacheTarget Target, long Created);

internal sealed class Caches(Workspace workspace, Operations operations)
{
    private readonly ConcurrentDictionary<string, CacheTarget> targets = new();
    private readonly ConcurrentDictionary<string, CachePlan> plans = new();

    private static string? Tool(string name)
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').ToList();
        directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        if (Environment.GetEnvironmentVariable("PNPM_HOME") is { Length: > 0 } pnpmHome)
        {
            directories.Add(pnpmHome);
        }

        if (Environment.GetEnvironmentVariable("UV_INSTALL_DIR") is { Length: > 0 } uvHome)
        {
            directories.Add(uvHome);
        }

        if (name == "dotnet")
        {
            directories.Insert(0, Path.Combine("D:\\Development", "dotnet"));
        }

        foreach (var directory in directories.Where(Directory.Exists))
        {
            if (directory.Contains("\\Microsoft\\WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string extension in new[] { ".exe", ".cmd", ".bat" })
            {
                string path = Path.Combine(directory.Trim('"'), name + extension);
                if (File.Exists(path) && path.IndexOfAny("&|<>^%!\"\r\n".ToCharArray()) < 0)
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static async Task<string> Run(string tool, string[] arguments, bool readOnly = false, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        start.Environment["CI"] = "1";
        start.Environment["COREPACK_ENABLE_NETWORK"] = "0";
        start.Environment["COREPACK_ENABLE_DOWNLOAD_PROMPT"] = "0";
        start.Environment["NPM_CONFIG_UPDATE_NOTIFIER"] = "false";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        if (environment != null)
        {
            foreach (var value in environment)
            {
                start.Environment[value.Key] = value.Value;
            }
        }

        if (Path.GetExtension(tool).Equals(".cmd", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(tool).Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // Tool paths are resolved locally, arguments are fixed constants defined below.
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            start.Arguments = "/d /s /c \"\"" + tool + "\" " + string.Join(' ', arguments) + "\"";
        }
        else
        {
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
        }

        using var process = Process.Start(start) ?? throw new IOException("无法启动缓存工具。");
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource();
        if (readOnly)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
        }

        var output = ReadOutput(process.StandardOutput, timeout.Token);
        var errors = ReadOutput(process.StandardError, timeout.Token);
        string stdout, stderr;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            stdout = await output;
            stderr = await errors;
        }
        catch (OperationCanceledException) when (readOnly)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            try
            {
                await Task.WhenAll(output, errors);
            }
            catch (OperationCanceledException) { }
            throw new TimeoutException("缓存路径查询超时。");
        }
        if (process.ExitCode != 0)
        {
            throw new IOException((stderr.Length > 0 ? stderr : stdout)[..Math.Min((stderr.Length > 0 ? stderr : stdout).Length, 4000)]);
        }

        return stdout.Length > 8000 ? stdout[..8000] : stdout;
    }

    private static async Task<string> ReadOutput(StreamReader reader, CancellationToken cancellation)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellation)) > 0)
        {
            if (result.Length < 8000)
            {
                result.Append(buffer, 0, Math.Min(count, 8000 - result.Length));
            }
        }

        return result.ToString();
    }

    private static async Task<string?> FindRoot(string? tool, string[] args, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (tool == null)
        {
            return null;
        }

        try
        {
            string output = await Run(tool, args, true, environment);
            foreach (string line in output.Split('\n').Reverse())
            {
                string path = line.Trim();
                int drive = path.IndexOf(":\\", StringComparison.Ordinal);
                if (drive > 0)
                {
                    path = path[(drive - 1)..];
                }

                if (Path.IsPathFullyQualified(path) && Directory.Exists(path))
                {
                    return Paths.Normalize(path);
                }
            }
        }
        catch (Exception) { }
        return null;
    }

    public async Task<CacheTarget[]> List()
    {
        var result = new List<CacheTarget>();
        void Add(CacheTarget target)
        {
            if (Directory.Exists(target.Root))
            {
                result.Add(target);
            }
        }
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Add(new("temp-user", "用户临时文件", "files", Path.GetTempPath().TrimEnd('\\'), "预览两天以前的临时文件，正在使用的项目会保留。"));
        Add(new("temp-system", "Windows 临时文件", "files", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), "按当前权限处理两天以前的临时文件。"));
        foreach (var browser in new[] { (Name: "Chrome", Path: "Google\\Chrome\\User Data", Process: "chrome"), (Name: "Edge", Path: "Microsoft\\Edge\\User Data", Process: "msedge") })
        {
            string basePath = Path.Combine(local, browser.Path);
            if (!Directory.Exists(basePath))
            {
                continue;
            }

            foreach (string profile in Directory.EnumerateDirectories(basePath).Where(p => Path.GetFileName(p) == "Default" || Path.GetFileName(p).StartsWith("Profile ", StringComparison.Ordinal)))
            {
                foreach (string cache in new[] { "Cache", "Code Cache", "GPUCache" })
                {
                    Add(new(browser.Name + "/" + Path.GetFileName(profile) + "/" + cache, browser.Name + " · " + Path.GetFileName(profile) + " · " + cache,
                        "files", Path.Combine(profile, cache), "清理网页资源缓存前需要关闭浏览器。", Process: browser.Process));
                }
            }
        }
        string? npm = Tool("npm"), pnpm = Tool("pnpm"), pip = Tool("pip"), uv = Tool("uv"), dotnet = Tool("dotnet"), dart = Tool("dart");
        var locations = await Task.WhenAll(
            FindRoot(npm, ["config", "get", "cache"]), FindRoot(pnpm, ["store", "path"]),
            FindRoot(pip, ["cache", "dir"]), FindRoot(uv, ["cache", "dir"]),
            FindRoot(dotnet, ["nuget", "locals", "http-cache", "--list"]),
            FindRoot(dotnet, ["nuget", "locals", "global-packages", "--list"]));
        var npmRoot = locations[0];
        if (npmRoot != null)
        {
            Add(new("npm", "npm 下载缓存", "download", npmRoot, "后续安装可能重新下载包。", npm, ["cache", "clean", "--force"]));
        }
        else
        {
            string downloadRoot = Path.Combine(Environment.GetEnvironmentVariable("NPM_CONFIG_CACHE") ?? Path.Combine(local, "npm-cache"), "_cacache");
            Add(new("npm-files", "npm 下载缓存", "download", downloadRoot, "按文件预览下载缓存，后续需要时重新下载。"));
        }

        var pnpmRoot = locations[1];
        if (pnpmRoot != null)
        {
            Add(new("pnpm-prune", "pnpm 未使用包", "prune", pnpmRoot, "通过 pnpm 修剪未使用的共享包。", pnpm, ["store", "prune"]));
        }

        var pipRoot = locations[2];
        if (pipRoot != null)
        {
            Add(new("pip", "pip 下载与 wheel 缓存", "download", pipRoot, "后续安装可能重新下载或构建包。", pip, ["cache", "purge"]));
        }
        else
        {
            string downloadRoot = Environment.GetEnvironmentVariable("PIP_CACHE_DIR") ?? Path.Combine(local, "pip", "Cache");
            foreach (string child in new[] { "http", "http-v2", "wheels" })
            {
                Add(new("pip-files/" + child, "pip · " + child, "download", Path.Combine(downloadRoot, child), "按文件预览下载缓存，后续需要时重新下载或构建。"));
            }
        }

        var uvRoot = locations[3];
        if (uvRoot != null)
        {
            Add(new("uv-prune", "uv 未使用缓存", "prune", uvRoot, "通过 uv 协调缓存锁并修剪未使用项。", uv, ["cache", "prune"]));
            Add(new("uv-clean", "uv 全部缓存", "shared", uvRoot, "符号链接模式的环境可能需要重建，后续安装需恢复缓存。", uv, ["cache", "clean"]));
        }
        var nugetHttp = locations[4];
        if (nugetHttp != null)
        {
            Add(new("nuget-http", "NuGet HTTP 缓存", "download", nugetHttp, "后续依赖恢复可能重新下载元数据。", dotnet, ["nuget", "locals", "http-cache", "--clear"]));
        }

        var nugetPackages = locations[5];
        if (nugetPackages != null)
        {
            Add(new("nuget-packages", "NuGet 全局依赖包", "shared", nugetPackages, "项目需要重新执行 dotnet restore，离线构建可能受影响。", dotnet, ["nuget", "locals", "global-packages", "--clear"]));
        }

        string pubRoot = Environment.GetEnvironmentVariable("PUB_CACHE") ?? Path.Combine(local, "Pub", "Cache");
        if (dart != null)
        {
            Add(new("pub", "Pub 共享依赖包", "shared", pubRoot, "Flutter/Dart 项目需要重新执行 pub get。", dart, ["pub", "cache", "clean", "--force"]));
        }

        targets.Clear();
        foreach (var target in result)
        {
            targets[target.Id] = target;
        }

        return result.ToArray();
    }

    public object Preview(string id, CancellationToken cancellation = default)
    {
        if (!targets.TryGetValue(id, out var target))
        {
            throw new ArgumentException("缓存位置已变化，请刷新列表。");
        }

        return Preview(target, cancellation);
    }

    internal object Preview(CacheTarget target, CancellationToken cancellation = default)
    {

        if (target.Process != null && Process.GetProcessesByName(target.Process).Any())
        {
            throw new InvalidOperationException($"请先关闭 {target.Name.Split(' ')[0]}，再生成清理清单。");
        }

        if (target.Tool != null)
        {
            if (operations.Protected(target.Root) || Paths.Within(target.Tool, target.Root))
            {
                throw new InvalidOperationException("该缓存位置包含受保护目录或工具程序，请先检查工具配置。");
            }

            var plan = new CachePlan(Guid.NewGuid().ToString("N"), target, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            plans[plan.Id] = plan;
            return new
            {
                kind = "tool",
                plan
            };
        }
        long before = DateTimeOffset.UtcNow.AddDays(target.Id.StartsWith("temp-", StringComparison.Ordinal) ? -2 : 0).ToUnixTimeMilliseconds();
        var warnings = new List<string> { target.Impact };
        var files = WindowsFiles.Enumerate(target.Root, message => { if (warnings.Count < 20) { warnings.Add(message); } }, cancellation)
            .Where(f => !f.Directory && !f.Reparse && f.Modified <= before).Take(10001).Select(f => f.Path).ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException("当前没有可清理的文件。");
        }

        if (files.Length > 10000)
        {
            warnings.Add("本次列出前 10,000 个匹配文件，清理后可再次运行。");
        }

        return new
        {
            kind = "files",
            plan = operations.Plan(new("recycle", files.Take(10000).ToArray()), target.Root, cancellation: cancellation, notes: warnings)
        };
    }

    public CachePlan Read(string id) => plans.TryGetValue(id, out var plan) ? plan : throw new ArgumentException("清理清单已失效。");
    public async Task Execute(string id, Job job)
    {
        if (!plans.TryRemove(id, out var plan) || plan.Created < DateTimeOffset.UtcNow.AddMinutes(-15).ToUnixTimeMilliseconds())
        {
            throw new InvalidOperationException("清理清单已失效，请重新预览。");
        }

        var target = plan.Target;
        job.Cancellation.Token.ThrowIfCancellationRequested();
        var refreshed = (await List()).FirstOrDefault(item => item.Id == target.Id);
        if (refreshed == null || WindowsFiles.Canonical(refreshed.Root) != WindowsFiles.Canonical(target.Root) || refreshed.Tool != target.Tool)
        {
            throw new InvalidOperationException("缓存配置在预览后已变化，请重新生成清单。");
        }

        job.Phase = target.Name + " · 等待工具完成当前操作";
        job.Cancellation.Token.ThrowIfCancellationRequested();
        var environment = new Dictionary<string, string>();
        switch (target.Id)
        {
            case "npm":
                environment["npm_config_cache"] = target.Root;
                break;
            case "pip":
                environment["PIP_CACHE_DIR"] = target.Root;
                break;
            case "uv-prune":
            case "uv-clean":
                environment["UV_CACHE_DIR"] = target.Root;
                break;
            case "nuget-http":
                environment["NUGET_HTTP_CACHE_PATH"] = target.Root;
                break;
            case "nuget-packages":
                environment["NUGET_PACKAGES"] = target.Root;
                break;
            case "pub":
                environment["PUB_CACHE"] = target.Root;
                break;
            case "pnpm-prune":
                environment["npm_config_store_dir"] = System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(target.Root), "^v[0-9]+$")
                    ? Path.GetDirectoryName(target.Root)! : target.Root;
                string? actualStore = await FindRoot(target.Tool, ["store", "path"], environment);
                if (actualStore == null || WindowsFiles.Canonical(actualStore) != WindowsFiles.Canonical(target.Root))
                {
                    throw new InvalidOperationException("无法固定 pnpm 存储位置，请检查工具配置。");
                }

                break;
        }
        job.Cancellation.Token.ThrowIfCancellationRequested();
        string output = await Run(target.Tool!, target.Arguments!, environment: environment);
        var record = new
        {
            id,
            action = "cache",
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            target.Name,
            target.Root,
            output
        };
        await File.AppendAllTextAsync(Path.Combine(workspace.DirectoryPath, "operations.jsonl"), Json.Text(record) + "\n");
        job.Result = record;
    }

    public Rule SaveRule(Rule rule)
    {
        string[] roots = Paths.Reduce(rule.Roots);
        if (roots.Length is < 1 or > 32 || rule.Name.Length is < 1 or > 200 || rule.Pattern.Length > 512 ||
            rule.MinSize < 0 || rule.MaxSize < rule.MinSize || rule.OlderDays < 0)
        {
            throw new ArgumentException("请检查规则名称、目录与筛选范围。");
        }

        var saved = rule with
        {
            Id = Guid.TryParseExact(rule.Id, "N", out _) ? rule.Id : Guid.NewGuid().ToString("N"),
            Roots = roots
        };
        workspace.Update(state => state with { Rules = [.. state.Rules.Where(r => r.Id != saved.Id), saved] });
        return saved;
    }

    public OperationPlan PreviewRule(string id, CancellationToken cancellation = default)
    {
        var rule = workspace.State.Rules.FirstOrDefault(r => r.Id == id) ?? throw new ArgumentException("规则不存在。");
        long before = DateTimeOffset.UtcNow.AddDays(-(rule.OlderDays ?? 0)).ToUnixTimeMilliseconds();
        var files = new List<string>();
        var warnings = new List<string>();
        foreach (string root in rule.Roots)
        {
            foreach (var file in WindowsFiles.Enumerate(root, message => { if (warnings.Count < 20) { warnings.Add(message); } }, cancellation))
            {
                if (file.Directory || file.Reparse || file.Modified > before || file.Size < rule.MinSize || rule.MaxSize.HasValue && file.Size > rule.MaxSize ||
                    rule.Extension.Length > 0 && !file.Extension.Equals(rule.Extension, StringComparison.OrdinalIgnoreCase) ||
                    !FileSystemName.MatchesSimpleExpression(rule.Pattern, file.Name, true) ||
                    rule.Exclusions?.Any(p => Paths.Within(file.Path, p)) == true)
                {
                    continue;
                }

                files.Add(file.Path);
                if (files.Count > 10000)
                {
                    warnings.Add("本次列出前 10,000 个匹配文件，清理后可再次运行。");
                    return operations.Plan(new("recycle", files.Take(10000).ToArray()), useIndex: false, cancellation: cancellation, notes: warnings);
                }
            }
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException("没有符合规则的文件。");
        }

        return operations.Plan(new("recycle", files.ToArray()), useIndex: false, cancellation: cancellation, notes: warnings);
    }
}
