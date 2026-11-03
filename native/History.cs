using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DiskManager;

internal sealed partial class Workspace
{
    public object Difference(string olderId, string newerId, string search, int offset)
    {
        var older = Stored(olderId);
        var newer = Stored(newerId);
        var oldInfo = older.Metadata();
        var newInfo = newer.Metadata();
        if (oldInfo.Status != "complete" || newInfo.Status != "complete")
        {
            throw new InvalidOperationException("差异对比需要两份完整扫描记录。");
        }

        if (!oldInfo.Roots.Order(StringComparer.Ordinal).SequenceEqual(newInfo.Roots.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("请选择扫描范围相同的两份记录。");
        }

        using var connection = newer.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "ATTACH DATABASE $file AS previous";
        command.Parameters.AddWithValue("$file", older.FilePath);
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        const string changes = """
            WITH old_unique AS (SELECT identity,MIN(path) path FROM previous.entries GROUP BY identity HAVING COUNT(*)=1),
            new_unique AS (SELECT identity,MIN(path) path FROM entries GROUP BY identity HAVING COUNT(*)=1),
            moves AS (
              SELECT n.path path,o.path old_path,n.total_allocated-o.total_allocated delta,n.size size,'moved' kind
              FROM entries n JOIN previous.entries o ON n.identity=o.identity AND n.created=o.created
              JOIN old_unique ou ON ou.path=o.path JOIN new_unique nu ON nu.path=n.path
              WHERE n.path<>o.path),
            changes AS (
              SELECT n.path path,o.path old_path,n.total_allocated-o.total_allocated delta,n.size size,'changed' kind
              FROM entries n JOIN previous.entries o ON n.path=o.path
              WHERE n.size<>o.size OR n.total_allocated<>o.total_allocated OR n.modified<>o.modified OR n.identity<>o.identity
              UNION ALL
              SELECT n.path,NULL,n.total_allocated,n.size,'added' FROM entries n
              LEFT JOIN previous.entries o ON n.path=o.path
              WHERE o.path IS NULL AND n.path NOT IN (SELECT path FROM moves)
              UNION ALL
              SELECT o.path,NULL,-o.total_allocated,o.size,'removed' FROM previous.entries o
              LEFT JOIN entries n ON n.path=o.path
              WHERE n.path IS NULL AND o.path NOT IN (SELECT old_path FROM moves)
              UNION ALL SELECT * FROM moves)
            """;
        command.Parameters.AddWithValue("$search", search.ToUpperInvariant());
        string filter = " WHERE instr(upper(path),$search)>0";
        command.CommandText = changes + " SELECT COUNT(*) FROM changes" + filter;
        long count = (long)command.ExecuteScalar()!;
        command.CommandText = changes + " SELECT path,old_path,delta,size,kind FROM changes" + filter + " ORDER BY ABS(delta) DESC,path LIMIT 200 OFFSET $offset";
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        var rows = new List<object>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new
            {
                path = reader.GetString(0),
                oldPath = reader.IsDBNull(1) ? null : reader.GetString(1),
                delta = reader.GetInt64(2),
                size = reader.GetInt64(3),
                kind = reader.GetString(4)
            });
        }

