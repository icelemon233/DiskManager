namespace DiskManager;

internal sealed record TreeRequest(string[] Expanded, string Sort = "allocated", bool Descending = true, int Offset = 0, int Limit = 200);
internal sealed record TreeRow(IndexedEntry Entry, int Depth, long ParentSize, long ParentAllocated);
internal sealed record MapNode(IndexedEntry Entry, List<MapNode> Children);

internal static class AnalysisViews
{
    private sealed record TreeFrame(IEnumerator<IndexedEntry> Entries, int Depth, long Size, long Allocated);
    public static object Tree(IAnalysisIndex index, TreeRequest request)
    {
        if (request.Expanded.Length > 5000)
        {
            throw new ArgumentException("展开目录过多，请先收起部分目录。");
        }

        var expanded = request.Expanded.ToHashSet(StringComparer.Ordinal);
        var result = new List<TreeRow>();
        var stack = new Stack<TreeFrame>();
        var metadata = index.Metadata();
        stack.Push(new(index.Children("", request.Sort, request.Descending).GetEnumerator(), 0, metadata.Size, metadata.Allocated));
        long count = 0;
        try
        {
            while (stack.TryPeek(out var frame))
            {
                if (!frame.Entries.MoveNext())
                {
                    stack.Pop().Entries.Dispose();
                    continue;
                }
                var entry = frame.Entries.Current;
                if (count >= Math.Max(0, request.Offset) && result.Count < Math.Clamp(request.Limit, 1, 500))
                {
                    result.Add(new(entry, frame.Depth, frame.Size, frame.Allocated));
                }

                count++;
                if (entry.File.Directory && expanded.Contains(entry.File.Path))
                {
                    stack.Push(new(index.Children(entry.File.Path, request.Sort, request.Descending).GetEnumerator(), frame.Depth + 1, entry.TotalSize, entry.TotalAllocated));
                }
            }
        }
        finally
        {
            foreach (var frame in stack)
            {
                frame.Entries.Dispose();
            }
        }
        return new
        {
            count,
            rows = result
        };
    }

    public static object Selection(IAnalysisIndex index, string[] paths)
    {
        if (paths.Length > 10000)
        {
            throw new ArgumentException("选择数量超限。");
        }

        long size = 0, allocated = 0, files = 0;
        string? parent = null;
        int count = 0;
        foreach (string path in paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (parent != null && path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.Ordinal))
            {
                continue;
            }

            var row = index.Find(path);
            if (row == null)
            {
                continue;
            }

            parent = row.File.Directory ? path : null;
            count++;
            checked
            {
                size += row.TotalSize;
                allocated += row.TotalAllocated;
                files += row.FileCount;
            }
        }
        return new
        {
            count,
            size,
            allocated,
            files
        };
    }

    public static MapNode[] Map(IAnalysisIndex index, string? scope, string metric, int budget = 4000)
    {
        long Weight(IndexedEntry row) => metric == "size" ? row.TotalSize : row.TotalAllocated;
        List<MapNode> Build(string parent, int available, int depth)
        {
            var source = index.Children(parent, metric, true).Where(row => Weight(row) > 0).ToArray();
            long total = source.Sum(Weight);
            var result = new List<MapNode>();
            int take = Math.Min(source.Length, Math.Max(1, available / 2));
            for (int position = 0; position < take; position++)
            {
                var row = source[position];
                int share = total > 0 ? (int)Math.Floor((available - take) * (double)Weight(row) / total) : 0;
                var descendants = row.File.Directory && share >= 2 && depth < 10 ? Build(row.File.Path, share, depth + 1) : [];
                result.Add(new(row, descendants));
            }
            if (take < source.Length)
            {
                long size = 0, allocated = 0, files = 0;
                foreach (var row in source.Skip(take))
                {
                    size += row.TotalSize;
                    allocated += row.TotalAllocated;
                    files += row.FileCount;
                }
                var rest = new FileEntry("", parent, $"其余 {source.Length - take:N0} 项", false, size, allocated, 0, 0, "", 0, 1);
                result.Add(new(new(rest, size, allocated, files), []));
            }
            return result;
        }
        return Build(scope ?? "", Math.Clamp(budget, 100, 6000), 0).ToArray();
    }
}
