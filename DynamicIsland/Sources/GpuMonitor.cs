using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using System.Threading;

namespace DynamicIsland.Sources
{
    /// <summary>
    /// GPU 监控：使用率 + 显存占用率。全程走性能计数器 + WMI，零 COM interop，不会崩。
    ///
    /// 线程模型（2026-07-07 改）：首次初始化（计数器枚举 + DXGI + WMI）和后续周期采样
    /// 全部在内部后台线程跑。原因：<see cref="PerformanceCounter.NextValue"/> 对差值型计数器
    /// 内部缓存上次采样值，跨线程交替调用同一实例会破坏差值连续性 → 读数跳变。所以采样
    /// 必须固定在创建计数器的同一线程上。外部 <see cref="GpuUsage"/>/<see cref="GpuVramUsage"/>
    /// getter 只读后台维护的最新缓存值，不阻塞 UI 线程。
    ///
    /// - GPU 使用率：性能计数器 <c>GPU Engine(*)\Utilization Percentage</c>，
    ///   枚举所有 3D/计算/视频解码/编码引擎实例累加，封顶 100。需要驱动暴露该计数器，
    ///   部分集显/旧驱动读不到 → <see cref="GpuUsage"/> 返回 NaN，UI 显示"--"。
    /// - 显存占用率：性能计数器 <c>GPU Adapter Memory(*)\Dedicated Usage</c> +
    ///   <c>Shared Usage</c> 求和得已用字节；显存总量用 DXGI（无 4GB 回绕，与任务管理器一致），
    ///   失败回退 WMI <c>Win32_VideoController.AdapterRAM</c>（>4GB 会回绕偏低，一次性查询缓存）。
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class GpuMonitor
    {
        // ─── 后台采样线程 ──────────────────────────────────────────
        // _running 是停止标志；_stop 用于中断采样间隔的睡眠，让 Stop() 立即唤醒退出。
        private Thread? _thread;
        private volatile bool _running;
        private readonly ManualResetEventSlim _stop = new(false);

        // 计数器实例：仅后台线程访问（Init + 周期采样都在同一线程），无需同步。
        private PerformanceCounter[]? _engineCounters;   // 使用率
        private PerformanceCounter[]? _memDedicated;     // 独立显存已用(字节)
        private PerformanceCounter[]? _memShared;        // 共享显存已用(字节)
        private bool _countersInited;
        private bool _countersFailed;

        private long _vramTotalBytes = -1;   // -1 = 还没查
        private bool _vramQueried;

        // ─── 最新缓存值（后台线程写 / UI 线程读）──────────────────────
        // double 不允许 volatile 关键字，用 Volatile.Read/Write 保证可见性（x64 上本就原子）。
        private double _gpu = double.NaN;     // 0~100，NaN 表示读不到
        private double _gpuVram = double.NaN; // 0~100，NaN 表示读不到

        private const int SampleIntervalMs = 1000;

        /// <summary>GPU 使用率 0~100；读不到返回 NaN。读后台最新缓存，不阻塞。</summary>
        public double GpuUsage => Volatile.Read(ref _gpu);

        /// <summary>显存占用率 0~100；读不到返回 NaN。读后台最新缓存，不阻塞。</summary>
        public double GpuVramUsage => Volatile.Read(ref _gpuVram);

        /// <summary>启动后台采样线程。幂等。</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _stop.Reset();
            _thread = new Thread(SampleLoop) { IsBackground = true, Name = "GpuMonitor" };
            _thread.Start();
        }

