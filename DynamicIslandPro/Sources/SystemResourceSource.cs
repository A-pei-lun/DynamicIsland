using System;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Threading;
using DynamicIslandPro.Island;

namespace DynamicIslandPro.Sources
{
    /// <summary>
    /// 系统资源数据源：CPU / 内存 / 网速。
    ///
    /// 收起态走"阈值触发"（方案 B）：CPU 或内存吃紧时才点亮，
    /// 平时让 <see cref="ClockSource"/> 兜底，不打扰用户。
    /// 为避免在阈值附近反复闪烁，进入和退出用不同的门槛（滞回）。
    ///
    /// 同时实现 <see cref="INotifyPropertyChanged"/>：采样永远在跑，
    /// 仪表盘无论阈值与否都能实时绑定 CPU/RAM/网速。
    /// </summary>
    public sealed class SystemResourceSource : IIslandSource, INotifyPropertyChanged
    {
        // ─── 阈值（滞回）────────────────────────────────────────────
        // 任一指标超过"进入"门槛即点亮；两个指标都跌到"退出"门槛以下才熄灭。
        // 进入门槛读 DisplaySettings（设置面板可改），退出 = 进入 - 10 保留滞回防抖。
        private static double CpuActivate => DisplaySettings.Instance.CpuActivate;
        private static double CpuDeactivate => DisplaySettings.Instance.CpuActivate - 10;
        private static double RamActivate => DisplaySettings.Instance.RamActivate;
        private static double RamDeactivate => DisplaySettings.Instance.RamActivate - 10;

        // ─── 采样 ──────────────────────────────────────────────────
        private readonly DispatcherTimer _timer;
        private readonly Dispatcher _dispatcher;

        private bool _hasCpuBaseline;        // 第一次采样只记基线，算不出差值
        private FILETIME _lastIdle, _lastKernel, _lastUser;

        private double _cpu;                  // 0~100
        private double _ram;                  // 0~100（dwMemoryLoad）
        private double _downRate;             // bytes/s
        private double _upRate;               // bytes/s
        private long _prevDown, _prevUp;      // 上次采样时所有上联接口的累计收/发字节
        private DateTime _prevNetAt;
        private bool _hasNetBaseline;

        private double _gpu;                  // 0~100，NaN 表示读不到
        private double _gpuVram;              // 0~100，NaN 表示读不到
        private readonly GpuMonitor _gpuMonitor = new();

        private bool _active;                 // 当前是否点亮（滞回后的状态）

