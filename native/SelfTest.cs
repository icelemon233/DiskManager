using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO.Pipes;
using System.Text;
using System.Buffers.Binary;

namespace DiskManager;

internal static class SelfTest
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);

    public static async Task Run()
    {
        Check(MftReader.CountAllocatedClusters([0x03, 0xff, 0xff, 0x7f, 0]) == 0, "sparse bad-cluster map consumes no allocated clusters");
        Check(MftReader.CountAllocatedClusters([0x11, 3, 10, 0x01, 100, 0x11, 2, 20, 0]) == 5, "mixed bad-cluster map counts mapped runs only");
        await CheckHandshake();
        await CheckAuthorizationDenial();
        await CheckScanSession();
        string testRoot = Path.Combine(Path.GetTempPath(), "DiskManager-Test-" + Guid.NewGuid().ToString("N"));
        string fixture = Path.Combine(testRoot, "中文 fixture");
        Directory.CreateDirectory(fixture);
        CheckMftParsing(testRoot);
        await CheckTransport(testRoot);
        string first = Path.Combine(fixture, "first.txt"), duplicate = Path.Combine(fixture, "duplicate.txt"), alias = Path.Combine(fixture, "hardlink.txt");
        await File.WriteAllTextAsync(first, new string('a', 5000));
        File.Copy(first, duplicate);
        if (!CreateHardLink(alias, first, IntPtr.Zero))
        {
            throw new IOException("无法创建硬链接测试资料。");
        }

        var workspace = new Workspace(Path.Combine(testRoot, "state"));
        var scan = new Job("scan");
        await workspace.Scan(new([fixture], false), scan);
        var store = workspace.Current();
        var firstInfo = WindowsFiles.Inspect(first);
        var aliasInfo = WindowsFiles.Inspect(alias);
        Check(firstInfo.Identity == aliasInfo.Identity && firstInfo.Links == 2, "hard-link identity");
        Check(firstInfo.Allocated > firstInfo.Size && firstInfo.Allocated % 4096 == 0, "allocation includes cluster slack");
        using var summary = JsonDocument.Parse(Json.Text(store.Summary()));
        Check(summary.RootElement.GetProperty("count").GetInt64() == 3, "file count");
        Check(summary.RootElement.GetProperty("allocated").GetInt64() == firstInfo.Allocated * 2, "allocated hard-link deduplication");
        var duplicates = new Duplicates(workspace);
        var duplicateJob = new Job("duplicates");
        duplicates.Run(new(), duplicateJob);
        using var duplicateResult = JsonDocument.Parse(Json.Text(duplicates.List(0)));
        Check(duplicateResult.RootElement.GetProperty("count").GetInt32() == 1, "content duplicates");
        var operations = new Operations(workspace, duplicates);
        Check(operations.Protected(Path.GetPathRoot(fixture)!), "volume-root protection");
        Check(operations.Protected(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), "system-directory protection");
        var changedPlan = operations.Plan(new("delete", [duplicate]));
        await File.AppendAllTextAsync(duplicate, "changed");
        var changedJob = new Job("operations");
        await operations.Execute(changedPlan.Id, changedJob);
        Check(File.Exists(duplicate) && changedJob.Issues == 1, "changed-file rejection");
        var renamePlan = operations.Plan(new("rename", [duplicate], Name: "renamed.txt"));
        await operations.Execute(renamePlan.Id, new("operations"));
        Check(File.Exists(Path.Combine(fixture, "renamed.txt")), "rename");
        var caches = new Caches(workspace, operations);
        const string cleanupImpact = "Fixture cache removal requires a later restore.";
        using var cachePreview = JsonDocument.Parse(Json.Text(caches.Preview(new CacheTarget("fixture-cache", "Fixture cache", "files", fixture, cleanupImpact))));
        Check(cachePreview.RootElement.GetProperty("plan").GetProperty("warnings").EnumerateArray().Any(item => item.GetString() == cleanupImpact),
            "cache impact is retained in the execution preview");
        string archive = Path.Combine(testRoot, "snapshot.diskmanager");
        await workspace.Export(archive, "snapshot", new(Snapshot: workspace.State.History[0].Id), new("export"));
        await workspace.Import(archive, new("import"));
        Check(workspace.Current().Metadata().Imported, "snapshot round trip");
        string small = Path.Combine(fixture, "resident.txt");
        await File.WriteAllTextAsync(small, "tiny");
        Check(WindowsFiles.Inspect(small).Allocated == 0, "resident data has no separate clusters");
        string longDirectory = Path.Combine(fixture, new string('x', 120), new string('y', 120));
        Directory.CreateDirectory(longDirectory);
        string longFile = Path.Combine(longDirectory, "长路径.txt");
        await File.WriteAllTextAsync(longFile, new string('b', 5000));
        Check(WindowsFiles.Inspect(longFile).Size == 5000, "extended-length Unicode paths");
        string streams = Path.Combine(fixture, "streams.bin");
        await File.WriteAllTextAsync(streams, new string('z', 5000));
        await File.WriteAllTextAsync(streams + ":extra", new string('q', 5000));
        Check(WindowsFiles.Inspect(streams).Allocated == 16384, "alternate stream allocation");
        string sparsePath = Path.Combine(fixture, "sparse.bin");
        using (var sparse = new FileStream(sparsePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Check(WindowsFiles.DeviceIoControl(sparse.SafeFileHandle, 0x900c4, null, 0, [], 0, out _, IntPtr.Zero), "create sparse fixture");
            sparse.SetLength(8 * 1024 * 1024);
            sparse.Flush(true);
        }
        Check(WindowsFiles.Inspect(sparsePath).Size == 8 * 1024 * 1024 && WindowsFiles.Inspect(sparsePath).Allocated == 0, "sparse allocation");
        string compressedPath = Path.Combine(fixture, "compressed.bin");
        using (var compressed = new FileStream(compressedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Check(WindowsFiles.DeviceIoControl(compressed.SafeFileHandle, 0x9c040, [1, 0], 2, [], 0, out _, IntPtr.Zero), "create compressed fixture");
            compressed.Write(new byte[1024 * 1024]);
            compressed.Flush(true);
        }
        Check(WindowsFiles.Inspect(compressedPath).Allocated < WindowsFiles.Inspect(compressedPath).Size, "compressed allocation");
        string recyclable = Path.Combine(fixture, "DiskManager-recycle-test-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(recyclable, "Disposable DiskManager recycle integration fixture.");
        var originalIdentity = WindowsFiles.Inspect(recyclable);
        string? recycled = await Recycle.Send(recyclable, path => Check(path == recyclable, "recycle scope"), CancellationToken.None);
        Check(!File.Exists(recyclable) && recycled != null && File.Exists(recycled), "recoverable recycle destination");
        Check(WindowsFiles.Inspect(recycled!).Identity == originalIdentity.Identity, "recycle preserves file identity");
        Console.WriteLine(Json.Text(new
        {
            passed = true,
            testRoot
        }));
        // Retain isolated evidence for inspection. Integration harness owns fixture cleanup.
    }

    private static async Task CheckHandshake()
    {
        string name = "DiskManager.Scan." + Guid.NewGuid().ToString("N");
        string secret = new('A', 64);
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task helper = ScanBroker.RunHelper(["--mft-pipe", name, secret, "D:\\"]);
        await pipe.WaitForConnectionAsync(timeout.Token);
        using var reader = new BinaryReader(pipe, Encoding.UTF8, true);
        Check(ScanBroker.ReadText(reader) == secret, "helper handshake");
        pipe.WriteByte(0);
        await helper;
    }

    private static void CheckMftParsing(string testRoot)
    {
        byte[] raw = new byte[1024];
        void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(offset, 2), value);
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset, 4), value);
        void U64(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(offset, 8), value);
        "FILE"u8.CopyTo(raw);
        U16(4, 48);
        U16(6, 3);
        U16(16, 1);
        U16(20, 56);
        U16(22, 1);
        U32(24, 320);
        U32(28, 1024);
        U16(48, 0xAAAA);
        U16(510, 0xAAAA);
        U16(1022, 0xAAAA);
        string name = "目录🙂.bin";
        U32(56, 0x30);
        U32(60, 112);
        U32(72, (uint)(66 + name.Length * 2));
        U16(76, 24);
        U64(80, (1UL << 48) | 5);
        raw[144] = (byte)name.Length;
        raw[145] = 1;
        Encoding.Unicode.GetBytes(name).CopyTo(raw, 146);
        U32(168, 0x80);
        U32(172, 72);
        raw[176] = 1;
        U64(192, 1);
        U16(200, 64);
        U64(208, 8192);
        U64(216, 5000);
        U64(224, 5000);
        raw[232] = 0x11;
        raw[233] = 2;
        raw[234] = 7;
        U32(240, 0xB0);
        U32(244, 72);
        raw[248] = 1;
        U16(272, 64);
        U64(280, 4096);
        U64(288, 512);
        U64(296, 512);
        raw[304] = 0x11;
        raw[305] = 1;
        raw[306] = 1;
        U32(312, uint.MaxValue);
        byte[] reusable = (byte[])raw.Clone();
        var parsed = MftReader.Parse(reusable, 42, 512) ?? throw new InvalidOperationException("MFT fixture did not parse");
        Array.Clear(reusable);
        Check(parsed.Names.Single().Name == name && parsed.Data?.Size == 5000 && parsed.Data?.Allocated == 8192,
            "MFT fields survive input buffer reuse");
        var master = MftReader.Parse((byte[])raw.Clone(), 0, 512)!;
        Check(master.Extents?.Single().Lcn == 7 && master.BitmapExtents?.Single().Lcn == 1,
            "MFT data and allocation-bitmap extents remain separate");
        using var file = new FileStream(Path.Combine(testRoot, "mft-bitmap.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        file.SetLength(8192);
        byte[] bits = new byte[512];
        bits[0] = 0x21;
        bits[128] = 1;
        file.Position = 4096;
        file.Write(bits);
        file.Flush(true);
        var bitmap = MftReader.ReadBitmap(file.SafeFileHandle, master, 4096, 512, 4194304, 1024)!;
        Check(bitmap != null && MftReader.HasAllocatedRecords(bitmap, 0, 1024) &&
            MftReader.HasAllocatedRecords(bitmap, 1024, 1024) && !MftReader.HasAllocatedRecords(bitmap, 2048, 1024),
            "bitmap identifies occupied and empty MFT ranges");
        Check(!MftReader.HasAllocatedRecords([1], 1, 7) && MftReader.HasAllocatedRecords([1], 8, 1),
            "bitmap partial-byte and missing-coverage ranges are conservative");
        file.Position = 4096 + 256;
        file.WriteByte(1);
        file.Flush(true);
        var refreshed = MftReader.ReadBitmap(file.SafeFileHandle, master, 4096, 512, 4194304, 1024)!;
        Check(!MftReader.HasAllocatedRecords(bitmap, 2048, 1024) && MftReader.HasAllocatedRecords(refreshed, 2048, 1024),
            "bitmap reread detects records allocated during scanning");
        master.BitmapExtents = [new(1, 1, 1)];
        Check(MftReader.ReadBitmap(file.SafeFileHandle, master, 4096, 512, 4194304, 1024) == null,
            "incomplete bitmap mappings fall back to full metadata reads");
        U16(252, 0x8000);
        var unsupported = MftReader.Parse((byte[])raw.Clone(), 0, 512)!;
        Check(unsupported.Data?.Size == 5000 && unsupported.BitmapExtents == null,
            "unsupported bitmap encoding preserves normal MFT data");
        U16(252, 0);
        U16(22, 0);
        _ = MftReader.Parse(raw, 42, 512);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 10000; iteration++)
        {
            if (MftReader.Parse(raw, 42, 512) != null)
            {
                throw new InvalidOperationException("Free MFT record was retained");
            }
        }

        Check(GC.GetAllocatedBytesForCurrentThread() - allocated < 1024, "unused MFT records avoid per-record heap allocations");
    }

    private static async Task CheckAuthorizationDenial()
    {
        int launches = 0;
        var broker = new ScanBroker(_ =>
        {
            launches++;
            throw new System.ComponentModel.Win32Exception(1223);
        });
        try
        {
            await broker.Authorize(new("authorization"));
        }
        catch (System.ComponentModel.Win32Exception) { }
        Check(broker.Authorization.State == "denied", "cancelled startup permission is reported");
        try
        {
            await broker.Read("D:\\", "", _ => { }, new("scan"), _ => { });
        }
        catch (InvalidOperationException) { }
        Check(launches == 1, "scan after cancelled startup does not repeat UAC");
        try
        {
            await broker.Authorize(new("authorization"), retry: true);
        }
        catch (System.ComponentModel.Win32Exception) { }
        Check(launches == 2, "explicit authorization retries the request");
        await broker.Close();
    }

    private static async Task CheckScanSession()
    {
        string name = "DiskManager.Session." + Guid.NewGuid().ToString("N");
        string secret = new('C', 64);
        using var controller = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int scans = 0;
        var helper = Task.Run(() => ScanBroker.RunSession(["--mft-session", name, secret], async args =>
        {
            using var data = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
            await data.ConnectAsync(timeout.Token);
            using var writer = new BinaryWriter(data, Encoding.UTF8, true);
            ScanBroker.WriteText(writer, args[2]);
            writer.Flush();
            Check(data.ReadByte() == 1, "session scan handshake");
            scans++;
            ScanBroker.WriteEntries(writer, [new(args[3] + "session.txt", args[3], "session.txt", false, 8, 0, 0, 0, "session-file", 32, 1)]);
            writer.Write((byte)5);
            writer.Flush();
        }));
        await controller.WaitForConnectionAsync(timeout.Token);
        using var reader = new BinaryReader(controller, Encoding.UTF8, true);
        Check(ScanBroker.ReadText(reader) == secret, "persistent helper handshake");
        controller.WriteByte(1);
        using (var command = new BinaryWriter(controller, Encoding.UTF8, true))
        {
            foreach (string root in new[] { "D:\\", "E:\\" })
            {
                string dataName = "DiskManager.Scan." + Guid.NewGuid().ToString("N");
                using var data = new NamedPipeServerStream(dataName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                command.Write((byte)1);
                ScanBroker.WriteText(command, dataName);
                ScanBroker.WriteText(command, secret);
                ScanBroker.WriteText(command, root);
                command.Flush();
                await data.WaitForConnectionAsync(timeout.Token);
                var job = new Job("scan");
                ScanBroker.ReadProtocol(data, secret, root, "C:\\Excluded", _ => { }, job, _ => { }, timeout.Token);
                Check(job.Completed == 1, "session result delivered");
            }
        }
        controller.Dispose();
        await helper.WaitAsync(timeout.Token);
        Check(scans == 2, "one helper serves multiple volumes and exits with its owner");
    }

    private static async Task CheckTransport(string testRoot)
    {
        string name = "DiskManager.Scan." + Guid.NewGuid().ToString("N");
        string secret = new('B', 64);
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var producer = Task.Run(async () =>
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);
            using var buffered = new BufferedStream(client, 1048576);
            using var output = new BinaryWriter(buffered, Encoding.UTF8, true);
            ScanBroker.WriteText(output, secret);
            output.Flush();
            Check(client.ReadByte() == 1, "scan protocol authorization");
            for (int batch = 0; batch < 16; batch++)
            {
                var entries = Enumerable.Range(batch * 256, 256).Select(index => new FileEntry(
                    $"D:\\ProtocolFixture\\文件-{index}.bin", "D:\\ProtocolFixture", $"文件-{index}.bin", false,
                    index + 1, 4096, 0, 0, index.ToString(), 32, 1)).ToArray();
                ScanBroker.WriteEntries(output, entries);
            }
            output.Write((byte)6);
            ScanBroker.WriteText(output, Json.Text(new MftReadStatistics("D:\\", 4194304, 4194304, 4096, 4096, 10, 20, 30, false)));
            output.Write((byte)5);
            output.Flush();
        });
        await pipe.WaitForConnectionAsync(timeout.Token);
        var store = new IndexStore(Path.Combine(testRoot, "protocol.db"));
        store.Create();
        var job = new Job("protocol");
        using (var writer = new IndexStore.Writer(store))
        {
            ScanBroker.ReadProtocol(pipe, secret, "D:\\", testRoot, writer.Add, job, _ => { }, timeout.Token);
        }

        await producer;
        Check(job.Completed == 4096, "all pipe batches and completion survive peer disconnect");
        Check(job.MftStatistics.Single().ActiveRecords == 4096, "MFT phase statistics survive transport");
        using (var writer = new IndexStore.Writer(store))
        {
            writer.Add(new("D:\\ProtocolFixture\\Case.txt", "D:\\ProtocolFixture", "Case.txt", false, 1, 0, 0, 0, "case-upper", 32, 1));
            writer.Add(new("D:\\ProtocolFixture\\case.txt", "D:\\ProtocolFixture", "case.txt", false, 1, 0, 0, 0, "case-lower", 32, 1));
        }
        using var caseResult = JsonDocument.Parse(Json.Text(store.Query(new(Search: "case.txt"))));
        Check(caseResult.RootElement.GetProperty("count").GetInt64() == 2, "case-distinct file names retained");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Self-test failed: " + name);
        }
    }
}
