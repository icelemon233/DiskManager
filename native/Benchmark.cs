using System.Diagnostics;
using System.Text.Json;

namespace DiskManager;

internal static class Benchmark
{
    public static void Run(string output)
    {
        output = Path.GetFullPath(output);
        if (!output.Contains("\\.runtime\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Benchmark output must use an isolated .runtime directory.");
        }

        Directory.CreateDirectory(output);
        string id = Guid.NewGuid().ToString("N");
        var store = new IndexStore(Path.Combine(output, id + ".db"));
        store.Create();
        const string root = "D:\\DiskManager-Synthetic-Benchmark";
        const int count = 1000000;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var timer = Stopwatch.StartNew();
        using (var writer = new IndexStore.Writer(store))
        {
            writer.Add(new(root, "", "Synthetic benchmark", true, 0, 0, now, now, "root", 16, 1));
            for (int group = 0; group < 10000; group++)
            {
                string parent = root + "\\folder-" + group.ToString("D5");
                writer.Add(new(parent, root, Path.GetFileName(parent), true, 0, 0, now, now, "directory-" + group, 16, 1));
                for (int item = 0; item < 100; item++)
                {
                    int number = group * 100 + item;
                    string name = "file-" + number.ToString("D7") + (item % 5 == 0 ? ".zip" : ".bin");
                    long size = 1000 + number % 10000 * 103;
                    writer.Add(new(parent + "\\" + name, parent, name, false, size, (size + 4095) / 4096 * 4096,
                        now, now, "identity-" + number, 32, 1));
                }
            }
        }
        long insertMs = timer.ElapsedMilliseconds;
        store.FinalizeIndex(new(id, "Synthetic benchmark", [root], now, count, 0, 0, "complete", "synthetic", insertMs), [], CancellationToken.None);
        store.Seal();
        long buildMs = timer.ElapsedMilliseconds;
        var queries = new List<object>();
        foreach (var item in new[]
        {
            ("largest", new QueryRequest(FilesOnly: true)),
            ("search", new QueryRequest(Search: "file-00001", FilesOnly: true)),
            ("extension", new QueryRequest(Extension: ".zip", FilesOnly: true)),
            ("folder", new QueryRequest(Parent: root + "\\folder-00050"))
        })
        {
            timer.Restart();
            using var result = JsonDocument.Parse(Json.Text(store.Query(item.Item2)));
            queries.Add(new
            {
                name = item.Item1,
                elapsedMs = timer.ElapsedMilliseconds,
                count = result.RootElement.GetProperty("count").GetInt64()
            });
        }
        var report = new
        {
            synthetic = true,
            files = count,
            insertMs,
            buildMs,
            databaseBytes = new FileInfo(store.FilePath).Length,
            peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64,
            queries
        };
        File.WriteAllText(Path.Combine(output, "result.json"), Json.Text(report));
        Console.WriteLine(Json.Text(report));
    }

    public static void Reindex(string source, string output)
    {
        if (!Path.GetFullPath(output).Contains("\\.runtime\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Reindex benchmark requires an isolated output directory.");
        }

        Directory.CreateDirectory(output);
        var original = new IndexStore(source);
        var store = new IndexStore(Path.Combine(output, Guid.NewGuid().ToString("N") + ".db"));
        store.Create();
        var timer = Stopwatch.StartNew();
        using (var writer = new IndexStore.Writer(store))
        {
            foreach (var row in original.All())
            {
                writer.Add(row.File);
            }
        }

        long insertMs = timer.ElapsedMilliseconds;
        store.FinalizeIndex(original.Metadata(), [], CancellationToken.None);
        store.Seal();
        var report = new
        {
            source = "existing live-volume index",
            insertMs,
            totalMs = timer.ElapsedMilliseconds,
            databaseBytes = new FileInfo(store.FilePath).Length,
            totals = store.Summary()
        };
        File.WriteAllText(Path.Combine(output, "result.json"), Json.Text(report));
        Console.WriteLine(Json.Text(new
        {
            report.source,
            report.insertMs,
            report.totalMs,
            report.databaseBytes
        }));
    }

    public static void Memory(string source, string output)
    {
        var store = new IndexStore(source);
        var timer = Stopwatch.StartNew();
        var entries = store.All().Select(row => row.File).ToArray();
        long readMs = timer.ElapsedMilliseconds;
        timer.Restart();
        var snapshot = AnalysisSnapshot.Build(entries, store.Metadata(), [], CancellationToken.None);
        long buildMs = timer.ElapsedMilliseconds;
        var queries = new List<object>();
        foreach (var request in new[] { new QueryRequest(FilesOnly: true), new QueryRequest(FilesOnly: true, Search: "package.json"),
            new QueryRequest(FilesOnly: true, Extension: ".zip"), new QueryRequest(Parent: snapshot.Metadata().Roots[0]) })
        {
            timer.Restart();
            using var result = System.Text.Json.JsonDocument.Parse(Json.Text(snapshot.Query(request)));
            queries.Add(new
            {
                request.Search,
                request.Extension,
                request.Parent,
                elapsedMs = timer.ElapsedMilliseconds,
                count = result.RootElement.GetProperty("count").GetInt64()
            });
        }
        var report = new
        {
            source = "cached metadata, no physical volume read",
            readMs,
            buildMs,
            count = snapshot.Metadata().Count,
            allocated = snapshot.Metadata().Allocated,
            peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64,
            queries
        };
        File.WriteAllText(output, Json.Text(report));
        Console.WriteLine(Json.Text(report));
    }

    public static void Validate(string source, string output)
    {
        var store = new IndexStore(source);
        long stride = Math.Max(1, store.Metadata().Count / 1000);
        int checkedFiles = 0, changed = 0, unavailable = 0;
        var mismatches = new List<object>();
        long index = 0;
        foreach (var row in store.All(new(FilesOnly: true)))
        {
            if (index++ % stride != 0)
            {
                continue;
            }

            try
            {
                var current = WindowsFiles.Inspect(row.File.Path);
                if (current.Modified != row.File.Modified || current.Size != row.File.Size || current.Links != row.File.Links || (current.Attributes & ~0x80u) != (row.File.Attributes & ~0x80u))
                {
                    changed++;
                    continue;
                }
                if (current.Issue != null)
                {
                    unavailable++;
                    continue;
                }
                checkedFiles++;
                if (current.Identity != row.File.Identity || current.Allocated != row.File.Allocated)
                {
                    mismatches.Add(new
                    {
                        row.File.Path,
                        expectedIdentity = row.File.Identity,
                        actualIdentity = current.Identity,
                        expectedAllocated = row.File.Allocated,
                        actualAllocated = current.Allocated
                    });
                }
            }
            catch (Exception) { unavailable++; }
        }
        File.WriteAllText(output, Json.Text(new
        {
            checkedFiles,
            changed,
            unavailable,
            mismatches
        }));
        Console.WriteLine(Json.Text(new
        {
            checkedFiles,
            changed,
            unavailable,
            mismatches = mismatches.Count
        }));
        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException("Index validation found metadata differences.");
        }
    }
}
