using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace DynamicIsland
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
        public int RefreshRate { get; init; }      // Hz，如 60/120/144

        /// <summary>ComboBox 显示用："主显示器 (1920×1080)" / "显示器 2 (2560×1440)"。用物理像素读起来直观。</summary>
        public string Display =>
            $"{(IsPrimary ? "主显示器" : $"显示器 {Index + 1}")} ({PhysicalBounds.Width:0}×{PhysicalBounds.Height:0})";
    }

    /// <summary>
    /// 枚举所有显示器（Win32 EnumDisplayMonitors，避免引入 System.Windows.Forms 依赖）。
    /// 物理像素几何按**每屏自身 DPI**（GetDpiForMonitor）转换为 DIP，与 WPF Window.Left/Top 同坐标系直接相加可用。
    /// net10 WPF 默认 PerMonitorV2，每块屏 DPI 可能不同，不能拿主屏一个 scale 套全部。
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
                    int refreshRate = 0;
                    var dm = default(DEVMODE);
                    dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
                    dm.dmDriverExtra = 0;
                    if (EnumDisplaySettings(info.szDevice, ENUM_CURRENT_SETTINGS, ref dm))
                        refreshRate = (int)dm.dmDisplayFrequency;

                    // 该屏自身 DPI scale（net10 WPF 默认 PerMonitorV2，每块屏 DPI 可能不同，
                    // 不能再拿主屏一个 scale 套全部）。物理 rcMonitor/rcWork 按 1/scale 转 DIP，
                    // 与 WPF Window.Left/Top 同坐标系直接相加可用。
                    double scale = GetMonitorDpiScale(hMonitor);
                    raw.Add(new MonitorInfo
                    {
                        Index = index++,
                        Bounds = ScaleRect(ToRect(info.rcMonitor), 1.0 / scale),
                        WorkArea = ScaleRect(ToRect(info.rcWork), 1.0 / scale),
                        PhysicalBounds = ToRect(info.rcMonitor),
                        DeviceName = info.szDevice ?? "",
                        IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                        RefreshRate = refreshRate > 0 ? refreshRate : 60,
                    });
                }
                return true;
            }

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

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
                    Bounds = raw[i].Bounds,               // 已是该屏 DIP（回调里按自身 DPI 转过）
                    WorkArea = raw[i].WorkArea,           // 已是该屏 DIP
                    PhysicalBounds = raw[i].PhysicalBounds,
                    DeviceName = raw[i].DeviceName,
                    IsPrimary = raw[i].IsPrimary,
                    RefreshRate = raw[i].RefreshRate,
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

        private const int ENUM_CURRENT_SETTINGS = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
        }

        // ── 每屏 DPI（PerMonitorV2 下每块屏可能不同，不能拿主屏一个值套全部）──
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        private const int MDT_EFFECTIVE_DPI = 0;

        /// <summary>取该显示器自身 DPI scale（dpiX/96.0）。失败回退 1.0（=96 DPI）。</summary>
        private static double GetMonitorDpiScale(IntPtr hMonitor)
        {
            try
            {
                if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                    return dpiX / 96.0;
            }
            catch { }
            return 1.0;
        }
    }
}
