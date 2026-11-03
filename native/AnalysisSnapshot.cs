using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace DiskManager;

internal interface IAnalysisIndex
{
    string? FilePath
    {
        get;
    }
    SnapshotInfo Metadata();
    object Query(QueryRequest query, CancellationToken cancellation = default);
    object Summary(string? scope = null);
    object Types(string? scope, int offset);
    IndexedEntry? Find(string path);
    IEnumerable<IndexedEntry> All(QueryRequest? query = null);
    IEnumerable<IndexedEntry> Children(string parent, string sort, bool descending) =>
        All(new(Parent: parent)).OrderBy(row => row, Comparer<IndexedEntry>.Create((a, b) => AnalysisSnapshot.Compare(a, b, sort, descending)));
}

internal sealed class AnalysisSnapshot : IAnalysisIndex
{
    private readonly IndexedEntry[] rows;
    private readonly Dictionary<string, int> paths;
    private readonly Dictionary<string, int[]> children;
    private readonly ConcurrentDictionary<string, Lazy<int[]>> orders = new();
    private readonly ConcurrentDictionary<string, SummaryData> summaries = new();
    private readonly SnapshotInfo metadata;
    public string[] Issues
    {
        get;
    }
    public string? FilePath => null;
    public SnapshotInfo Metadata() => metadata;
    private sealed record TypeData(string Extension, long Count, long Size, long Allocated);
    private sealed record SummaryData(long Count, long Size, long Allocated, TypeData[] Types, string[] Issues);

    private AnalysisSnapshot(IndexedEntry[] rows, Dictionary<string, int> paths, Dictionary<string, int[]> children,
        SnapshotInfo metadata, string[] issues)
    {
        this.rows = rows;
        this.paths = paths;
        this.children = children;
        this.metadata = metadata;
        Issues = issues;
    }

    public static AnalysisSnapshot Build(IEnumerable<FileEntry> input, SnapshotInfo info, IEnumerable<string> issues,
        CancellationToken cancellation)
    {
        var files = input.ToArray();
        var paths = new Dictionary<string, int>(files.Length, StringComparer.Ordinal);
        var parents = new int[files.Length];
        var sizes = new long[files.Length];
        var allocated = new long[files.Length];
        var counts = new long[files.Length];
        var folders = new long[files.Length];
        var directories = new List<int>();
        var links = new Dictionary<string, int>(StringComparer.Ordinal);
        var childLists = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        long totalSize = 0, totalAllocated = 0, totalFiles = 0;
        for (int index = 0; index < files.Length; index++)
        {
            if ((index & 4095) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            var file = files[index];
            if (!paths.TryAdd(file.Path, index))
            {
                throw new InvalidDataException("扫描包含重复路径。");
            }

            if (!childLists.TryGetValue(file.Parent, out var siblings))
            {
                childLists[file.Parent] = siblings = [];
            }

            siblings.Add(index);
            if (file.Directory)
            {
                directories.Add(index);
                continue;
            }
            sizes[index] = file.Size;
            counts[index] = 1;
            allocated[index] = file.Allocated;
            if (file.Links > 1)
            {
                if (links.TryGetValue(file.Identity, out int owner))
                {
                    if (StringComparer.Ordinal.Compare(file.Path, files[owner].Path) < 0)
                    {
                        allocated[owner] = 0;
                        links[file.Identity] = index;
                    }
                    else
                    {
                        allocated[index] = 0;
                    }
                }
                else
                {
                    links.Add(file.Identity, index);
                }
            }
        }
        for (int index = 0; index < files.Length; index++)
        {
            parents[index] = paths.TryGetValue(files[index].Parent, out int parent) && parent != index ? parent : -1;
            if (files[index].Directory)
            {
                continue;
            }

            checked
            {
                totalFiles++;
                totalSize += sizes[index];
                totalAllocated += allocated[index];
            }
            if (parents[index] >= 0)
            {
                checked
                {
                    sizes[parents[index]] += sizes[index];
                    allocated[parents[index]] += allocated[index];
                    counts[parents[index]]++;
                }
            }
        }
        directories.Sort((left, right) => files[right].Path.Length.CompareTo(files[left].Path.Length));
        foreach (int index in directories)
        {
            cancellation.ThrowIfCancellationRequested();
            int parent = parents[index];
            if (parent < 0)
            {
                continue;
            }

            checked
            {
                sizes[parent] += sizes[index];
                allocated[parent] += allocated[index];
                counts[parent] += counts[index];
                folders[parent] += folders[index] + 1;
            }
        }
        var rows = new IndexedEntry[files.Length];
        for (int index = 0; index < files.Length; index++)
        {
            rows[index] = new(files[index], sizes[index], allocated[index], counts[index], folders[index]);
        }

        var children = childLists.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        return new(rows, paths, children, info with
        {
            Count = totalFiles,
            Size = totalSize,
            Allocated = totalAllocated,
            FolderCount = directories.Count(index => parents[index] >= 0)
        }, issues.Take(10000).ToArray());
    }

    public AnalysisSnapshot WithMetadata(SnapshotInfo value) => new(rows, paths, children, value, Issues);
    public IndexedEntry? Find(string path) => paths.TryGetValue(path, out int index) ? rows[index] : null;

    internal static int Compare(IndexedEntry left, IndexedEntry right, string sort, bool descending)
    {
        int primary = sort switch
        {
            "name" => StringComparer.OrdinalIgnoreCase.Compare(left.File.Name, right.File.Name),
            "path" => StringComparer.Ordinal.Compare(left.File.Path, right.File.Path),
            "size" => left.TotalSize.CompareTo(right.TotalSize),
            "modified" => left.File.Modified.CompareTo(right.File.Modified),
            "count" => left.FileCount.CompareTo(right.FileCount),
            "folders" => (left.FolderCount ?? 0).CompareTo(right.FolderCount ?? 0),
            _ => left.TotalAllocated.CompareTo(right.TotalAllocated)
        };
        if (primary != 0)
        {
            return descending ? -primary : primary;
        }

        return StringComparer.Ordinal.Compare(left.File.Path, right.File.Path);
    }

    private int[] Ordered(QueryRequest query)
    {
        string key = query.Sort + "/" + query.Descending + "\0" + (query.Parent == null ? "global" : "parent:" + query.Parent);
        return orders.GetOrAdd(key, _ => new Lazy<int[]>(() =>
        {
            var sorted = query.Parent == null ? Enumerable.Range(0, rows.Length).ToArray()
                : children.TryGetValue(query.Parent, out var local) ? (int[])local.Clone() : [];
            Array.Sort(sorted, (a, b) => Compare(rows[a], rows[b], query.Sort, query.Descending));
            return sorted;
        })).Value;
    }

    public IEnumerable<IndexedEntry> Children(string parent, string sort, bool descending) =>
        Ordered(new(Parent: parent, Sort: sort, Descending: descending)).Select(index => rows[index]);

    private static string GlobPattern(string pattern)
    {
        var result = new StringBuilder("\\A");
        for (int index = 0; index < pattern.Length; index++)
        {
            char value = pattern[index];
            if (value == '*')
            {
                result.Append(".*");
            }
            else if (value == '?')
            {
                result.Append('.');
            }
            else if (value == '[' && pattern.IndexOf(']', index + 1) is var end && end > index + 1)
            {
                result.Append(pattern.AsSpan(index, end - index + 1));
                index = end;
            }
            else
            {
                result.Append(Regex.Escape(value.ToString()));
            }
        }
        return result.Append("\\z").ToString();
    }

    private static Func<IndexedEntry, bool> Matcher(QueryRequest query)
    {
        if (query.Search.Length > 512)
        {
            throw new ArgumentException("搜索内容过长。");
        }

        Regex? expression = query.Search.Length > 0 && query.Mode is "regex" or "wildcard"
            ? new Regex(query.Mode == "regex" ? query.Search : GlobPattern(query.Search),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)) : null;
        string? extension = query.Extension.Length == 0 ? null : query.Extension == "<none>" ? "" : query.Extension.ToLowerInvariant();
        string? prefix = query.Scope?.TrimEnd('\\') + "\\";
        return row =>
        {
            var file = row.File;
            return (!query.FilesOnly || !file.Directory) && (!query.DirectoriesOnly || file.Directory)
                && (query.Scope == null || file.Path == query.Scope || file.Path.StartsWith(prefix!, StringComparison.Ordinal))
                && (extension == null || file.Extension == extension)
                && row.TotalSize >= query.MinSize && (!query.MaxSize.HasValue || row.TotalSize <= query.MaxSize)
                && (!query.Before.HasValue || file.Modified <= query.Before) && (!query.After.HasValue || file.Modified >= query.After)
                && (query.Search.Length == 0 || (expression != null ? expression.IsMatch(file.Path) : file.Path.Contains(query.Search, StringComparison.OrdinalIgnoreCase)));
        };
    }

