using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskManager;

// All volume access is read-only. Parsed records are metadata, never mutation targets.
internal sealed class MftReader(string root, Action<long, long> progress, Action<string> issue)
{
    private const ulong RecordMask = 0x0000ffffffffffff;
    internal sealed record NameRef(ulong Parent, string Name);
    internal sealed record Extent(long Vcn, long Lcn, long Clusters);
    internal sealed class Record
    {
        public ulong Index, Base;
        public ushort Sequence, Links;
        public bool Directory;
        public uint Attributes;
        public long Created, Modified;
        public long BadClusters;
        public readonly List<NameRef> Names = [];
        public (long Size, long Allocated)? Data;
        public Dictionary<string, (long Size, long Allocated)>? NamedStreams;
        public List<Extent>? Extents;
        public HashSet<ulong>? ExtraRecords;
        public byte[]? Bitmap;
        public long BitmapLength;
        public List<Extent>? BitmapExtents;
        public void SetStream(string name, long size, long allocated)
        {
            if (name.Length == 0)
            {
                Data = (size, allocated);
            }
            else
            {
                (NamedStreams ??= new(StringComparer.Ordinal))[name] = (size, allocated);
            }
        }
    }

    public MftReadStatistics? Statistics
    {
        get; private set;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    private static ulong U64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
    private static long I64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8));
    internal static long CountAllocatedClusters(ReadOnlySpan<byte> pairs)
    {
        long allocated = 0;
        int position = 0;
        while (position < pairs.Length)
        {
            byte header = pairs[position++];
            if (header == 0)
            {
                return allocated;
            }

            int lengthBytes = header & 15, offsetBytes = header >> 4;
            if (lengthBytes is < 1 or > 8 || offsetBytes > 8 || position + lengthBytes + offsetBytes > pairs.Length)
            {
                throw new InvalidDataException("数据区间无效。");
            }

            ulong clusters = 0;
            for (int i = 0; i < lengthBytes; i++)
            {
                clusters |= (ulong)pairs[position++] << (8 * i);
            }

            if (clusters == 0 || clusters > long.MaxValue)
            {
                throw new InvalidDataException("数据区间长度无效。");
            }

            if (offsetBytes > 0)
            {
                allocated = checked(allocated + (long)clusters);
            }

            position += offsetBytes;
        }
        throw new InvalidDataException("数据区间缺少结束标记。");
    }
    private static long Timestamp(ReadOnlySpan<byte> data, int offset)
    {
        long value = I64(data, offset);
        return value > 0 && value <= DateTime.MaxValue.ToFileTimeUtc()
            ? DateTimeOffset.FromFileTime(value).ToUnixTimeMilliseconds() : 0;
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> bytes, long position)
    {
        int read = 0;
        while (read < bytes.Length)
        {
            int count = RandomAccess.Read(handle, bytes[read..], position + read);
            if (count == 0)
            {
                throw new IOException("磁盘读取提前结束。");
            }

            read += count;
        }
    }

    internal static Record? Parse(Span<byte> bytes, ulong index, int sectorSize, bool fixup = true)
    {
        if (bytes.Length < 48 || !bytes[..4].SequenceEqual("FILE"u8) || (U16(bytes, 22) & 1) == 0)
        {
            return null;
        }

        int used = checked((int)U32(bytes, 24));
        if (used > bytes.Length || used < 48)
        {
            throw new InvalidDataException("文件记录长度无效。");
        }

        if (fixup)
        {
            int array = U16(bytes, 4), count = U16(bytes, 6);
            if (array < 8 || array + count * 2 > bytes.Length || count != bytes.Length / sectorSize + 1)
            {
                throw new InvalidDataException("文件记录更新序列无效。");
            }

            ushort signature = U16(bytes, array);
            for (int i = 1; i < count; i++)
            {
                int end = i * sectorSize - 2;
                if (U16(bytes, end) != signature)
                {
                    throw new InvalidDataException("扫描期间文件记录已变化。");
                }

                bytes[end] = bytes[array + 2 * i];
                bytes[end + 1] = bytes[array + 2 * i + 1];
            }
        }
        var record = new Record
        {
            Index = index,
            Base = U64(bytes, 32),
            Sequence = U16(bytes, 16),
            Links = U16(bytes, 18),
            Directory = (U16(bytes, 22) & 2) != 0
        };
        bool master = index == 0 || (record.Base & RecordMask) == 0 && record.Base != 0;
        for (int offset = U16(bytes, 20); offset + 8 <= used;)
        {
            uint type = U32(bytes, offset);
            if (type == uint.MaxValue)
            {
                break;
            }

            int length = checked((int)U32(bytes, offset + 4));
            if (length < 24 || offset + length > used)
            {
                throw new InvalidDataException("文件属性长度无效。");
            }

            bool resident = bytes[offset + 8] == 0;
            int nameLength = bytes[offset + 9], nameOffset = U16(bytes, offset + 10);
            if (nameLength > 0 && (nameOffset < 16 || nameOffset + nameLength * 2 > length))
            {
                throw new InvalidDataException("文件流名称无效。");
            }

            string streamName = nameLength == 0 ? "" : Encoding.Unicode.GetString(bytes.Slice(offset + nameOffset, nameLength * 2));
            bool bitmapStream = type == 0xB0 && master && streamName.Length == 0;
            if (resident)
            {
                int contentLength = checked((int)U32(bytes, offset + 16)), content = offset + U16(bytes, offset + 20);
                if (content < offset + 24 || content + contentLength > offset + length)
                {
                    throw new InvalidDataException("常驻属性范围无效。");
                }

                if (type == 0x10 && contentLength >= 36)
                {
                    record.Created = Timestamp(bytes, content);
                    record.Modified = Timestamp(bytes, content + 8);
                    record.Attributes = U32(bytes, content + 32);
                }
                if (type == 0x30 && contentLength >= 66 && bytes[content + 65] != 2)
                {
                    int characters = bytes[content + 64];
                    if (66 + characters * 2 > contentLength)
                    {
                        throw new InvalidDataException("文件名长度无效。");
                    }

                    string name = Encoding.Unicode.GetString(bytes.Slice(content + 66, characters * 2));
                    if (!name.Contains('\\') && !name.Contains('/') && !name.Contains('\0'))
                    {
                        record.Names.Add(new(U64(bytes, content), name));
                    }
                }
                if (type == 0x80)
                {
                    record.SetStream(streamName, contentLength, 0);
                }
                if (type == 0xB0 && master && streamName.Length == 0)
                {
                    record.Bitmap = bytes.Slice(content, contentLength).ToArray();
                    record.BitmapLength = contentLength;
                }
                if (type == 0x20 && master)
                {
                    for (int item = content; item + 26 <= content + contentLength;)
                    {
                        int itemLength = U16(bytes, item + 4);
                        if (itemLength < 26 || item + itemLength > content + contentLength)
                        {
                            break;
                        }

                        if (U32(bytes, item) == 0x80)
                        {
                            (record.ExtraRecords ??= []).Add(U64(bytes, item + 16));
                        }

                        item += itemLength;
                    }
                }
            }
            else if (type == 0x80 || bitmapStream)
            {
                try
                {
                    if (bitmapStream && (U16(bytes, offset + 12) & 0xC0FF) != 0)
                    {
                        offset += length;
                        continue;
                    }
                    if (length < 64)
                    {
                        throw new InvalidDataException("非常驻属性长度无效。");
                    }

                    long vcn = I64(bytes, offset + 16);
                    int runOffset = U16(bytes, offset + 32);
                    if (runOffset < 64 || runOffset >= length)
                    {
                        throw new InvalidDataException("数据区间偏移无效。");
                    }

                    if ((index == 8 || record.Base != 0 && (record.Base & RecordMask) == 8) && streamName.Equals("$Bad", StringComparison.OrdinalIgnoreCase))
                    {
                        record.BadClusters = checked(record.BadClusters + CountAllocatedClusters(bytes.Slice(offset + runOffset, length - runOffset)));
                    }

                    if (vcn == 0)
                    {
                        long allocated = I64(bytes, offset + 40), size = I64(bytes, offset + 48);
                        ushort flags = U16(bytes, offset + 12);
                        if ((flags & 0x8001) != 0 && runOffset >= 72)
                        {
                            allocated = I64(bytes, offset + 64);
                        }

                        if (size < 0 || allocated < 0)
                        {
                            throw new InvalidDataException("文件长度无效。");
                        }

                        if (bitmapStream)
                        {
                            record.BitmapLength = size;
                        }
                        else
                        {
                            record.SetStream(streamName, size, allocated);
                        }
                    }
                    if (index == 0 || (record.Base & RecordMask) == 0 && record.Base != 0)
                    {
                        long lcn = 0;
                        for (int run = offset + runOffset; run < offset + length && bytes[run] != 0;)
                        {
                            byte header = bytes[run++];
                            int sizeBytes = header & 15, deltaBytes = header >> 4;
                            if (sizeBytes is < 1 or > 8 || deltaBytes > 8 || run + sizeBytes + deltaBytes > offset + length)
                            {
                                throw new InvalidDataException("MFT 数据区间无效。");
                            }

                            ulong clusters = 0;
                            for (int i = 0; i < sizeBytes; i++)
                            {
                                clusters |= (ulong)bytes[run++] << (8 * i);
                            }

                            long delta = 0;
                            for (int i = 0; i < deltaBytes; i++)
                            {
                                delta |= (long)bytes[run++] << (8 * i);
                            }

                            if (deltaBytes is > 0 and < 8 && (bytes[run - 1] & 128) != 0)
                            {
                                delta |= -1L << (8 * deltaBytes);
                            }

                            if (clusters == 0 || clusters > long.MaxValue || deltaBytes == 0)
                            {
                                throw new InvalidDataException("MFT 数据区间不受支持。");
                            }

                            lcn = checked(lcn + delta);
                            if (lcn < 0)
                            {
                                throw new InvalidDataException("MFT 磁盘偏移无效。");
                            }

                            if (streamName.Length == 0)
                            {
                                if (bitmapStream)
                                {
                                    (record.BitmapExtents ??= []).Add(new(vcn, lcn, (long)clusters));
                                }
                                else
                                {
                                    (record.Extents ??= []).Add(new(vcn, lcn, (long)clusters));
                                }
                            }

                            vcn = checked(vcn + (long)clusters);
                        }
                    }
                }
                catch (Exception ex) when (bitmapStream && ex is InvalidDataException or OverflowException or ArgumentException)
                {
                    record.BitmapLength = 0;
                    record.BitmapExtents = null;
                }
            }
            else if (type == 0x20 && index == 0)
            {
                throw new NotSupportedException("该卷 MFT 属性列表需要目录扫描回退。");
            }

            offset += length;
        }
        if (record.Directory)
        {
            record.Attributes |= 0x10;
        }

        return record;
    }

    internal static bool HasAllocatedRecords(ReadOnlySpan<byte> bitmap, long firstRecord, int count)
    {
        if (firstRecord < 0 || count < 0 || firstRecord > (long)bitmap.Length * 8 - count)
        {
            return true;
        }

        for (long index = firstRecord; index < firstRecord + count; index++)
        {
            if ((bitmap[(int)(index >> 3)] & (1 << (int)(index & 7))) != 0)
            {
                return true;
            }
        }

        return false;
    }

    internal static byte[]? ReadBitmap(SafeFileHandle volume, Record master, int cluster, int sector, long length, int recordSize)
    {
        long needed = (length / recordSize + 7) / 8;
        if (needed is < 1 or > 67108864 || master.BitmapLength < needed)
        {
            return null;
        }

        byte[] bitmap;
        if (master.Bitmap != null)
        {
            if (master.Bitmap.Length < needed)
            {
                return null;
            }

            bitmap = master.Bitmap;
        }
        else
        {
            if (master.BitmapExtents == null)
            {
                return null;
            }

            bitmap = new byte[checked((int)((needed + sector - 1) / sector * sector))];
            long filled = 0;
            try
            {
                foreach (var extent in master.BitmapExtents.OrderBy(item => item.Vcn).DistinctBy(item => item.Vcn))
                {
                    if (checked(extent.Vcn * cluster) != filled)
                    {
                        return null;
                    }

                    int count = (int)Math.Min(checked(extent.Clusters * cluster), bitmap.Length - filled);
                    ReadExactly(volume, bitmap.AsSpan((int)filled, count), checked(extent.Lcn * cluster));
                    filled += count;
                    if (filled >= needed)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OverflowException or ArgumentException) { return null; }
            if (filled < needed)
            {
                return null;
            }
        }
        // These records are permanently allocated. An inconsistent map must not suppress reads.
        if (!HasAllocatedRecords(bitmap, 0, 1) || !HasAllocatedRecords(bitmap, 5, 1))
        {
            return null;
        }

        return bitmap;
    }

    public IEnumerable<FileEntry> Read(CancellationToken cancellation)
    {
        var metadataTimer = Stopwatch.StartNew();
        using var volume = WindowsFiles.CreateFile(@"\\.\" + root[..2], 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (volume.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var geometry = new byte[96];
        if (!WindowsFiles.DeviceIoControl(volume, 0x90064, null, 0, geometry, geometry.Length, out _, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        int sector = checked((int)U32(geometry, 40)), cluster = checked((int)U32(geometry, 44)), recordSize = checked((int)U32(geometry, 48));
        long length = I64(geometry, 56), start = I64(geometry, 64);
        if (sector < 512 || cluster < sector || recordSize < sector || recordSize > 65536 || length < recordSize)
        {
            throw new InvalidDataException("NTFS 卷几何参数无效。");
        }

        var first = new byte[recordSize];
        ReadExactly(volume, first, checked(start * cluster));
        var master = Parse(first, 0, sector) ?? throw new InvalidDataException("无法读取 MFT 根记录。");
        foreach (ulong reference in master.ExtraRecords?.ToArray() ?? [])
        {
            if ((reference & RecordMask) == 0)
            {
                continue;
            }

            byte[] input = BitConverter.GetBytes(reference), output = new byte[recordSize + 16];
            if (!WindowsFiles.DeviceIoControl(volume, 0x90068, input, input.Length, output, output.Length, out _, IntPtr.Zero) ||
                (U64(output, 0) & RecordMask) != (reference & RecordMask))
            {
                throw new IOException("无法读取 MFT 扩展区间。");
            }

            var extra = Parse(output.AsSpan(12, recordSize), reference & RecordMask, sector, false);
            if (extra == null || (extra.Base & RecordMask) != 0)
            {
                throw new InvalidDataException("MFT 扩展记录无效。");
            }

            if (extra.Extents != null)
            {
                (master.Extents ??= []).AddRange(extra.Extents);
            }

            if (extra.BitmapExtents != null)
            {
                (master.BitmapExtents ??= []).AddRange(extra.BitmapExtents);
            }

            if (extra.BitmapLength > 0)
            {
                master.BitmapLength = extra.BitmapLength;
            }

            if (extra.Bitmap != null)
            {
                master.Bitmap = extra.Bitmap;
            }
        }
        var records = new Dictionary<ulong, Record>();
        var extensions = new List<Record>();
        byte[]? bitmap = ReadBitmap(volume, master, cluster, sector, length, recordSize);
        var skipped = new List<(long Logical, long Physical, int Count)>();
        long expectedVcn = 0, completed = 0, readBytes = 0, ioTicks = 0, parseTicks = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        void ReadChunk(long logical, long physical, int count)
        {
            cancellation.ThrowIfCancellationRequested();
            long started = Stopwatch.GetTimestamp();
            ReadExactly(volume, buffer.AsSpan(0, count), physical);
            ioTicks += Stopwatch.GetTimestamp() - started;
            readBytes += count;
            started = Stopwatch.GetTimestamp();
            for (int offset = 0; offset < count; offset += recordSize)
            {
                ulong index = (ulong)((logical + offset) / recordSize);
                try
                {
                    var record = Parse(buffer.AsSpan(offset, recordSize), index, sector);
                    if (record == null)
                    {
                        continue;
                    }

                    if (record.Base != 0)
                    {
                        extensions.Add(record);
                    }
                    else
                    {
                        records[index] = record;
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
                {
                    issue($"MFT 记录 {index} — {ex.Message}");
                }
            }
            parseTicks += Stopwatch.GetTimestamp() - started;
        }
        try
        {
            foreach (var extent in (master.Extents ?? []).OrderBy(e => e.Vcn).DistinctBy(e => e.Vcn))
            {
                if (extent.Vcn != expectedVcn)
                {
                    throw new InvalidDataException("MFT 区间不连续，需使用目录扫描。");
                }

                expectedVcn += extent.Clusters;
                long extentBytes = Math.Min(extent.Clusters * cluster, length - extent.Vcn * cluster);
                for (long position = 0; position < extentBytes;)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(1024 * 1024, extentBytes - position);
                    count -= count % recordSize;
                    if (count == 0)
                    {
                        break;
                    }

                    long logical = extent.Vcn * cluster + position, physical = extent.Lcn * cluster + position;
                    if (bitmap != null && !HasAllocatedRecords(bitmap, logical / recordSize, count / recordSize))
                    {
                        skipped.Add((logical, physical, count));
                    }
                    else
                    {
                        ReadChunk(logical, physical, count);
                    }

                    position += count;
                    completed += count;
                    progress(completed, length);
                }
            }
            if (skipped.Count > 0)
            {
                byte[]? freshBitmap = null;
                try
                {
                    ReadExactly(volume, first, checked(start * cluster));
                    var freshMaster = Parse(first, 0, sector);
                    if (freshMaster != null)
                    {
                        freshBitmap = ReadBitmap(volume, freshMaster, cluster, sector, length, recordSize);
                    }
                }
                catch (Exception ex) when (ex is IOException or OverflowException or ArgumentException) { }
                foreach (var chunk in skipped)
                {
                    if (freshBitmap == null || HasAllocatedRecords(freshBitmap, chunk.Logical / recordSize, chunk.Count / recordSize))
                    {
                        ReadChunk(chunk.Logical, chunk.Physical, chunk.Count);
                    }
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        var latestGeometry = new byte[96];
        if (WindowsFiles.DeviceIoControl(volume, 0x90064, null, 0, latestGeometry, latestGeometry.Length, out _, IntPtr.Zero) && I64(latestGeometry, 56) != length)
        {
            issue("扫描期间文件表长度发生变化，请刷新结果。");
        }

        if (completed < length - recordSize)
        {
            throw new InvalidDataException("MFT 读取不完整。");
        }

        foreach (var extension in extensions)
        {
            if (!records.TryGetValue(extension.Base & RecordMask, out var owner) || owner.Sequence != extension.Base >> 48)
            {
                continue;
            }

            owner.Names.AddRange(extension.Names);
            owner.BadClusters = checked(owner.BadClusters + extension.BadClusters);
            owner.Data ??= extension.Data;
            if (extension.NamedStreams != null)
            {
                foreach (var stream in extension.NamedStreams)
                {
                    (owner.NamedStreams ??= new(StringComparer.Ordinal)).TryAdd(stream.Key, stream.Value);
                }
            }
        }
        Statistics = new(root, length, readBytes, length / recordSize, records.Count + extensions.Count,
            (long)(ioTicks * 1000.0 / Stopwatch.Frequency), (long)(parseTicks * 1000.0 / Stopwatch.Frequency), metadataTimer.ElapsedMilliseconds, bitmap != null);
        var paths = new Dictionary<ulong, string> { [5] = root };
        string? Resolve(ulong index, HashSet<ulong> visiting)
        {
            if (paths.TryGetValue(index, out var saved))
            {
                return saved;
            }

            if (visiting.Count > 512 || !visiting.Add(index) || !records.TryGetValue(index, out var node))
            {
                return null;
            }

            var name = node.Names.FirstOrDefault();
            if (name == null || name.Name is "." or "..")
            {
                return null;
            }

            if (!records.TryGetValue(name.Parent & RecordMask, out var parentRecord) || parentRecord.Sequence != name.Parent >> 48)
            {
                return null;
            }

            var parent = Resolve(name.Parent & RecordMask, visiting);
            if (parent == null)
            {
                return null;
            }

            return paths[index] = Path.Combine(parent, name.Name);
        }
        uint serial = U32(geometry, 0);
        foreach (var record in records.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            if (record.Index == 5)
            {
                yield return new(root, "", root, true, 0, 0, record.Modified, record.Created,
                    $"{serial:x8}:{((ulong)record.Sequence << 48 | 5):x16}", record.Attributes, record.Links);
                continue;
            }
            // Keep metadata records in the hierarchy and account for their allocated streams.

            long size = record.Data?.Size ?? 0;
            long allocated = checked((record.Data?.Allocated ?? 0) + (record.NamedStreams?.Values.Sum(stream => stream.Allocated) ?? 0));
            if (record.Index == 8)
            {
                allocated = checked((record.Data?.Allocated ?? 0) + (record.NamedStreams?.Where(stream => !stream.Key.Equals("$Bad", StringComparison.OrdinalIgnoreCase)).Sum(stream => stream.Value.Allocated) ?? 0) + record.BadClusters * cluster);
            }

            string identity = $"{serial:x8}:{((ulong)record.Sequence << 48 | record.Index):x16}";
            IEnumerable<NameRef> names = record.Names.Count > 1 ? record.Names.Distinct() : record.Names;
            foreach (var name in names)
            {
                if (!records.TryGetValue(name.Parent & RecordMask, out var parentRecord) || parentRecord.Sequence != name.Parent >> 48)
                {
                    continue;
                }

                var parent = paths.GetValueOrDefault(name.Parent & RecordMask) ?? Resolve(name.Parent & RecordMask, []);
                if (parent == null)
                {
                    issue($"MFT 记录 {record.Index} 的父目录不可用。");
                    continue;
                }
                string path = Path.Combine(parent, name.Name);
                yield return new(path, parent, name.Name, record.Directory, record.Directory ? 0 : size,
                    record.Directory ? 0 : allocated, record.Modified, record.Created,
                    identity, record.Attributes, record.Links);
            }
        }
    }
}
