using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;

namespace DynamicIsland.Sources
{
    /// <summary>
    /// GPU 监控：使用率 + 显存占用率。全程走性能计数器 + WMI，零 COM interop，不会崩。
    ///
    /// - GPU 使用率：性能计数器 <c>\GPU Engine(*)\Utilization Percentage</c>，
    ///   枚举所有 3D/计算/视频解码/编码引擎实例累加，封顶 100。需要驱动暴露该计数器，
    ///   部分集显/旧驱动读不到 → <see cref="GpuUsage"/> 返回 NaN，UI 显示"--"。
    /// - 显存占用率：性能计数器 <c>\GPU Adapter Memory(*)\Dedicated Usage</c> +
    ///   <c>Shared Usage</c> 求和得已用字节；显存总量用 WMI Win32_VideoController.AdapterRAM
    ///   （取首块独显，一次性查询缓存）。
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class GpuMonitor
    {
        private PerformanceCounter[]? _engineCounters;   // 使用率
        private PerformanceCounter[]? _memDedicated;     // 独立显存已用(字节)
        private PerformanceCounter[]? _memShared;        // 共享显存已用(字节)
        private bool _countersInited;
        private bool _countersFailed;

        private long _vramTotalBytes = -1;   // -1 = 还没查
        private bool _vramQueried;

        /// <summary>GPU 使用率 0~100；读不到返回 NaN。</summary>
        public double GpuUsage
        {
            get
            {
                if (_countersFailed) return double.NaN;
                if (!_countersInited) InitCounters();
                if (_engineCounters == null || _engineCounters.Length == 0) return double.NaN;

                try
                {
                    double sum = 0;
                    foreach (var c in _engineCounters) sum += c.NextValue();
                    return Math.Clamp(sum, 0, 100);
                }
                catch { return double.NaN; }
            }
        }

        /// <summary>显存占用率 0~100；读不到返回 NaN。</summary>
        public double GpuVramUsage
        {
            get
            {
                if (_countersFailed) return double.NaN;
                if (!_countersInited) InitCounters();

                var total = GetVramTotalBytes();
                if (total <= 0) return double.NaN;

                try
                {
                    long used = 0;
                    if (_memDedicated != null)
                        foreach (var c in _memDedicated) used += (long)c.NextValue();
                    if (_memShared != null)
                        foreach (var c in _memShared) used += (long)c.NextValue();
                    if (used <= 0) return double.NaN;
                    return Math.Clamp((double)used / total * 100.0, 0, 100);
                }
                catch { return double.NaN; }
            }
        }

        // ─── 性能计数器懒初始化 ────────────────────────────────────
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
                using var searcher = new ManagementObjectSearcher(
                    "SELECT AdapterRAM FROM Win32_VideoController");
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