    public object Query(QueryRequest query, CancellationToken cancellation = default)
    {
        var match = Matcher(query);
        var result = new List<IndexedEntry>();
        long count = 0;
        int inspected = 0;
        foreach (int index in Ordered(query))
        {
            if ((inspected++ & 4095) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            var row = rows[index];
            if (!match(row))
            {
                continue;
            }

            if (count >= Math.Max(0, query.Offset) && result.Count < Math.Clamp(query.Limit, 1, 1000))
            {
                result.Add(row);
            }

            count++;
        }
        cancellation.ThrowIfCancellationRequested();
        return new
        {
            count,
            rows = result
        };
    }

    public IEnumerable<IndexedEntry> All(QueryRequest? query = null)
    {
        query ??= new();
        var match = Matcher(query);
        var candidates = query.Parent == null ? Enumerable.Range(0, rows.Length) : children.GetValueOrDefault(query.Parent, []);
        foreach (int index in candidates)
        {
            if (match(rows[index]))
            {
                yield return rows[index];
            }
        }
    }

    private SummaryData Summarize(string? scope)
    {
        return summaries.GetOrAdd(scope ?? "", _ =>
        {
            var grouped = new Dictionary<string, (long Count, long Size, long Allocated)>(StringComparer.Ordinal);
            long count = 0, size = 0, allocated = 0;
            foreach (var row in All(new(FilesOnly: true, Scope: scope)))
            {
                string extension = row.File.Extension;
                var type = grouped.GetValueOrDefault(extension);
                grouped[extension] = (type.Count + 1, checked(type.Size + row.File.Size), checked(type.Allocated + row.TotalAllocated));
                count++;
                size = checked(size + row.File.Size);
                allocated = checked(allocated + row.TotalAllocated);
            }
            var types = grouped.Select(pair => new TypeData(pair.Key, pair.Value.Count, pair.Value.Size, pair.Value.Allocated))
                .OrderByDescending(type => type.Allocated).ThenBy(type => type.Extension, StringComparer.Ordinal).ToArray();
            return new(count, size, allocated, types, Issues.Take(100).ToArray());
        });
    }

    public object Summary(string? scope = null)
    {
        var summary = Summarize(scope);
        return summary with
        {
            Types = summary.Types.Take(100).ToArray()
        };
    }

    public object Types(string? scope, int offset)
    {
        var types = Summarize(scope).Types;
        return new
        {
            count = types.Length,
            rows = types.Skip(Math.Max(0, offset)).Take(200).ToArray()
        };
    }
}
