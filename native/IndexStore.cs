using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace DiskManager;

internal sealed class IndexStore(string path) : IAnalysisIndex
{
    public string FilePath { get; } = path;
    private bool scratch;
    public SqliteConnection Open(bool writable = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = writable ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        if (writable && scratch)
        {
            using var configure = connection.CreateCommand();
            configure.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA cache_size=-65536; PRAGMA temp_store=MEMORY;";
            configure.ExecuteNonQuery();
        }
        connection.CreateFunction<string, string, bool>("matches", (pattern, value) =>
            Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)));
        return connection;
    }

    public void Create()
    {
        scratch = true;
        using var connection = Open(true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA user_version=1;
            CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE entries (
                path TEXT PRIMARY KEY, parent TEXT NOT NULL,
                name TEXT NOT NULL, directory INTEGER NOT NULL, size INTEGER NOT NULL,
                allocated INTEGER NOT NULL, modified INTEGER NOT NULL, created INTEGER NOT NULL,
                identity TEXT NOT NULL, attributes INTEGER NOT NULL, links INTEGER NOT NULL,
                issue TEXT, extension TEXT NOT NULL, search_key TEXT NOT NULL,
                total_size INTEGER NOT NULL DEFAULT 0, total_allocated INTEGER NOT NULL DEFAULT 0,
                file_count INTEGER NOT NULL DEFAULT 0, folder_count INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE issues (message TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public void PrepareScratch() => scratch = true;

    public void Seal()
    {
        scratch = false;
        using (var connection = Open(true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
        }
        using var file = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        file.Flush(true);
    }

    public sealed class Writer : IDisposable
    {
        private readonly SqliteConnection connection;
        private SqliteTransaction transaction;
        private readonly SqliteCommand command;
        private int pending;

        public Writer(IndexStore store)
        {
            connection = store.Open(true);
            transaction = connection.BeginTransaction();
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO entries
                (path,parent,name,directory,size,allocated,modified,created,identity,attributes,links,issue,extension,search_key,total_size,total_allocated,file_count)
                VALUES($path,$parent,$name,$directory,$size,$allocated,$modified,$created,$identity,$attributes,$links,$issue,$extension,$key,$totalSize,$totalAllocated,$count)
                """;
            foreach (var name in new[] { "path", "parent", "name", "directory", "size", "allocated", "modified", "created", "identity", "attributes", "links", "issue", "extension", "key", "totalSize", "totalAllocated", "count" })
            {
                command.Parameters.Add(new SqliteParameter("$" + name, DBNull.Value));
            }

            command.Prepare();
        }

        public void Add(FileEntry entry)
        {
            object[] values = [entry.Path, entry.Parent, entry.Name, entry.Directory, entry.Size, entry.Allocated,
                entry.Modified, entry.Created, entry.Identity, entry.Attributes, entry.Links,
                (object?)entry.Issue ?? DBNull.Value, entry.Extension, entry.Path.ToUpperInvariant(),
                entry.Size, entry.Allocated, entry.Directory ? 0 : 1];
            for (int i = 0; i < values.Length; i++)
            {
                command.Parameters[i].Value = values[i];
            }

            command.ExecuteNonQuery();
            if (++pending >= 8192)
            {
                Flush();
            }
        }

        public void Flush()
        {
            transaction.Commit();
            transaction.Dispose();
            transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            pending = 0;
        }

        public void RemoveRoot(string root)
        {
            using var remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM entries WHERE path=$path OR substr(path,1,length($prefix))=$prefix";
            remove.Parameters.AddWithValue("$path", root);
            remove.Parameters.AddWithValue("$prefix", root.TrimEnd('\\') + "\\");
            remove.ExecuteNonQuery();
        }

        public void Dispose()
        {
            transaction.Commit();
            command.Dispose();
            transaction.Dispose();
            connection.Dispose();
        }
    }

    private sealed class Aggregate(string path, string parent)
    {
        public string Path { get; } = path;
        public string Parent { get; } = parent;
        public long Size, Allocated, Count, Folders;
    }

    public void FinalizeIndex(SnapshotInfo metadata, IEnumerable<string> issues, CancellationToken cancellation)
    {
        using var connection = Open(true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS entries_parent ON entries(parent);
            CREATE INDEX IF NOT EXISTS entries_identity ON entries(identity);
            CREATE INDEX IF NOT EXISTS entries_size ON entries(size);
            UPDATE entries AS candidate SET total_allocated=
              CASE WHEN path=(SELECT MIN(path) FROM entries WHERE identity=candidate.identity AND directory=0) THEN allocated ELSE 0 END
              WHERE directory=0 AND links>1;
            """;
        command.ExecuteNonQuery();
        var directories = new Dictionary<string, Aggregate>(StringComparer.Ordinal);
        command.CommandText = "SELECT path,parent FROM entries WHERE directory=1";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                directories.Add(reader.GetString(0), new(reader.GetString(0), reader.GetString(1)));
            }
        }

        command.CommandText = "SELECT parent,SUM(size),SUM(total_allocated),COUNT(*) FROM entries WHERE directory=0 GROUP BY parent";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!directories.TryGetValue(reader.GetString(0), out var aggregate))
                {
                    continue;
                }

                aggregate.Size = reader.GetInt64(1);
                aggregate.Allocated = reader.GetInt64(2);
                aggregate.Count = reader.GetInt64(3);
            }
        }
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "UPDATE entries SET total_size=$size,total_allocated=$allocated,file_count=$count,folder_count=$folders WHERE path=$path AND (total_size<>$size OR total_allocated<>$allocated OR file_count<>$count OR folder_count<>$folders)";
        command.Parameters.AddWithValue("$size", 0L);
        command.Parameters.AddWithValue("$allocated", 0L);
        command.Parameters.AddWithValue("$count", 0L);
        command.Parameters.AddWithValue("$path", "");
        command.Parameters.AddWithValue("$folders", 0L);
        foreach (var directory in directories.Values.OrderByDescending(d => d.Path.Length))
        {
            cancellation.ThrowIfCancellationRequested();
            if (directories.TryGetValue(directory.Parent, out var parent))
            {
                checked
                {
                    parent.Size += directory.Size;
                    parent.Allocated += directory.Allocated;
                    parent.Count += directory.Count;
                    parent.Folders += directory.Folders + 1;
                }
            }
            command.Parameters[0].Value = directory.Size;
            command.Parameters[1].Value = directory.Allocated;
            command.Parameters[2].Value = directory.Count;
            command.Parameters[3].Value = directory.Path;
            command.Parameters[4].Value = directory.Folders;
            command.ExecuteNonQuery();
        }
        command.Parameters.Clear();
        command.CommandText = "INSERT OR REPLACE INTO metadata(key,value) VALUES('scan',$value)";
        command.Parameters.AddWithValue("$value", Json.Text(metadata));
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO issues(message) VALUES($value)";
        foreach (var issue in issues.Take(10000))
        {
            command.Parameters[0].Value = issue;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        command.Transaction = null;
        command.Parameters.Clear();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS entries_allocated ON entries(total_allocated);
            CREATE INDEX IF NOT EXISTS entries_total_size ON entries(total_size);
            CREATE INDEX IF NOT EXISTS entries_files_allocated ON entries(directory,total_allocated DESC);
            CREATE INDEX IF NOT EXISTS entries_types ON entries(extension,directory,total_allocated DESC);
            CREATE INDEX IF NOT EXISTS entries_search ON entries(search_key);
            ANALYZE;
            """;
        command.ExecuteNonQuery();
    }

    private static string Filter(SqliteCommand command, QueryRequest query)
    {
        var clauses = new List<string> { "1=1" };
        void Add(string expression, string key, object value)
        {
            clauses.Add(expression);
            command.Parameters.AddWithValue(key, value);
        }
        if (query.Parent != null)
        {
            Add("parent=$parent", "$parent", query.Parent);
        }
        if (query.Scope != null)
        {
            string basis = query.Scope.TrimEnd('\\');
            Add("(path=$scope OR (path >= $scopeStart AND path < $scopeEnd))", "$scope", query.Scope);
            command.Parameters.AddWithValue("$scopeStart", basis + "\\");
            command.Parameters.AddWithValue("$scopeEnd", basis + "]");
        }

        if (query.FilesOnly)
        {
            clauses.Add("directory=0");
        }

        if (query.DirectoriesOnly)
        {
            clauses.Add("directory=1");
        }

        if (query.Search.Length > 0)
        {
            if (query.Search.Length > 512)
            {
                throw new ArgumentException("搜索内容过长。");
            }

            string expression = query.Mode switch
            {
                "wildcard" => "search_key GLOB $search",
                "regex" => "matches($search,path)",
                _ => "instr(search_key,$search)>0"
            };
            Add(expression, "$search", query.Mode == "regex" ? query.Search : query.Search.ToUpperInvariant());
        }
        if (query.Extension.Length > 0)
        {
            Add("extension=$extension", "$extension", query.Extension == "<none>" ? "" : query.Extension.ToLowerInvariant());
        }

        if (query.MinSize > 0)
        {
            Add("total_size >= $min", "$min", query.MinSize);
        }

        if (query.MaxSize.HasValue)
        {
            Add("total_size <= $max", "$max", query.MaxSize.Value);
        }

        if (query.Before.HasValue)
        {
            Add("modified <= $before", "$before", query.Before.Value);
        }

        if (query.After.HasValue)
        {
            Add("modified >= $after", "$after", query.After.Value);
        }

        return string.Join(" AND ", clauses);
    }

    private const string Columns = "path,parent,name,directory,size,allocated,modified,created,identity,attributes,links,issue,total_size,total_allocated,file_count";
    private static string FolderColumn(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('entries') WHERE name='folder_count'";
        return (long)check.ExecuteScalar()! > 0 ? "folder_count" : "NULL";
    }
    private static string SelectedColumns(SqliteConnection connection, string prefix = "") =>
        string.Join(',', Columns.Split(',').Select(name => prefix + name)) + "," +
        (FolderColumn(connection) is var folder && folder != "NULL" ? prefix + folder : "NULL");
    private static IndexedEntry ReadEntry(SqliteDataReader reader) => new(new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetInt64(4), reader.GetInt64(5),
        reader.GetInt64(6), reader.GetInt64(7), reader.GetString(8), (uint)reader.GetInt64(9), (uint)reader.GetInt64(10),
        reader.IsDBNull(11) ? null : reader.GetString(11)), reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14), reader.IsDBNull(15) ? null : reader.GetInt64(15));

    public object Query(QueryRequest query, CancellationToken cancellation = default)
    {
        using var connection = Open();
        using var interrupt = cancellation.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        using var command = connection.CreateCommand();
        cancellation.ThrowIfCancellationRequested();
        string table = "entries";
        string prefix = "";
        string filter;
        if (query.Search.Length > 0)
        {
            if (query.Search.Length > 512)
            {
                throw new ArgumentException("搜索内容过长。");
            }

            string remaining = Filter(command, query with
            {
                Search = ""
            });
            string search = query.Mode switch
            {
                "wildcard" => "search_key GLOB $search",
                "regex" => "matches($search,path)",
                _ => "instr(search_key,$search)>0"
            };
            command.Parameters.AddWithValue("$search", query.Mode == "regex" ? query.Search : query.Search.ToUpperInvariant());
            // Evaluate the covering text index before fetching rows for the remaining predicates.
            using var indexCheck = connection.CreateCommand();
            indexCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='entries_search'";
            string indexHint = query.Mode != "regex" && (long)indexCheck.ExecuteScalar()! > 0 ? " INDEXED BY entries_search" : "";
            command.CommandText = $"CREATE TEMP TABLE matched AS SELECT rowid AS id FROM entries{indexHint} WHERE CASE WHEN {search} THEN ({remaining}) ELSE 0 END";
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM matched";
            table = "matched CROSS JOIN entries e ON e.rowid=matched.id";
            prefix = "e.";
            filter = "1=1";
        }
        else
        {
            filter = Filter(command, query);
            command.CommandText = $"SELECT COUNT(*) FROM entries WHERE {filter}";
        }
        var count = (long)command.ExecuteScalar()!;
        string column = query.Sort switch
        {
            "size" => "total_size",
            "name" => "name COLLATE NOCASE",
            "modified" => "modified",
            "count" => "file_count",
            "folders" => FolderColumn(connection) == "NULL" ? "name" : "folder_count",
            _ => "total_allocated"
        };
        string direction = query.Descending ? "DESC" : "ASC";
        string columns = SelectedColumns(connection, prefix);
        command.CommandText = $"SELECT {columns} FROM {table} WHERE {filter} ORDER BY {prefix}{column} {direction},{prefix}path LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 1000));
        command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));
        using var reader = command.ExecuteReader();
        var rows = new List<IndexedEntry>();
        while (reader.Read())
        {
            rows.Add(ReadEntry(reader));
        }

        cancellation.ThrowIfCancellationRequested();
        return new
        {
            count,
            rows
        };
    }

    public IEnumerable<IndexedEntry> All(QueryRequest? query = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var filter = Filter(command, query ?? new());
        command.CommandText = $"SELECT {SelectedColumns(connection)} FROM entries WHERE {filter} ORDER BY path";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return ReadEntry(reader);
        }
    }

    public IndexedEntry? Find(string path)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectedColumns(connection)} FROM entries WHERE path=$path";
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    public object Summary(string? scope = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        string filter = Filter(command, new(Scope: scope)) + " AND +directory=0";
        // Global aggregates need most rows. Avoid a size-ranking index that scatters table reads.
        string table = scope == null ? "entries NOT INDEXED" : "entries";
        long count, size, allocated;
        if (scope == null)
        {
            var metadata = Metadata();
            count = metadata.Count;
            size = metadata.Size;
            allocated = metadata.Allocated;
        }
        else
        {
            command.CommandText = $"SELECT COUNT(*),COALESCE(SUM(size),0),COALESCE(SUM(total_allocated),0) FROM {table} WHERE {filter}";
            using var reader = command.ExecuteReader();
            reader.Read();
            count = reader.GetInt64(0);
            size = reader.GetInt64(1);
            allocated = reader.GetInt64(2);
        }
        command.CommandText = $"SELECT extension,COUNT(*),SUM(size),SUM(total_allocated) FROM {table} WHERE {filter} GROUP BY extension ORDER BY SUM(total_allocated) DESC,extension LIMIT 100";
        var types = new List<object>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                types.Add(new
                {
                    extension = reader.GetString(0),
                    count = reader.GetInt64(1),
                    size = reader.GetInt64(2),
                    allocated = reader.GetInt64(3)
                });
            }
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT message FROM issues LIMIT 100";
        var issues = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                issues.Add(reader.GetString(0));
            }
        }

        return new
        {
            count,
            size,
            allocated,
            types,
            issues
        };
    }

    public SnapshotInfo Metadata()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key='scan'";
        return System.Text.Json.JsonSerializer.Deserialize<SnapshotInfo>((string)command.ExecuteScalar()!, Json.Options)!;
    }

    public object Types(string? scope, int offset)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        string filter = Filter(command, new(Scope: scope)) + " AND +directory=0";
        string table = scope == null ? "entries NOT INDEXED" : "entries";
        command.CommandText = $"SELECT COUNT(DISTINCT extension) FROM entries WHERE {filter}";
        long count = (long)command.ExecuteScalar()!;
        command.CommandText = $"SELECT extension,COUNT(*),SUM(size),SUM(total_allocated) FROM {table} WHERE {filter} GROUP BY extension ORDER BY SUM(total_allocated) DESC,extension LIMIT 200 OFFSET $offset";
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        var rows = new List<object>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new
            {
                extension = reader.GetString(0),
                count = reader.GetInt64(1),
                size = reader.GetInt64(2),
                allocated = reader.GetInt64(3)
            });
        }

        return new
        {
            count,
            rows
        };
    }

    public void CopyTo(string destination)
    {
        using var source = Open();
        using var target = new IndexStore(destination).Open(true);
        source.BackupDatabase(target);
    }
}
