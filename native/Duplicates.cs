using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskManager;

internal sealed record DuplicateGroup(string Id, string Hash, long Size, long Recoverable, FileEntry[] Files, string SuggestedKeep);
internal sealed record DuplicateRequest(string[]? Roots = null, bool DownloadOnline = false, string[]? Preferred = null);

internal sealed class Duplicates(Workspace workspace)
{
    private readonly object sync = new();
    private List<DuplicateGroup> groups = [];
    private string session = "";
    private List<string> skipped = [];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        public long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string fileName, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextStreamW(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr handle);

    internal static IEnumerable<(string Name, long Size)> Streams(string path)
    {
        var handle = FindFirstStreamW(WindowsFiles.ExtendedPath(path), 0, out var data, 0);
        if (handle == new IntPtr(-1))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 38 or 1 or 87)
            {
                yield return ("::$DATA", new FileInfo(path).Length);
                yield break;
            }
            throw new System.ComponentModel.Win32Exception(error);
        }
        try
        {
            do
            {
                yield return (data.Name, data.Size);
            }
            while (FindNextStreamW(handle, out data));
            if (Marshal.GetLastWin32Error() != 38)
            {
                throw new IOException("无法完整读取文件的数据流。");
            }
        }
        finally { FindClose(handle); }
    }

    public static string Hash(FileEntry expected, CancellationToken cancellation)
    {
        var before = WindowsFiles.Inspect(expected.Path);
        if (!SameFile(before, expected))
        {
            throw new IOException("文件在扫描后已变化。");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var stream in Streams(expected.Path).OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(stream.Name + "\0" + stream.Size + "\0"));
            string path = stream.Name == "::$DATA" ? expected.Path : expected.Path + stream.Name;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
        }
        if (!SameFile(WindowsFiles.Inspect(expected.Path), before))
        {
            throw new IOException("校验期间文件发生变化。");
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static bool SameFile(FileEntry a, FileEntry b) =>
        a.Identity == b.Identity && a.Size == b.Size && a.Modified == b.Modified && a.Created == b.Created && a.Directory == b.Directory;

    public object Estimate(DuplicateRequest request)
    {
        long files = 0, download = 0;
        foreach (var row in workspace.Current().All(new(FilesOnly: true)))
        {
            if (request.Roots is { Length: > 0 } && !request.Roots.Any(r => Paths.Within(row.File.Path, r)))
            {
                continue;
            }

            if (row.File.OnlineOnly)
            {
                files++;
                download += row.File.Size;
            }
        }
        return new
        {
            files,
            download
        };
    }

    public void Run(DuplicateRequest request, Job job)
    {
        var store = workspace.Current();
        if (store.Metadata().Imported)
        {
            throw new InvalidOperationException("请对本机位置重新扫描后校验内容。");
        }

        var candidates = store.All(new(FilesOnly: true)).Select(r => r.File)
            .Where(f => request.Roots is not { Length: > 0 } || request.Roots.Any(r => Paths.Within(f.Path, r)))
            .GroupBy(f => f.Identity).Select(g => g.First()).GroupBy(f => f.Size).Where(g => g.Count() > 1).ToArray();
        job.Total = candidates.Sum(g => (long)g.Count());
        var found = new List<DuplicateGroup>();
        var unavailable = new List<string>();
        string[] preferred = request.Preferred ?? workspace.State.Preferences.PreferredDirectories ?? [];
        foreach (var bySize in candidates)
        {
            var hashes = new Dictionary<string, List<FileEntry>>(StringComparer.Ordinal);
            foreach (var file in bySize)
            {
                job.Cancellation.Token.ThrowIfCancellationRequested();
                job.Phase = file.Path;
                try
                {
                    if (file.OnlineOnly && !request.DownloadOnline)
                    {
                        throw new IOException("在线文件已跳过");
                    }

                    if (file.Reparse && WindowsFiles.HasLinkAncestor(file.Path))
                    {
                        throw new IOException("链接文件需在实际位置校验");
                    }

                    if (file.Issue != null)
                    {
                        throw new IOException(file.Issue);
                    }

                    string hash = Hash(file, job.Cancellation.Token);
                    if (!hashes.TryGetValue(hash, out var files))
                    {
                        hashes[hash] = files = [];
                    }

                    files.Add(file);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    job.Issues++;
                    if (unavailable.Count < 1000)
                    {
                        unavailable.Add($"{file.Path} — {ex.Message}");
                    }
                }
                job.Completed++;
            }
            foreach (var match in hashes.Where(h => h.Value.Count > 1))
            {
                int Preference(FileEntry file)
                {
                    int index = Array.FindIndex(preferred, path => Paths.Within(file.Path, path));
                    return index < 0 ? int.MaxValue : index;
                }
                var ordered = match.Value.OrderBy(Preference).ThenBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
                found.Add(new(Guid.NewGuid().ToString("N"), match.Key, bySize.Key,
                    ordered.Skip(1).Where(f => f.Links <= 1).Sum(f => f.Allocated), ordered, ordered[0].Path));
            }
        }
        lock (sync)
        {
            groups = found.OrderByDescending(g => g.Recoverable).ToList();
            session = job.Id;
            skipped = unavailable;
        }
        job.Result = new
        {
            session,
            count = found.Count,
            recoverable = found.Sum(g => g.Recoverable),
            skipped = job.Issues
        };
    }

    public object List(int offset)
    {
        lock (sync)
        {
            return new
            {
                session,
                count = groups.Count,
                rows = groups.Skip(Math.Max(0, offset)).Take(50).ToArray(),
                skipped = skipped.Take(50).ToArray()
            };
        }
    }

    public List<(FileEntry Keeper, string Hash)> KeepersFor(string[] selected)
    {
        lock (sync)
        {
            var keepers = new List<(FileEntry, string)>();
            foreach (var group in groups.Where(g => g.Files.Any(f => selected.Contains(f.Path, StringComparer.Ordinal))))
            {
                var keep = group.Files.FirstOrDefault(f => !selected.Contains(f.Path, StringComparer.Ordinal));
                if (keep == null)
                {
                    throw new InvalidOperationException("每个重复组至少需要保留一份。");
                }

                keepers.Add((keep, group.Hash));
            }
            if (selected.Any(path => !groups.Any(g => g.Files.Any(f => f.Path.Equals(path, StringComparison.Ordinal)))))
            {
                throw new InvalidOperationException("重复文件结果已变化，请重新选择。");
            }

            return keepers;
        }
    }
}
