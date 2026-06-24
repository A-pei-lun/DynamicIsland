using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DynamicIsland.Island;

namespace DynamicIsland.Alerts
{
    /// <summary>
    /// 下载完成提醒源：监听系统 Downloads 文件夹（或用户自定义路径），
    /// 在新文件创建且写入结束后投递提醒。
    ///
    /// 实现要点：
    /// - 用 FileSystemWatcher 监听目标文件夹的 Created 事件。
    /// - 不监听 Changed 事件（浏览器写碎片文件过于频繁）。
    /// - 新文件创建后启动一个"稳定检测"：每 500ms 查一次文件大小，
    ///   连续两次采样大小相同（且 >0）则认为写入结束。
    /// - 忽略临时/半成品文件（.crdownload / .part / .tmp 等后缀）。
    /// - 目标路径优先取 DisplaySettings.DownloadFolderPath（用户自定义），
    ///   为空则用 KnownFolder 取系统 Downloads。
    /// </summary>
    public sealed class DownloadAlertSource : IDisposable
    {
        private readonly AlertHost _host;
        private FileSystemWatcher? _watcher;
        private bool _started;
        private bool _disposed;

        // 正在监控是否写入结束的文件：path → (lastSize, checkTimer)
        private readonly ConcurrentDictionary<string, PendingFile> _pending = new();

        // 半成品后缀：写入中不会报警
        private static readonly string[] IgnoredExtensions =
            { ".crdownload", ".part", ".tmp", ".temp", ".download", ".opdownload" };

        // 稳定检测参数
        private static readonly TimeSpan StabilityCheckInterval = TimeSpan.FromMilliseconds(500);
        private const int StableSampleCount = 2; // 连续 2 次采样大小相同 → 稳定

        public DownloadAlertSource(AlertHost host) => _host = host;

        public void Start()
        {
            if (_started) return;
            _started = true;

            string folder = ResolveWatchFolder();
            if (!Directory.Exists(folder))
            {
                // 文件夹不存在（极端情况），静默——用户手动配置后会重新 Start
                System.Diagnostics.Debug.WriteLine($"[DownloadAlert] 目标文件夹不存在: {folder}");
                return;
            }

            _watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnFileCreated;
            _watcher.Error += OnWatcherError;

            System.Diagnostics.Debug.WriteLine($"[DownloadAlert] 开始监控: {folder}");
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;

            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFileCreated;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
                _watcher = null;
            }

            // 清理所有 pending 检测
            foreach (var kv in _pending.ToArray())
            {
                kv.Value.Timer.Dispose();
                _pending.TryRemove(kv.Key, out _);
            }
        }

        /// <summary>
        /// 重新配置监控路径（设置里改了下载目录后调用）。
        /// </summary>
        public void Restart()
        {
            Stop();
            Start();
        }

        private void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            if (!DisplaySettings.Instance.EnableDownloadAlert) return;

            string ext = Path.GetExtension(e.Name)?.ToLowerInvariant() ?? "";
            if (IgnoredExtensions.Contains(ext)) return;

            // 跳过隐藏文件和系统文件
            try
            {
                if ((File.GetAttributes(e.FullPath) & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    return;
            }
            catch
            {
                return; // 文件可能已被删/移走
            }

            // 开始稳定检测
            StartStabilityCheck(e.FullPath);
        }

        /// <summary>
        /// 对新创建的文件启动写入结束检测：定时采样大小，连续 N 次不变则视为完成。
        /// </summary>
        private void StartStabilityCheck(string path)
        {
            var timer = new System.Threading.Timer(_ => CheckStability(path), null,
                StabilityCheckInterval, StabilityCheckInterval);
            _pending[path] = new PendingFile { Timer = timer };
        }

        private void CheckStability(string path)
        {
            if (!_pending.TryGetValue(path, out var pf)) return;

            long currentSize;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { FinalizeFile(path, null); return; }
                currentSize = fi.Length;
            }
            catch
            {
                FinalizeFile(path, null);
                return;
            }

            if (currentSize == 0)
            {
                // 空文件（刚创建还没写入），重置计数
                pf.SampleCount = 0;
                pf.LastSize = 0;
                return;
            }

            if (currentSize == pf.LastSize)
            {
                pf.SampleCount++;
                if (pf.SampleCount >= StableSampleCount)
                {
                    // 稳定了 → 报警
                    FinalizeFile(path, currentSize);
                }
            }
            else
            {
                // 还在写入中
                pf.SampleCount = 0;
                pf.LastSize = currentSize;
            }
        }

        private void FinalizeFile(string path, long? size)
        {
            if (_pending.TryRemove(path, out var pf))
            {
                pf.Timer.Dispose();
            }

            // 文件大小为 0 或不存在 → 无意义，跳过
            if (size == null || size == 0) return;

            string fileName = Path.GetFileName(path);

            // 格式化文件大小
            string sizeStr = FormatSize(size.Value);

            // 动作按钮「打开」：在资源管理器里定位到刚下载的文件（选中而非直接打开文件，
            // 下载内容未知类型，选中更安全且符合"找文件"意图）。
            _host.Enqueue(new SimpleAlert(
                $"download.{fileName}", "下载完成", $"{fileName} · {sizeStr}", "⬇",
                TimeSpan.FromSeconds(2.5), priority: 40,
                action: new AlertAction("打开", () => RevealInExplorer(path))));
        }

        /// <summary>在资源管理器中定位（选中）指定文件。失败静默——动作按钮失败不应阻塞提醒。</summary>
        private static void RevealInExplorer(string path)
        {
            try
            {
                // /select,"路径" 让 Explorer 打开所在目录并选中该文件
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch
            {
                // 路径含特殊字符等极端情况失败，静默
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            // 缓冲区溢出等错误：重启 watcher
            System.Diagnostics.Debug.WriteLine($"[DownloadAlert] 监控错误: {e.GetException()?.Message}");
            Restart();
        }

        /// <summary>
        /// 解析监控目标路径。优先取用户自定义路径，否则自动检测系统 Downloads 文件夹。
        ///
        /// 检测顺序：
        /// 1. 用户自定义路径（DisplaySettings.DownloadFolderPath）
        /// 2. 系统已知文件夹 Downloads（Win32 SHGetKnownFolderPath）
        /// 3. 回退 %UserProfile%\Downloads
        /// </summary>
        internal static string ResolveWatchFolder()
        {
            string? custom = DisplaySettings.Instance.DownloadFolderPath;
            if (!string.IsNullOrWhiteSpace(custom))
                return custom;

            // 用 Win32 API 取 KnownFolder Downloads（比 Environment.GetFolderPath 更可靠）
            try
            {
                string? known = GetKnownDownloadsPath();
                if (!string.IsNullOrWhiteSpace(known))
                    return known;
            }
            catch
            {
                // 回退
            }

            // 兜底：UserProfile/Downloads
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
                return Path.Combine(userProfile, "Downloads");

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Downloads");
        }

        /// <summary>
        /// 用 Win32 SHGetKnownFolderPath 取系统 Downloads 路径。
        /// </summary>
        private static string? GetKnownDownloadsPath()
        {
            // FOLDERID_Downloads
            Guid folderId = new("{374DE290-123F-4565-9164-39C4925E467B}");
            int hr = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out IntPtr pathPtr);
            if (hr != 0) return null;

            try
            {
                return Marshal.PtrToStringUni(pathPtr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
            uint dwFlags,
            IntPtr hToken,
            out IntPtr pszPath);

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        private sealed class PendingFile
        {
            public System.Threading.Timer Timer = null!;
            public long LastSize;
            public int SampleCount;
        }
    }
}
