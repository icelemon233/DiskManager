using System.ComponentModel;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskManager;

internal static class WindowsFiles
{
    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle NativeCreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, byte[] information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, byte[] information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputLength,
        byte[] output, int outputLength, out int returned, IntPtr overlapped);

    internal static string ExtendedPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + path;
    internal static SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template) =>
        NativeCreateFile(ExtendedPath(path), access, share, security, creation, flags, template);

    public static string Canonical(string path)
    {
        path = Paths.Normalize(path);
        using var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var buffer = new StringBuilder(1024);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return Paths.Normalize(buffer.ToString());
    }

    private static SafeFileHandle OpenMutation(FileEntry expected, bool matchModified = true)
    {
        var handle = CreateFile(expected.Path, 0x10080, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            string identity = $"{info.Volume:x8}:{((ulong)info.IndexHigh << 32 | info.IndexLow):x16}";
            long size = (info.Attributes & 0x10) != 0 ? 0 : ((long)info.SizeHigh << 32) | info.SizeLow;
            if (identity != expected.Identity || Time(info.Creation) != expected.Created ||
                matchModified && (Time(info.Write) != expected.Modified || size != expected.Size))
            {
                throw new IOException("文件身份或内容状态已变化，请重新预览。");
            }

            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    public static void DeleteVerified(FileEntry expected)
    {
        using var handle = OpenMutation(expected, !expected.Directory);
        if (!SetFileInformationByHandle(handle, 4, [1], 1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public static void RenameVerified(FileEntry expected, string destination)
    {
        using var handle = OpenMutation(expected);
        byte[] name = Encoding.Unicode.GetBytes(ExtendedPath(destination));
        var information = new byte[20 + name.Length];
        BitConverter.GetBytes(name.Length).CopyTo(information, 16);
        name.CopyTo(information, 20);
        if (!SetFileInformationByHandle(handle, 3, information, (uint)information.Length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static long Allocation(SafeFileHandle handle, string path)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            while (!GetFileInformationByHandleEx(handle, 7, buffer, (uint)buffer.Length))
            {
                int error = Marshal.GetLastWin32Error();
                if (error is 1 or 50 or 87)
                {
                    if (!GetFileInformationByHandleEx(handle, 1, buffer, 24))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    return BitConverter.ToInt64(buffer, 0);
                }
                if (error is not (122 or 234) || buffer.Length >= 1048576)
                {
                    throw new Win32Exception(error);
                }

                byte[] larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = larger;
            }
            long total = 0;
            int offset = 0;
            while (true)
            {
                if (offset + 24 > buffer.Length)
                {
                    throw new InvalidDataException("文件流记录无效。");
                }

                int next = BitConverter.ToInt32(buffer, offset);
                long allocated = BitConverter.ToInt64(buffer, offset + 16);
                if (allocated < 0)
                {
                    throw new InvalidDataException("文件占用无效。");
                }

                if (allocated is > 0 and < 4096)
                {
                    int nameLength = BitConverter.ToInt32(buffer, offset + 4);
                    if (nameLength < 0 || offset + 24 + nameLength > buffer.Length)
                    {
                        throw new InvalidDataException("数据流名称无效。");
                    }

                    string name = Encoding.Unicode.GetString(buffer, offset + 24, nameLength);
                    SafeFileHandle? alternate = null;
                    try
                    {
                        var stream = handle;
                        if (name != "::$DATA")
                        {
                            alternate = CreateFile(path + name, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                            stream = alternate;
                        }
                        if (!stream.IsInvalid && !DeviceIoControl(stream, 0x90073, new byte[8], 8, new byte[32], 32, out _, IntPtr.Zero)
                            && Marshal.GetLastWin32Error() == 38)
                        {
                            allocated = 0;
                        }
                    }
                    finally { alternate?.Dispose(); }
                }
                total = checked(total + allocated);
                if (next == 0)
                {
                    return total;
                }

                if (next < 24)
                {
                    throw new InvalidDataException("文件流偏移无效。");
                }

                offset = checked(offset + next);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static FileEntry Inspect(string path)
    {
        path = Paths.Normalize(path);
        using var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        bool directory = (info.Attributes & 0x10) != 0;
        long size = directory ? 0 : ((long)info.SizeHigh << 32) | info.SizeLow;
        long allocated = 0;
        string? issue = null;
        if (!directory)
        {
            try
            {
                allocated = Allocation(handle, path);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException) { issue = "无法读取实际占用 — " + ex.Message; }
        }
        var identity = $"{info.Volume:x8}:{((ulong)info.IndexHigh << 32 | info.IndexLow):x16}";
        return new(path, Path.GetDirectoryName(path) ?? "", Path.GetFileName(path) is { Length: > 0 } name ? name : path,
            directory, size, allocated, Time(info.Write), Time(info.Creation), identity, info.Attributes, info.Links, issue);
    }

    private static long Time(System.Runtime.InteropServices.ComTypes.FILETIME time) =>
        DateTimeOffset.FromFileTime(((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime).ToUnixTimeMilliseconds();

    public static bool HasLinkAncestor(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            using var handle = CreateFile(current, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            var tag = new byte[8];
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, tag, 8))
            {
                return true;
            }

            uint value = BitConverter.ToUInt32(tag, 4);
            // Cloud tags describe placeholders without redirecting the path to another location.
            if ((value & 0xffff0fff) != 0x9000001a)
            {
                return true;
            }
        }
        return false;
    }

    public static IEnumerable<FileEntry> Enumerate(string root, Action<string> issue, CancellationToken cancellation)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var path))
        {
            cancellation.ThrowIfCancellationRequested();
            FileEntry entry;
            try
            {
                entry = Inspect(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                issue($"{path} — {ex.Message}");
                continue;
            }
            yield return entry;
            if (entry.Issue != null)
            {
                issue($"{path} — {entry.Issue}");
            }

            if (!entry.Directory || entry.Reparse)
            {
                continue;
            }

            IEnumerator<string>? children = null;
            try
            {
                children = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issue($"{path} — {ex.Message}"); }
            if (children == null)
            {
                continue;
            }

            using (children)
            {
                while (true)
                {
                    string child;
                    try
                    {
                        if (!children.MoveNext())
                        {
                            break;
                        }

                        child = children.Current;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        issue($"{path} — {ex.Message}");
                        break;
                    }
                    pending.Push(child);
                }
            }
        }
    }
}
