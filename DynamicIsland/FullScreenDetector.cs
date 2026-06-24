using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace DynamicIsland
{
    /// <summary>
    /// 全屏应用检测：定时轮询前台窗口，判断是否全屏覆盖灵动岛所在的目标显示器。
    /// 全屏时让 MainWindow 隐藏（不挡全屏应用视野），但有 alert 时仍短暂弹出展示。
    ///
    /// 判定逻辑（物理像素坐标系）：
    /// 1. 取前台窗口 hwnd → GetWindowRect 得物理矩形
    /// 2. MonitorFromWindow 得该窗口所在屏 HMONITOR → GetMonitorInfo 得屏物理矩形
    /// 3. 窗口矩形 ≥ 屏物理矩形 且 hwnd != 自己 → 视为全屏
    ///    （最大化窗口、无边框全屏游戏、F11 全屏播放器都满足）
    ///
    /// 轮询而非事件：Win32 无"前台窗口全屏变化"的现成事件，Shell 钩子太重。
    /// 2 秒一次足够及时（全屏进出瞬间到 2 秒内响应），CPU 可忽略。
    /// </summary>
    public sealed class FullScreenDetector : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly Func<Rect> _getTargetPhysicalBounds;
        // 灵动岛自身的 hwnd，判定时排除（自己窗口虽小但可能误判）
        private IntPtr _selfHwnd;
        private bool _isFullScreen;
        private bool _disposed;

        /// <summary>当前是否检测到全屏应用覆盖目标屏。变化时触发 IsFullScreenChanged。</summary>
        public bool IsFullScreen => _isFullScreen;

        /// <summary>全屏状态切换时触发。参数为新的 IsFullScreen 值。</summary>
        public event EventHandler<bool>? IsFullScreenChanged;

        public FullScreenDetector(Func<Rect> getTargetPhysicalBounds)
        {
            _getTargetPhysicalBounds = getTargetPhysicalBounds;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, _) => Poll();
        }

        /// <summary>启动轮询。传灵动岛自身 hwnd 用于排除自检。</summary>
        public void Start(IntPtr selfHwnd)
        {
            _selfHwnd = selfHwnd;
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
        }

        private void Poll()
        {
            if (_disposed) return;
            bool now = ComputeIsFullScreen();
            if (now != _isFullScreen)
            {
                _isFullScreen = now;
                IsFullScreenChanged?.Invoke(this, now);
            }
        }

        private bool ComputeIsFullScreen()
        {
            try
            {
                var fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == _selfHwnd) return false;

                if (!GetWindowRect(fg, out var wr)) return false;
                // 窗口最小化时 wr 是负坐标小矩形，直接排除
                if (wr.Right - wr.Left <= 0 || wr.Bottom - wr.Top <= 0) return false;

                // 找前台窗口所在的屏
                var hMon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
                if (!GetMonitorInfoSafe(hMon, out var mi)) return false;

                // 屏的物理矩形（rcMonitor 含任务栏区，全屏判断用 rcMonitor 更稳：最大化窗口覆盖 rcMonitor）
                var monLeft = mi.rcMonitor.Left;
                var monTop = mi.rcMonitor.Top;
                var monW = mi.rcMonitor.Right - mi.rcMonitor.Left;
                var monH = mi.rcMonitor.Bottom - mi.rcMonitor.Top;

                var target = _getTargetPhysicalBounds();

                // 窗口矩形要覆盖整个屏才算全屏（允许几像素容差，最大化窗口有时差 1~2px）
                const int tol = 4;
                bool covers = wr.Left <= monLeft + tol
                           && wr.Top <= monTop + tol
                           && wr.Right >= mi.rcMonitor.Right - tol
                           && wr.Bottom >= mi.rcMonitor.Bottom - tol;
                if (!covers) return false;

                // 还要确认这块屏就是灵动岛所在的屏——不然用户在副屏全屏游戏，主屏的岛不该藏。
                if (target.Width <= 0) return true; // 目标屏信息缺失，按全屏处理更安全
                bool sameScreen = Math.Abs(target.Left - monLeft) < tol
                                && Math.Abs(target.Top - monTop) < tol
                                && Math.Abs(target.Width - monW) < tol
                                && Math.Abs(target.Height - monH) < tol;
                return sameScreen;
            }
            catch
            {
                // 检测失败一律按"非全屏"处理，避免误藏
                return false;
            }
        }

        // ─── Win32 ─────────────────────────────────────────────────
        private const int MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        // 标准 MONITORINFO（40 字节）：cbSize/rcMonitor/rcWork/dwFlags。
        // 不用 MONITORINFOEX 的 szDevice——detector 只读 rcMonitor，多余字段反而引坑：
        // 裸 string 字段默认 marshal 成 LPWSTR 指针，Marshal.SizeOf 算出的 cbSize 既非 40 也非 104，
        // GetMonitorInfo 收到非法 cbSize 直接返回 false → ComputeIsFullScreen 永远 return false → 全屏永不隐藏。
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private static bool GetMonitorInfoSafe(IntPtr hMonitor, out MONITORINFO mi)
        {
            mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            return GetMonitorInfo(hMonitor, ref mi);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
        }
    }
}