        return new
        {
            count,
            rows
        };
    }

    public async Task Export(string destination, string kind, QueryRequest query, Job job, IAnalysisIndex? captured = null)
    {
        var store = captured ?? Current(query.Snapshot);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (kind == "csv")
            {
                await using var output = new StreamWriter(temp, false, new UTF8Encoding(true));
                await output.WriteLineAsync("Path,Directory,Size,Allocated,UniqueAllocated,ModifiedUtc,FileId,Links,Issue");
                foreach (var entry in store.All(query))
                {
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    var file = entry.File;
                    await output.WriteLineAsync(string.Join(',', Csv(file.Path), file.Directory, entry.TotalSize, file.Allocated,
                        entry.TotalAllocated, Csv(DateTimeOffset.FromUnixTimeMilliseconds(file.Modified).ToString("O")),
                        Csv(file.Identity), file.Links, Csv(file.Issue ?? "")));
                    job.Completed++;
                }
            }
            else if (kind == "snapshot")
            {
                using var archive = ZipFile.Open(temp, ZipArchiveMode.Create);
                await using (var manifest = new StreamWriter(archive.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
                {
                    await manifest.WriteAsync(Json.Text(new
                    {
                        format = "DiskManager",
                        version = 1,
                        scan = State.History.FirstOrDefault(s => s.Id == store.Metadata().Id) ?? store.Metadata()
                    }));
                }

                await using var output = new StreamWriter(archive.CreateEntry("entries.jsonl", CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
                foreach (var row in store.All())
                {
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    await output.WriteLineAsync(Json.Text(row.File));
                    job.Completed++;
                }
            }
            else
            {
                throw new ArgumentException("导出格式无效。");
            }

            job.Cancellation.Token.ThrowIfCancellationRequested();
            File.Move(temp, destination, true);
            job.Result = new
            {
                destination
            };
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static string Csv(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]))
        {
            value = "'" + value;
        }

        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static async IAsyncEnumerable<string> BoundedLines(StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        var result = new StringBuilder();
        var buffer = new char[16384];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellation)) > 0)
        {
            for (int index = 0; index < read; index++)
            {
                if (buffer[index] == '\n')
                {
                    yield return result.ToString().TrimEnd('\r');
                    result.Clear();
                }
                else
                {
                    result.Append(buffer[index]);
                }

                if (result.Length > 262144)
                {
                    throw new InvalidDataException("导入记录过长。");
                }
            }
        }
        if (result.Length > 0)
        {
            yield return result.ToString();
        }
    }

    public async Task Import(string source, Job job)
    {
        if (new FileInfo(source).Length > 2147483648)
        {
            throw new InvalidDataException("快照文件超过 2 GiB 导入上限。");
        }

        using var archive = ZipFile.OpenRead(source);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("快照缺少版本信息。");
        if (manifestEntry.Length > 8388608)
        {
            throw new InvalidDataException("快照头过长。");
        }

        using var manifestReader = new StreamReader(manifestEntry.Open());
        using var manifest = JsonDocument.Parse(await manifestReader.ReadToEndAsync(job.Cancellation.Token));
        if (manifest.RootElement.GetProperty("format").GetString() != "DiskManager" || manifest.RootElement.GetProperty("version").GetInt32() != 1)
        {
            throw new InvalidDataException("快照版本不受支持。");
        }

        var info = Json.Read<SnapshotInfo>(manifest.RootElement.GetProperty("scan"));
        if (info.Roots.Length is < 1 or > 32)
        {
            throw new InvalidDataException("快照扫描范围无效。");
        }

        string[] roots = info.Roots.Select(Paths.Normalize).ToArray();
        var data = archive.GetEntry("entries.jsonl") ?? throw new InvalidDataException("快照缺少文件记录。");
        if (data.Length > 8589934592)
        {
            throw new InvalidDataException("解压后的快照超过 8 GiB 上限。");
        }

        string id = Guid.NewGuid().ToString("N");
        TrackIndex(id);
        var store = new IndexStore(DatabasePath(id));
        store.Create();
        try
        {
            using var reader = new StreamReader(data.Open());
            using (var writer = new IndexStore.Writer(store))
            {
                long characters = 0;
                await foreach (string line in BoundedLines(reader, job.Cancellation.Token))
                {
                    characters += line.Length;
                    if (++job.Completed > 10000000 || characters > 8589934592)
                    {
                        throw new InvalidDataException("快照数据超限。");
                    }

                    var entry = JsonSerializer.Deserialize<FileEntry>(line, Json.Options) ?? throw new InvalidDataException("文件记录为空。");
                    if (entry.Path.Length > 32760 || entry.Name.Length > 255 || entry.Identity.Length > 128 ||
                        entry.Size < 0 || entry.Allocated < 0 || !roots.Any(r => Paths.Within(Paths.Normalize(entry.Path), r)))
                    {
                        throw new InvalidDataException("快照文件记录无效。");
                    }

                    string parent = roots.Contains(entry.Path, StringComparer.OrdinalIgnoreCase) ? "" : Path.GetDirectoryName(entry.Path) ?? "";
                    writer.Add(entry with
                    {
                        Parent = parent
                    });
                }
            }
            info = info with
            {
                Id = id,
                Roots = roots,
                Label = "导入 · " + info.Label,
                Imported = true,
                Pinned = true
            };
            store.FinalizeIndex(info, [], job.Cancellation.Token);
            store.Seal();
            lock (stateLock)
            {
                Update(state => state with { History = [.. state.History, info], Current = id, Garbage = (state.Garbage ?? []).Where(item => item != id).ToList() });
                Volatile.Write(ref liveIndex, null);
            }
            job.Result = info;
        }
        catch { File.Delete(store.FilePath); throw; }
        finally { FinishIndex(id); }
    }

}
