using System.Text.Json;

namespace DiskManager;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Text(object? value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(JsonElement value) => value.Deserialize<T>(Options) ?? throw new ArgumentException("参数为空。");
}

internal sealed record FileEntry(
    string Path, string Parent, string Name, bool Directory, long Size, long Allocated,
    long Modified, long Created, string Identity, uint Attributes, uint Links,
    string? Issue = null)
{
    public string Extension => Directory ? "" : System.IO.Path.GetExtension(Name).ToLowerInvariant();
    public bool OnlineOnly => (Attributes & (0x1000u | 0x40000u | 0x400000u)) != 0;
    public bool Reparse => (Attributes & 0x400u) != 0;
}

internal sealed record ScanRequest(string[] Roots, bool Fast = true);
internal sealed record QueryRequest(
    string? Snapshot = null, string? Parent = null, string Search = "", string Extension = "",
    long MinSize = 0, long? MaxSize = null, long? Before = null, long? After = null,
    string Sort = "allocated", bool Descending = true, int Offset = 0, int Limit = 200,
    bool FilesOnly = false, bool DirectoriesOnly = false, string Mode = "contains", string? Channel = null, string? Scope = null);
internal sealed record IndexedEntry(FileEntry File, long TotalSize, long TotalAllocated, long FileCount, long? FolderCount = null);
internal sealed record MftReadStatistics(string Root, long TableBytes, long ReadBytes, long RecordSlots, long ActiveRecords,
    long IoMs, long ParseMs, long MetadataMs, bool BitmapUsed);
internal sealed record ScanTiming(long PermissionMs, long ReadMs, long AggregateMs, long ReadyMs, long? SaveMs = null, MftReadStatistics[]? Mft = null);
internal sealed record SnapshotInfo(string Id, string Label, string[] Roots, long Created, long Count,
    long Size, long Allocated, string Status, string Method, long DurationMs, bool Pinned = false, bool Imported = false,
    ScanTiming? Timing = null, long FolderCount = 0);
internal sealed record Preferences(int RetainCount = 10, long HistoryBudget = 2147483648,
    string[]? PreferredDirectories = null);
internal sealed record Rule(string Id, string Name, string[] Roots, string Pattern = "*", string Extension = "",
    long MinSize = 0, long? MaxSize = null, int? OlderDays = null, string[]? Exclusions = null);

internal sealed class Job(string kind)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Kind { get; } = kind;
    public string State { get; set; } = "running";
    public string Phase { get; set; } = "准备";
    public long Completed
    {
        get; set;
    }
    public long? Total
    {
        get; set;
    }
    public int Issues
    {
        get; set;
    }
    public string? Error
    {
        get; set;
    }
    public object? Result
    {
        get; set;
    }
    public long Started { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long PermissionMs
    {
        get; set;
    }
    public List<MftReadStatistics> MftStatistics { get; } = [];
    public CancellationTokenSource Cancellation { get; } = new();
    public object Public(bool includeResult = false) => new { Id, Kind, State, Phase, Completed, Total, Issues, Error, Result = includeResult ? Result : null, Started };
}

internal static class Paths
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("路径为空或包含无效字符。");
        }

        if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("请输入完整的本地路径。");
        }

        var full = Path.GetFullPath(value);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0)
        {
            throw new ArgumentException("请选择本地磁盘或文件夹。");
        }

        return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }

    public static bool Within(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static string[] Reduce(IEnumerable<string> paths)
    {
        var sorted = paths.Select(Normalize).Select(p => File.Exists(p) || Directory.Exists(p) ? WindowsFiles.Canonical(p) : p)
            .Distinct(StringComparer.Ordinal).OrderBy(p => p.Length).ToArray();
        return sorted.Where((p, i) => !sorted.Take(i).Any(parent => p.Equals(parent, StringComparison.Ordinal) ||
            p.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.Ordinal))).ToArray();
    }

    public static void AtomicJson(string path, object value)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, Json.Text(value));
        File.Move(temp, path, true);
    }
}
