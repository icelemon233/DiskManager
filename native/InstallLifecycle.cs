using System.Diagnostics;

namespace DiskManager;

internal static class InstallLifecycle
{
    public static async Task<int> EnsureExit(string directory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(directory))
            {
                throw new ArgumentException("安装目录必须是绝对路径。");
            }

            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (!Directory.Exists(root))
            {
                return 0;
            }

            using var self = Process.GetCurrentProcess();
            List<string> Owned()
            {
                var found = new List<string>();
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        if (process.Id == self.Id)
                        {
                            continue;
                        }

                        try
                        {
                            if (process.SessionId != self.SessionId)
                            {
                                continue;
                            }

                            string? file = process.MainModule?.FileName;
                            if (file != null && Paths.Within(file, root))
                            {
                                found.Add(file);
                            }
                        }
                        catch (System.ComponentModel.Win32Exception) { }
                        catch (InvalidOperationException) { }
                    }
                }
                return found;
            }
            var processes = Owned();
            if (processes.Count == 0)
            {
                return 0;
            }

            string executable = Path.Combine(root, "DiskManager.exe");
            if (processes.Contains(executable, StringComparer.OrdinalIgnoreCase))
            {
                using var request = Process.Start(new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = "--request-exit"
                });
            }
            DateTime deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                if (Owned().Count == 0)
                {
                    return 0;
                }

                await Task.Delay(250);
            }
            Console.Error.WriteLine("DiskManager 尚未完成退出，请等待后台任务结束后重试。");
            return 20;
        }
        catch (Exception ex) { Console.Error.WriteLine("无法确认应用已退出。" + ex.Message); return 21; }
    }
}