        public SystemResourceSource()
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, _) => Sample();
        }

        // ─── IIslandSource ────────────────────────────────────────
        public string Id => "system";
        public int Priority => 50;            // 普通信息档：盖过时钟，但让位于音乐
        public bool IsActive => _active;

        public string CollapsedText
        {
            get
            {
                if (!_active) return string.Empty;
                // 收起态很窄（200×40），只放最关键的两项
                return $"🖥 {Math.Round(_cpu)}% · 💾 {Math.Round(_ram)}%";
            }
        }

        // 收起态的文字回退。展开态改由 IslandDashboard 组合展示，这里基本用不到。
        public string? ExpandedText =>
            $"🖥 CPU    {Math.Round(_cpu),3}%\n" +
            $"💾 RAM    {Math.Round(_ram),3}%\n" +
            $"📡 NET   ↓{FormatRate(_downRate)}  ↑{FormatRate(_upRate)}";

        public FrameworkElement? ExpandedView => null;

        public event EventHandler? Changed;

        // ─── 仪表盘绑定属性 ────────────────────────────────────────
        public double Cpu => _cpu;
        public double Ram => _ram;
        public double Gpu => _gpu;
        public double GpuVram => _gpuVram;
        public string DownRateText => FormatRate(_downRate);
        public string UpRateText => FormatRate(_upRate);

        /// <summary>GPU 使用率文本，读不到显示"--"。</summary>
        public string GpuText => double.IsNaN(_gpu) ? "--" : $"{Math.Round(_gpu):0}%";
        /// <summary>显存占用率文本，读不到显示"--"。</summary>
        public string GpuVramText => double.IsNaN(_gpuVram) ? "--" : $"{Math.Round(_gpuVram):0}%";

        // ─── 启动/停止 ────────────────────────────────────────────
        public void Start()
        {
            // 立刻采一次建立基线，再按间隔刷新
            Sample();
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            _active = false;
        }

        public void Dispose() => Stop();

        // ─── 采样 ──────────────────────────────────────────────────
        private void Sample()
        {
            SampleCpu();
            SampleRam();
            SampleNet();
            SampleGpu();

            // 仪表盘绑定：永远推最新值（不受阈值影响）
            OnPropertyChanged(nameof(Cpu));
            OnPropertyChanged(nameof(Ram));
            OnPropertyChanged(nameof(Gpu));
            OnPropertyChanged(nameof(GpuVram));
            OnPropertyChanged(nameof(GpuText));
            OnPropertyChanged(nameof(GpuVramText));
            OnPropertyChanged(nameof(DownRateText));
            OnPropertyChanged(nameof(UpRateText));

            // 收起态仲裁：滞回决定要不要点亮
            var wasActive = _active;
            if (!_active)
                _active = _cpu >= CpuActivate || _ram >= RamActivate;
            else
                _active = _cpu >= CpuDeactivate || _ram >= RamDeactivate;

            // 点亮/熄灭切换，或已点亮时数值变了，都要通知 host 重刷收起态
            if (_active || wasActive)
                Changed?.Invoke(this, EventArgs.Empty);
        }

        private void SampleCpu()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
                return;

            if (!_hasCpuBaseline)
            {
                _lastIdle = idle;
                _lastKernel = kernel;
                _lastUser = user;
                _hasCpuBaseline = true;
                return;
            }

            // GetSystemTimes 返回的是累计的 100ns 单位时间。kernel 已含 idle。
            // usage = 1 - idleDelta / (kernelDelta + userDelta)
            var idleDelta = ToUlong(idle) - ToUlong(_lastIdle);
            var kernelDelta = ToUlong(kernel) - ToUlong(_lastKernel);
            var userDelta = ToUlong(user) - ToUlong(_lastUser);
            var total = kernelDelta + userDelta;

            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;

            if (total > 0)
            {
                var busy = total - idleDelta;
                _cpu = Math.Clamp((double)busy / total * 100.0, 0, 100);
            }
        }

        private void SampleRam()
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
                _ram = mem.dwMemoryLoad;
        }

        private void SampleNet()
        {
            // 取所有"上联"接口（排除 loopback、隧道）的累计收/发字节求和，
            // 与上次采样做差除以时间，得到上下行速率。
            long down = 0, up = 0;
            try
            {
                foreach (var i in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (i.OperationalStatus != OperationalStatus.Up
                        || i.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || i.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var s = i.GetIPv4Statistics();
                    down += s.BytesReceived;
                    up += s.BytesSent;
                }
            }
            catch
            {
                // 某些虚拟接口读统计会抛，忽略即可
            }

            var now = DateTime.UtcNow;
            if (!_hasNetBaseline)
            {
                _prevDown = down;
                _prevUp = up;
                _prevNetAt = now;
                _hasNetBaseline = true;
                return;
            }

            var dt = (now - _prevNetAt).TotalSeconds;
            if (dt > 0)
            {
                _downRate = Rate(_prevDown, down, dt);
                _upRate = Rate(_prevUp, up, dt);
            }

            _prevDown = down;
            _prevUp = up;
            _prevNetAt = now;
        }

        private void SampleGpu()
        {
            // GPU 监控走性能计数器，读不到返回 NaN（UI 显示"--"）
            try
            {
                _gpu = _gpuMonitor.GpuUsage;
                _gpuVram = _gpuMonitor.GpuVramUsage;
            }
            catch
            {
                _gpu = double.NaN;
                _gpuVram = double.NaN;
            }
        }

        private static double Rate(long prev, long cur, double seconds)
        {
            if (seconds <= 0) return 0;
            var delta = cur - prev;
            if (delta < 0) return 0;   // 接口重置/计数器回绕，丢弃
            return delta / seconds;
        }

        // ─── 格式化 ────────────────────────────────────────────────
        private static string FormatRate(double bytesPerSec)
        {
            if (bytesPerSec < 1024) return $"{bytesPerSec:0} B/s";
            if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:0.0} KB/s";
            return $"{bytesPerSec / (1024 * 1024):0.0} MB/s";
        }

        // ─── INotifyPropertyChanged ────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ─── P/Invoke ──────────────────────────────────────────────
        private static ulong ToUlong(FILETIME f)
            => ((ulong)(uint)f.dwHighDateTime << 32) | (uint)f.dwLowDateTime;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(
            out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }
    }
}
