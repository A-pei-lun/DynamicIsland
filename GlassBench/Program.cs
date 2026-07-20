using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace GlassBench;

/// <summary>
/// BitBlt 抓屏全流程基准。复刻 HlslGlassBackend.CaptureBehind 的完整 GDI 序列，
/// 量单帧耗时分布、子步骤拆分、GC 分配与回收，为"是否值得上 D3D9Ex interop"提供数据依据。
///
/// 用法：dotnet run --project GlassBench [-- width height]
/// 默认量两组：720×240（展开态，最重）+ 360×40（收起态，常态）。
/// </summary>
internal static class Program
{
    // ── GDI P/Invoke（复制自 LiquidGlass/HlslGlassBackend.cs 的 CaptureBehind 序列）──
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr src, int x1, int y1, uint rop);
    const uint SRCCOPY = 0x00CC0020;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--probe")
        {
            InteropProbe.Run();
            return 0;
        }
        if (args.Length > 0 && args[0] == "--d3d9")
        {
            D3D9Probe.Run();
            return 0;
        }
        if (args.Length > 0 && args[0] == "--wingc")
        {
            WinGcProbe.Run();
            return 0;
        }
        if (args.Length > 0 && args[0] == "--frame")
        {
            WinGcFrameProbe.Run();
            return 0;
        }
        if (args.Length > 0 && args[0] == "--shared")
        {
            SharedTextureProbe.Run();
            return 0;
        }

        var sizes = new (int w, int h, string label)[]
        {
            (720, 240, "展开态 Expanded"),
            (360, 40,  "收起态 Collapsed"),
        };
        // 命令行覆盖：dotnet run -- 640 480
        if (args.Length >= 2 && int.TryParse(args[0], out int aw) && int.TryParse(args[1], out int ah))
            sizes = new[] { (aw, ah, $"自定义 {aw}×{ah}") };

        const int Warmup = 200;
        const int Frames = 1000;

        Console.WriteLine("GlassBench — BitBlt 抓屏全流程基准");
        Console.WriteLine($"  {Warmup} warmup + {Frames} measure | 复刻 CaptureBehind: GetDC/CreateCompatibleDC/CreateCompatibleBitmap/SelectObject/BitBlt/CreateBitmapSourceFromHBitmap/Freeze/DeleteObject/DeleteDC/ReleaseDC");
        Console.WriteLine($"  环境：{RuntimeInformation.OSDescription.Trim()} / .NET {Environment.Version}");
        Console.WriteLine();

        foreach (var (w, h, label) in sizes)
        {
            BenchOne(w, h, label, Warmup, Frames);
            Console.WriteLine();
        }

        Console.WriteLine("注：以上为单线程同步耗时。HlslGlassBackend 实际在 UI 线程（DispatcherTimer.Tick）跑此序列，每帧阻塞 UI 这么久。");
        Console.WriteLine("    作者原注释声称 \"胶囊尺寸（≤720×240）下 CPU 拷贝 <1ms\" — 对照上方 avg/p99 验证。");
        return 0;
    }

    private static void BenchOne(int w, int h, string label, int warmup, int frames)
    {
        Console.WriteLine($"── {label}  {w}×{h}px ──");

        // warmup：JIT + GDI + WPF Imaging 首次初始化预热，丢弃
        for (int i = 0; i < warmup; i++) CaptureFull(w, h, out _);

        // 先分配统计数组，再取 GC 基线 — 排除数组自身分配
        var totals = new double[frames];
        var tPrep  = new double[frames];
        var tBlt   = new double[frames];
        var tToWpf = new double[frames];
        var tClean = new double[frames];

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long memBefore = GC.GetTotalAllocatedBytes();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);

        var wall = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
            totals[i] = CaptureFullBreakdown(w, h, out tPrep[i], out tBlt[i], out tToWpf[i], out tClean[i]);
        wall.Stop();

        long memAfter = GC.GetTotalAllocatedBytes();
        int g0a = GC.CollectionCount(0), g1a = GC.CollectionCount(1), g2a = GC.CollectionCount(2);

        Stats(totals, "全流程 total");
        Stats(tPrep,  "  A. GDI 准备 (GetDC/CreateCompatible*/SelectObject)");
        Stats(tBlt,   "  B. BitBlt (屏→内存)");
        Stats(tToWpf, "  C. CreateBitmapSourceFromHBitmap + Freeze");
        Stats(tClean, "  D. GDI 清理 (SelectObject/DeleteObject/DeleteDC/ReleaseDC)");

        double allocKb = (memAfter - memBefore) / 1024.0;
        Console.WriteLine($"  分配：{allocKb:N1} KB / {frames} 帧 = {allocKb / frames:N2} KB/帧");
        Console.WriteLine($"  GC 回收：Gen0 +{g0a - g0}  Gen1 +{g1a - g1}  Gen2 +{g2a - g2}");
        Console.WriteLine($"  实际吞吐：{frames / wall.Elapsed.TotalSeconds:N0} fps（单线程连续）");
        double avg = totals.Average();
        Console.WriteLine($"  → 30fps 下每秒占 UI 线程 {avg * 30 / 1000.0:N2} ms ({avg * 30 / 10000.0:F2}% CPU)");
        Console.WriteLine($"  → 144fps 下每秒占 UI 线程 {avg * 144 / 1000.0:N2} ms ({avg * 144 / 10000.0:F2}% CPU)");
    }

    /// <summary>完整序列 + 拆分计时（与 HlslGlassBackend.CaptureBehind 同构）。</summary>
    private static double CaptureFullBreakdown(int w, int h,
        out double prep, out double blt, out double toWpf, out double clean)
    {
        var sTotal = Stopwatch.StartNew();
        var s = Stopwatch.StartNew();

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        s.Stop(); prep = s.Elapsed.TotalMilliseconds;

        s.Restart();
        BitBlt(mem, 0, 0, w, h, screen, 0, 0, SRCCOPY);
        s.Stop(); blt = s.Elapsed.TotalMilliseconds;

        s.Restart();
        BitmapSource? bs = null;
        try
        {
            bs = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bs?.Freeze();
        }
        catch { }
        s.Stop(); toWpf = s.Elapsed.TotalMilliseconds;

        s.Restart();
        SelectObject(mem, old);
        DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        s.Stop(); clean = s.Elapsed.TotalMilliseconds;

        // 保留引用避免被过于乐观地提前回收（真实路径里 bs 会赋给 _captureH.Source 活一阵子）
        GC.KeepAlive(bs);

        sTotal.Stop();
        return sTotal.Elapsed.TotalMilliseconds;
    }

    /// <summary>不拆分的完整序列（warmup 用）。</summary>
    private static void CaptureFull(int w, int h, out BitmapSource? bs)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, w, h, screen, 0, 0, SRCCOPY);
        SelectObject(mem, old);
        bs = null;
        try
        {
            bs = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bs?.Freeze();
        }
        catch { }
        DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
    }

    private static void Stats(double[] xs, string label)
    {
        var sorted = xs.OrderBy(x => x).ToArray();
        double avg = xs.Average();
        double min = sorted[0];
        double max = sorted[^1];
        double p50 = sorted[sorted.Length / 2];
        double p95 = sorted[(int)(sorted.Length * 0.95)];
        double p99 = sorted[(int)(sorted.Length * 0.99)];
        Console.WriteLine($"  {label,-58} avg={avg:F3}  min={min:F3}  p50={p50:F3}  p95={p95:F3}  p99={p99:F3}  max={max:F3}  (ms)");
    }
}
