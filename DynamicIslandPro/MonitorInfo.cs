using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace DynamicIslandPro
{
    /// <summary>
    /// 单个显示器的几何信息。
    /// Bounds/WorkArea 已转为 WPF DIP（与 Window.Left/Top 同坐标系，定位用）。
    /// PhysicalBounds 是物理像素分辨率（仅用于显示给用户看，如 "2560×1440"）。
    /// </summary>
    public sealed class MonitorInfo
    {
        public int Index { get; init; }
        public Rect Bounds { get; init; }          // DIP，定位用
        public Rect WorkArea { get; init; }        // DIP，工作区
        public Rect PhysicalBounds { get; init; }  // 物理像素，只给 Display 字符串读
        public string DeviceName { get; init; } = "";
        public bool IsPrimary { get; init; }

        /// <summary>ComboBox 显示用："主显示器 (1920×1080)" / "显示器 2 (2560×1440)"。用物理像素读起来直观。</summary>
        public string Display =>
            $"{(IsPrimary ? "主显示器" : $"显示器 {Index + 1}")} ({PhysicalBounds.Width:0}×{PhysicalBounds.Height:0})";
    }

    /// <summary>
    /// 枚举所有显示器（Win32 EnumDisplayMonitors，避免引入 System.Windows.Forms 依赖）。
    /// 物理像素几何按 system DPI 统一转换为 DIP，与 WPF Window.Left/Top 同坐标系直接相加可用。
    /// </summary>
    public static class MonitorEnumerator
    {
        public static IReadOnlyList<MonitorInfo> EnumerateMonitors()
        {
            // 先收集物理像素几何，再统一按 system DPI 转 DIP（与 WPF Window.Left/Top 同坐标系）
            var raw = new List<MonitorInfo>();
            int index = 0;

            bool Callback(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    raw.Add(new MonitorInfo
                    {
                        Index = index++,
                        Bounds = ToRect(info.rcMonitor),
                        WorkArea = ToRect(info.rcWork),
                        DeviceName = info.szDevice ?? "",
                        IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    });
                }
                return true;
            }

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

            // 推断 system DPI scale：主屏物理宽 / DIP 宽（SystemParameters.PrimaryScreenWidth 是 DIP）。
            // WPF 默认 system-DPI-aware，所有副屏按主屏 DPI 渲染，统一 scale 够用。
            double dpiScale = 1.0;
            var primary = raw.Find(m => m.IsPrimary);
            if (primary != null && primary.Bounds.Width > 0)
            {
                double dipW = SystemParameters.PrimaryScreenWidth;
                if (dipW > 0)
                    dpiScale = primary.Bounds.Width / dipW;
            }
            if (dpiScale <= 0) dpiScale = 1.0;

            // 主屏排首位：用户预期"第一个就是主屏"，索引语义更稳
            raw.Sort((a, b) =>
            {
                if (a.IsPrimary && !b.IsPrimary) return -1;
                if (!a.IsPrimary && b.IsPrimary) return 1;
                return a.Index.CompareTo(b.Index);
            });

            var list = new List<MonitorInfo>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                list.Add(new MonitorInfo
                {
                    Index = i,
                    Bounds = ScaleRect(raw[i].Bounds, 1.0 / dpiScale),
                    WorkArea = ScaleRect(raw[i].WorkArea, 1.0 / dpiScale),
                    PhysicalBounds = raw[i].Bounds,
                    DeviceName = raw[i].DeviceName,
                    IsPrimary = raw[i].IsPrimary,
                });
            }
            return list;
        }

        private static Rect ScaleRect(Rect r, double s) =>
            new(r.X * s, r.Y * s, r.Width * s, r.Height * s);

        private static Rect ToRect(RECT r) =>
            new(r.left, r.top, Math.Max(0, r.right - r.left), Math.Max(0, r.bottom - r.top));

        // ─── Win32 ─────────────────────────────────────────────────
        private const int MONITORINFOF_PRIMARY = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    }
}
