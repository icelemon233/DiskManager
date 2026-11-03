using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace DiskManager;

internal sealed record ScanAuthorization(string State, string? Message = null);

internal sealed class ScanBroker(Func<ProcessStartInfo, Process?>? launch = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private NamedPipeServerStream? controller;
    private Process? helper;
    private bool attempted;
    private bool closed;
    private ScanAuthorization authorization = new("not-requested");
    public ScanAuthorization Authorization => Volatile.Read(ref authorization);

    public async Task Authorize(Job job, bool retry = false)
    {
        var permission = Stopwatch.StartNew();
        await gate.WaitAsync(job.Cancellation.Token);
        try
        {
            if (closed)
            {
                throw new InvalidOperationException("应用正在退出。");
            }

            if (controller?.IsConnected == true && helper?.HasExited == false)
            {
                return;
            }

            if (attempted && !retry)
            {
                throw new InvalidOperationException("快扫尚未授权，请重新授权。");
            }

            await ReleaseSession();
            attempted = true;
            Volatile.Write(ref authorization, new("waiting"));
            job.Phase = "等待 Windows 授权";
            string pipeName = "DiskManager.Session." + Guid.NewGuid().ToString("N");
            string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            controller = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in new[] { "--mft-session", pipeName, secret })
            {
                start.ArgumentList.Add(argument);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            using var cancel = timeout.Token.Register(controller.Dispose);
            helper = (launch ?? Process.Start)(start) ?? throw new IOException("无法启动扫描进程。");
            await controller.WaitForConnectionAsync(timeout.Token);
            using var reader = new BinaryReader(controller, Encoding.UTF8, true);
            if (ReadText(reader) != secret)
            {
                throw new IOException("扫描进程身份校验失败。");
            }

            job.Cancellation.Token.ThrowIfCancellationRequested();
            controller.WriteByte(1);
            Volatile.Write(ref authorization, new("ready"));
            job.Result = Authorization;
        }
        catch (Exception ex)
        {
            bool cancelled = ex is OperationCanceledException || ex is Win32Exception { NativeErrorCode: 1223 };
            Volatile.Write(ref authorization, new(cancelled ? "denied" : "unavailable", "快扫尚未授权"));
            await ReleaseSession();
            throw;
        }
        finally
        {
            job.PermissionMs += permission.ElapsedMilliseconds;
            gate.Release();
        }
    }

    public async Task Read(string root, string excluded, Action<FileEntry> accept, Job job, Action<string> issue)
    {
        await Authorize(job);
        await gate.WaitAsync(job.Cancellation.Token);
        try
        {
            job.Cancellation.Token.ThrowIfCancellationRequested();
            string pipeName = "DiskManager.Scan." + Guid.NewGuid().ToString("N");
            string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
            using var command = new BinaryWriter(controller ?? throw new IOException("扫描进程已关闭。"), Encoding.UTF8, true);
            command.Write((byte)1);
            WriteText(command, pipeName);
            WriteText(command, secret);
            WriteText(command, root);
            command.Flush();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await pipe.WaitForConnectionAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!job.Cancellation.IsCancellationRequested)
            {
                throw new TimeoutException("等待扫描进程连接超时。");
            }
            try
            {
                ReadProtocol(pipe, secret, root, excluded, accept, job, issue, timeout.Token);
            }
            catch (Exception) when (job.Cancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException(job.Cancellation.Token);
            }
        }
        finally
        {
            if (helper?.HasExited != false)
            {
                Volatile.Write(ref authorization, new("unavailable", "快扫授权进程已退出"));
            }

            gate.Release();
        }
    }

    private async Task ReleaseSession()
    {
        controller?.Dispose();
        controller = null;
        if (helper == null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await helper.WaitForExitAsync(timeout.Token);
        helper.Dispose();
        helper = null;
    }

    public async Task Close()
    {
        await gate.WaitAsync();
        try
        {
            closed = true;
            await ReleaseSession();
        }
        finally { gate.Release(); }
    }

    internal static async Task RunSession(string[] args, Func<string[], Task>? scan = null)
    {
        if (args.Length != 3 || !args[1].StartsWith("DiskManager.Session.", StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[1]["DiskManager.Session.".Length..], "N", out _) || args[2].Length != 64)
        {
            throw new ArgumentException("授权进程参数无效。");
        }

        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(30000);
        using var reader = new BinaryReader(pipe, Encoding.UTF8, true);
        using (var writer = new BinaryWriter(pipe, Encoding.UTF8, true))
        {
            WriteText(writer, args[2]);
            writer.Flush();
        }
        if (pipe.ReadByte() != 1)
        {
            return;
        }

        while (true)
        {
            int operation = pipe.ReadByte();
            if (operation == -1)
            {
                return;
            }

            if (operation != 1)
            {
                throw new InvalidDataException("授权进程请求无效。");
            }

            string dataPipe = ReadText(reader), secret = ReadText(reader), root = ReadText(reader);
            try
            {
                await (scan ?? RunHelper)(["--mft-pipe", dataPipe, secret, root]);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { }
        }
    }

    internal static void ReadProtocol(NamedPipeServerStream pipe, string secret, string root, string excluded,
        Action<FileEntry> accept, Job job, Action<string> issue, CancellationToken startupCancellation, Action? authenticated = null)
    {
        using var cancel = job.Cancellation.Token.Register(pipe.Dispose);
        using var buffered = new BufferedStream(pipe, 1048576);
        using var reader = new BinaryReader(buffered, Encoding.UTF8, true);
        using (startupCancellation.Register(pipe.Dispose))
        {
            if (ReadText(reader) != secret)
            {
                throw new IOException("扫描进程身份校验失败。");
            }
        }

        pipe.WriteByte(1);
        authenticated?.Invoke();
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        while (true)
        {
            job.Cancellation.Token.ThrowIfCancellationRequested();
            switch (reader.ReadByte())
            {
                case 1:
                    long done = reader.ReadInt64(), total = reader.ReadInt64();
                    job.Phase = $"{root} 读取 NTFS 文件记录 · {done / 1048576} / {total / 1048576} MiB";
                    break;
                case 2:
                    issue(ReadText(reader));
                    break;
                case 3:
                    int count = reader.ReadInt32();
                    if (count is < 1 or > 256)
                    {
                        throw new InvalidDataException("扫描批次大小无效。");
                    }

                    for (int index = 0; index < count; index++)
                    {
                        string path = ReadText(reader);
                        bool directory = reader.ReadBoolean();
                        long size = reader.ReadInt64(), allocated = reader.ReadInt64(), modified = reader.ReadInt64(), created = reader.ReadInt64();
                        string identity = ReadText(reader);
                        uint attributes = reader.ReadUInt32(), links = reader.ReadUInt32();
                        string problem = ReadText(reader);
                        string name = Path.GetFileName(path);
                        string parent = Path.GetDirectoryName(path) ?? "";
                        if (parents.TryGetValue(parent, out var shared))
                        {
                            parent = shared;
                        }
                        else
                        {
                            parents.Add(parent, parent);
                        }

                        var entry = new FileEntry(path, parent, name.Length == 0 ? path : name,
                            directory, size, allocated, modified, created, identity, attributes, links, problem.Length == 0 ? null : problem);
                        if (!Paths.Within(entry.Path, root))
                        {
                            throw new InvalidDataException("扫描结果超出范围。");
                        }

                        if (Paths.Within(entry.Path, excluded))
                        {
                            continue;
                        }

                        accept(entry);
                        job.Completed++;
                    }
                    job.Phase = $"{root} 建立文件索引";
                    job.Total = null;
                    break;
                case 4:
                    throw new IOException(ReadText(reader));
                case 5:
                    return;
                case 6:
                    var statistics = System.Text.Json.JsonSerializer.Deserialize<MftReadStatistics>(ReadText(reader), Json.Options)
                        ?? throw new InvalidDataException("扫描统计无效。");
                    if (statistics.Root != root || statistics.ReadBytes < 0 || statistics.ReadBytes > statistics.TableBytes)
                    {
                        throw new InvalidDataException("扫描统计范围无效。");
                    }

                    job.MftStatistics.Add(statistics);
                    break;
                default:
                    throw new InvalidDataException("扫描协议消息无效。");
            }
        }
    }

    internal static void WriteText(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > 262144)
        {
            throw new InvalidDataException("扫描文本长度超限。");
        }

        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    internal static string ReadText(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length is < 0 or > 262144)
        {
            throw new InvalidDataException("扫描文本长度无效。");
        }

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException("扫描进程提前关闭。");
        }

        return new UTF8Encoding(false, true).GetString(bytes);
    }

    internal static void WriteEntries(BinaryWriter writer, IReadOnlyCollection<FileEntry> entries)
    {
        writer.Write((byte)3);
        writer.Write(entries.Count);
        foreach (var entry in entries)
        {
            WriteText(writer, entry.Path);
            writer.Write(entry.Directory);
            writer.Write(entry.Size);
            writer.Write(entry.Allocated);
            writer.Write(entry.Modified);
            writer.Write(entry.Created);
            WriteText(writer, entry.Identity);
            writer.Write(entry.Attributes);
            writer.Write(entry.Links);
            WriteText(writer, entry.Issue ?? "");
        }
        writer.Flush();
    }

    public static async Task RunHelper(string[] args)
    {
        if (args.Length != 4 || !args[1].StartsWith("DiskManager.Scan.", StringComparison.Ordinal) || args[2].Length != 64)
        {
            throw new ArgumentException("扫描进程参数无效。");
        }

        // The server ACL is current-user-only. A per-launch secret authenticates this helper.
        // Avoid client-side owner-token equality across the UAC integrity boundary.
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using var buffered = new BufferedStream(pipe, 1048576);
        using var writer = new BinaryWriter(buffered, Encoding.UTF8, true);
        WriteText(writer, args[2]);
        writer.Flush();
        if (pipe.ReadByte() != 1)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        async Task WatchDisconnect()
        {
            try
            {
                await pipe.ReadExactlyAsync(new byte[1], cancellation.Token);
                cancellation.Cancel();
            }
            catch (OperationCanceledException) { }
            catch (IOException) { cancellation.Cancel(); }
        }
        var disconnected = WatchDisconnect();
        try
        {
            string root = Paths.Normalize(args[3]);
            if (root.Length != 3 || new DriveInfo(root).DriveFormat != "NTFS")
            {
                throw new ArgumentException("请选择 NTFS 卷。");
            }

            var lastProgress = Stopwatch.StartNew();
            var scanner = new MftReader(root, (done, total) =>
            {
                if (lastProgress.ElapsedMilliseconds < 300)
                {
                    return;
                }

                writer.Write((byte)1);
                writer.Write(done);
                writer.Write(total);
                writer.Flush();
                lastProgress.Restart();
            }, message => { writer.Write((byte)2); WriteText(writer, message); writer.Flush(); });
            var batch = new List<FileEntry>(256);
            foreach (var entry in scanner.Read(cancellation.Token))
            {
                batch.Add(entry);
                if (batch.Count < 256)
                {
                    continue;
                }

                WriteEntries(writer, batch);
                batch.Clear();
            }
            if (batch.Count > 0)
            {
                WriteEntries(writer, batch);
            }
            writer.Write((byte)6);
            WriteText(writer, Json.Text(scanner.Statistics));
            writer.Write((byte)5);
            writer.Flush();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                writer.Write((byte)4);
                WriteText(writer, ex.Message);
                writer.Flush();
            }
            catch (IOException) { }
        }
        finally
        {
            cancellation.Cancel();
            await disconnected;
        }
    }
}
