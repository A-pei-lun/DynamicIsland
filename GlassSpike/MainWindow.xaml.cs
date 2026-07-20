using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassSpike;

/// <summary>
/// 液态玻璃 spike：验证"自渲染可调模糊"是否比 DWM 亚克力更值得做成 beta。
///
/// 关键招：SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) 让本窗在截屏里不可见，
/// 于是 BitBlt 抓到的就是"窗后桌面"（无反馈——本窗内容不会进入下一帧抓取），
/// 再叠 WPF BlurEffect 做可调半径高斯模糊（DWM 任何路径都不开放半径，这是唯一能调模糊量的路）。
///
/// B 切 Liquid(自渲染)/DWM(亚克力) 对照；↑↓ 半径；E 金边(边缘高光)；D 深/浅；Esc 退出。
/// 拖动可移到不同桌面背景上观察。判定标准：Liquid 观感是否明显优于 DWM，且 FPS 可接受。
/// </summary>
public partial class MainWindow : Window
{
    // ── 显示亲和性 + GDI 截屏 ──
    const uint WDA_NONE = 0x00000000;
    const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr src, int x1, int y1, uint rop);
    const uint SRCCOPY = 0x00CC0020;
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    // ── DWM 路径（A/B 对照用，复用 AcrylicSpike 的 accent 代码）──
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWCP_ROUND = 2;
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int cb);
    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

    const int WCA_ACCENT_POLICY = 19;
    const int ACCENT_DISABLED = 0;
    const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    [StructLayout(LayoutKind.Sequential)] struct ACCENT_POLICY { public int AccentState, AccentFlags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)] struct WINCOMPATTRDATA { public int Attribute; public IntPtr Data; public IntPtr SizeOfData; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);

    IntPtr _hwnd;
    bool _liquid = true, _edge = true, _dark = true;
    double _radius = 22;
    DispatcherTimer _timer = null!;
    int _frames, _fps;
    DateTime _fpsT;

    public MainWindow() { InitializeComponent(); }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        if (PresentationSource.FromVisual(this) is HwndSource src && src.CompositionTarget != null)
            src.CompositionTarget.BackgroundColor = Colors.Transparent;
        int round = DWMWCP_ROUND; DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30fps
        _timer.Tick += (_, _) => Tick();
        _fpsT = DateTime.UtcNow;
        SetMode(true);
    }

    void Tick()
    {
        if (!_liquid) return;
        var bmp = CaptureBehind();
        if (bmp != null) CaptureImage.Source = bmp;
        _frames++;
        var now = DateTime.UtcNow;
        if ((now - _fpsT).TotalMilliseconds >= 500)
        {
            _fps = _frames * 2; _frames = 0; _fpsT = now; // 0.5s 窗口 ×2 = 每秒帧数
        }
        UpdateStatus();
    }

    /// <summary>抓本窗所在矩形处的"窗后桌面"：EXCLUDEFROMCAPTURE 让本窗在截屏里不可见，故无反馈。</summary>
    BitmapSource? CaptureBehind()
    {
        GetWindowRect(_hwnd, out var rc);
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return null;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, w, h, screen, rc.Left, rc.Top, SRCCOPY);
        SelectObject(mem, old);
        BitmapSource? bs = null;
        try
        {
            bs = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (bs != null) bs.Freeze();
        }
        catch { }
        DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        return bs;
    }

    void SetMode(bool liquid)
    {
        _liquid = liquid;
        if (liquid)
        {
            // 自渲染：本窗排除出截屏 + 抓窗后桌面 + WPF 模糊。不需要 DWM backdrop。
            SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);
            SetAccent(ACCENT_DISABLED, 0);
            var m0 = new MARGINS(); DwmExtendFrameIntoClientArea(_hwnd, ref m0);
            CaptureImage.Visibility = Visibility.Visible;
            Tint.Visibility = Visibility.Visible;
            _timer.Start();
        }
        else
        {
            // DWM 对照：accent state4 亚克力（= 现主程序的模糊路径）+ 金边叠层。
            _timer.Stop();
            SetWindowDisplayAffinity(_hwnd, WDA_NONE);
            var m1 = new MARGINS { cxLeftWidth = -1 }; DwmExtendFrameIntoClientArea(_hwnd, ref m1);
            SetAccent(ACCENT_ENABLE_ACRYLICBLURBEHIND, _dark ? 0x60000000u : 0x60FFFFFFu);
            CaptureImage.Visibility = Visibility.Hidden;
            Tint.Visibility = Visibility.Hidden;
        }
        UpdateStatus();
    }

    int SetAccent(int state, uint gradient)
    {
        var policy = new ACCENT_POLICY { AccentState = state, AccentFlags = 2, GradientColor = gradient, AnimationId = 0 };
        var data = new WINCOMPATTRDATA
        {
            Attribute = WCA_ACCENT_POLICY,
            Data = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>()),
            SizeOfData = (IntPtr)Marshal.SizeOf<ACCENT_POLICY>()
        };
        try { Marshal.StructureToPtr(policy, data.Data, false); return SetWindowCompositionAttribute(_hwnd, ref data); }
        finally { Marshal.FreeHGlobal(data.Data); }
    }

    void UpdateStatus()
    {
        Status.Text = $"{(_liquid ? "液态玻璃 · 自渲染" : "DWM 亚克力 · 对照")}\n"
                    + $"半径 {_radius:0}   金边 {(_edge ? "开" : "关")}   底色 {(_dark ? "深" : "浅")}\n"
                    + $"FPS {_fps}   抓屏 EXCLUDEFROMCAPTURE";
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up: _radius = Math.Min(100, _radius + 2); Blur.Radius = _radius; UpdateStatus(); break;
            case Key.Down: _radius = Math.Max(0, _radius - 2); Blur.Radius = _radius; UpdateStatus(); break;
            case Key.B: SetMode(!_liquid); break;
            case Key.E: _edge = !_edge; GlassEdge.Visibility = _edge ? Visibility.Visible : Visibility.Collapsed; UpdateStatus(); break;
            case Key.D:
                _dark = !_dark;
                Tint.Background = new SolidColorBrush(_dark ? Color.FromArgb(0x40, 0, 0, 0) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                if (!_liquid) SetAccent(ACCENT_ENABLE_ACRYLICBLURBEHIND, _dark ? 0x60000000u : 0x60FFFFFFu);
                UpdateStatus();
                break;
            case Key.Escape: Close(); break;
        }
    }

    void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
