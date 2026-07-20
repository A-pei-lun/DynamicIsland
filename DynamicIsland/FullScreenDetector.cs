using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace DynamicIsland
{
    /// <summary>
    /// 全屏应用检测：前台窗口变化事件 + 定时轮询兜底，判断是否全屏覆盖灵动岛所在目标显示器。
    ///
    /// 核心规则：只有当前**前台窗口**是全屏且覆盖岛所在屏时才算全屏。
    /// Win 键切桌面 / Alt+Tab 切走 → 前台变了 → 立即取消全屏态 → 胶囊弹出。
    /// 切回游戏 → 前台变回游戏全屏窗口 → 立即恢复全屏态 → 胶囊隐藏。
    /// </summary>
    public sealed class FullScreenDetector : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly Func<Rect> _getTargetPhysicalBounds;
        private IntPtr _selfHwnd;
        private bool _isFullScreen;
        private bool _disposed;
        private IntPtr _winEventHook;

        /// <summary>当前是否检测到全屏应用覆盖目标屏。变化时触发 IsFullScreenChanged。</summary>
        public bool IsFullScreen => _isFullScreen;

        /// <summary>全屏状态切换时触发。参数为新的 IsFullScreen 值。</summary>
        public event EventHandler<bool>? IsFullScreenChanged;

        public FullScreenDetector(Func<Rect> getTargetPhysicalBounds)
        {
            _getTargetPhysicalBounds = getTargetPhysicalBounds;
            _foregroundHook = OnForegroundChanged;
            // 轮询兜底：1 秒一次（前台事件即时触发，轮询防漏）
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Poll();
        }

        private void OnForegroundChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // WinEvent 回调在独立线程，需要调到 UI 线程
            _timer.Dispatcher.BeginInvoke(() => Poll());
        }

        /// <summary>启动轮询 + 前台窗口事件钩子。</summary>
        public void Start(IntPtr selfHwnd)
        {
            _selfHwnd = selfHwnd;
            _timer.Start();

            // WinEvent 钩子：前台窗口切换时立即检查，无需等轮询
            _winEventHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundHook, 0, 0,
                WINEVENT_OUTOFCONTEXT);

            // 启动时立即检查一次
            Poll();
        }

        public void Stop()
        {
            _timer.Stop();
            if (_winEventHook != IntPtr.Zero) { UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
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

                // 桌面 / 任务栏 / 开始菜单 → 不算全屏
                if (IsShellWindow(fg)) return false;

                // 前台窗口最小化了（Win+D / 显示桌面）→ 不算全屏
                if (IsIconic(fg)) return false;

                if (!GetWindowRect(fg, out var wr)) return false;
                // 防御：宽高异常也排除
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

        // ── WinEvent 钩子：前台窗口切换即时回调 ──
        private const uint WINEVENT_OUTOFCONTEXT = 0;
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

        private readonly WinEventProc _foregroundHook;

        delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventProc lpfnWinEventProc, int idProcess, int idThread, uint dwFlags);

        [DllImport("user32.dll")]
        static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        // ── Win32 ─────────────────────────────────────────────────
        private const int MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int maxCount);

        private static bool IsShellWindow(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(64);
            GetClassName(hwnd, sb, 64);
            var cls = sb.ToString();
            return cls is "Progman" or "WorkerW" or "Shell_TrayWnd";
        }

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
            if (_winEventHook != IntPtr.Zero) { UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
        }
    }
}