        /// <summary>停止后台采样线程，最多等 2s 退出。</summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _stop.Set();                 // 唤醒睡眠中的采样循环
            _thread?.Join(2000);
            _stop.Reset();
            _thread = null;
            // 重置缓存为 NaN，下次 Start 重新暖
            Volatile.Write(ref _gpu, double.NaN);
            Volatile.Write(ref _gpuVram, double.NaN);
        }

        private void SampleLoop()
        {
            // 首次：慢初始化（计数器枚举 + DXGI + WMI）放后台，不卡 UI；暖完立即采一次。
            InitCounters();
            SampleOnce();

            while (_running)
            {
                // 可中断睡眠：Stop() 会 Set _stop 立即唤醒返回 true → 退出。
                if (_stop.Wait(SampleIntervalMs)) break;
                if (!_running) break;
                SampleOnce();
            }
        }

        /// <summary>采一次：读所有计数器 NextValue，更新缓存。仅后台线程调用。</summary>
        private void SampleOnce()
        {
            if (_countersFailed) return;   // 保持 NaN
            if (!_countersInited) InitCounters();
            if (_countersFailed) return;

            try
            {
                // 使用率：枚举引擎实例累加，封顶 100
                double gpu = double.NaN;
                if (_engineCounters != null && _engineCounters.Length > 0)
                {
                    double sum = 0;
                    foreach (var c in _engineCounters) sum += c.NextValue();
                    gpu = Math.Clamp(sum, 0, 100);
                }

                // 显存占用率：已用字节 / 总量
                double vram = double.NaN;
                var total = GetVramTotalBytes();
                if (total > 0)
                {
                    long used = 0;
                    if (_memDedicated != null)
                        foreach (var c in _memDedicated) used += (long)c.NextValue();
                    if (_memShared != null)
                        foreach (var c in _memShared) used += (long)c.NextValue();
                    if (used > 0)
                        vram = Math.Clamp((double)used / total * 100.0, 0, 100);
                }

                Volatile.Write(ref _gpu, gpu);
                Volatile.Write(ref _gpuVram, vram);
            }
            catch
            {
                Volatile.Write(ref _gpu, double.NaN);
                Volatile.Write(ref _gpuVram, double.NaN);
            }
        }

        // ─── 性能计数器懒初始化（仅后台线程）──────────────────────
        private void InitCounters()
        {
            _countersInited = true;
            try
            {
                // 使用率：枚举 GPU Engine 实例，过滤出计算类引擎
                if (PerformanceCounterCategory.Exists("GPU Engine"))
                {
                    var cat = new PerformanceCounterCategory("GPU Engine");
                    var instances = cat.GetInstanceNames();
                    var wanted = instances
                        .Where(n => n.Contains("engtype_3D") || n.Contains("engtype_Compute")
                                    || n.Contains("engtype_VideoDecode") || n.Contains("engtype_VideoEncode"))
                        .ToArray();
                    if (wanted.Length > 0)
                    {
                        _engineCounters = wanted
                            .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true))
                            .ToArray();
                        foreach (var c in _engineCounters) _ = c.NextValue(); // 暖一次
                    }
                }

                // 显存已用：GPU Adapter Memory 计数器
                if (PerformanceCounterCategory.Exists("GPU Adapter Memory"))
                {
                    var cat = new PerformanceCounterCategory("GPU Adapter Memory");
                    var instances = cat.GetInstanceNames();
                    if (instances.Length > 0)
                    {
                        _memDedicated = instances
                            .Select(n => new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", n, true))
                            .ToArray();
                        _memShared = instances
                            .Select(n => new PerformanceCounter("GPU Adapter Memory", "Shared Usage", n, true))
                            .ToArray();
                        foreach (var c in _memDedicated) _ = c.NextValue();
                        foreach (var c in _memShared) _ = c.NextValue();
                    }
                }

                if (_engineCounters == null && _memDedicated == null)
                    _countersFailed = true;
            }
            catch
            {
                _countersFailed = true;
            }
        }

        /// <summary>显存总量(字节)。优先 DXGI（无 4GB 回绕，与任务管理器一致）；失败回退 WMI AdapterRAM（>4GB 会回绕偏低）。</summary>
        private long GetVramTotalBytes()
        {
            if (_vramQueried) return _vramTotalBytes;
            _vramQueried = true;
            try
            {
                // DXGI 路线：DedicatedVideoMemory 是 ulong，无回绕
                long dxgi = VramInfo.GetTotalBytes();
                if (dxgi > 0)
                {
                    _vramTotalBytes = dxgi;
                    return _vramTotalBytes;
                }
            }
            catch { /* 走 WMI 回退 */ }

            try
            {
                // WMI 回退：加 3s 超时。精简系统/损坏 WMI 上 searcher.Get() 可能阻塞几十秒，
                // 不加超时会拖死后台线程（虽不再卡 UI，但仍占资源且永远采不到值）。
                using var searcher = new ManagementObjectSearcher(
                    "SELECT AdapterRAM FROM Win32_VideoController");
                searcher.Options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(3) };
                long best = -1;
                foreach (var mo in searcher.Get().Cast<ManagementObject>())
                {
                    var ram = mo["AdapterRAM"] as uint? ?? 0;
                    // AdapterRAM 是 uint，超过 4GB 会回绕；取最大的一块当主显存（仍偏低，但兜底）
                    if (ram > best) best = ram;
                }
                _vramTotalBytes = best;
            }
            catch
            {
                _vramTotalBytes = -1;
            }
            return _vramTotalBytes;
        }
    }
}
