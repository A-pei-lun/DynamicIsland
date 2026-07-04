using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DynamicIsland
{
    /// <summary>
    /// 液态玻璃自渲染器（beta）：把 GlassSpike 验证的自渲染可调模糊接到主程序。
    ///
    /// 原理：SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) 让本窗在截屏里不可见，
    /// 于是 BitBlt 抓本窗矩形处得到的就是"窗后桌面"（无抓屏反馈——本窗内容不进入下一帧抓取），
    /// 再用 WPF BlurEffect 做可调半径高斯模糊（DWM 任何路径都不开放半径，这是唯一能调模糊量的路）。
    /// 模糊后的图作为 GlassCapture 的 Source 填满胶囊（GlassBorder 圆角裁剪到胶囊形），上叠 GlassTint 底色。
    ///
    /// 窗口基座（透穿）由 WindowBackdrop.Apply 的 LiquidGlass 分支走 accent state2 处理；本类只管自渲染层。
    /// 仅 BackdropMode.LiquidGlass 时由 MainWindow 启停。参数（半径/底色/帧率）全来自 DisplaySettings，运行中可调。
    /// </summary>
    internal sealed class LiquidGlassRenderer
    {
        // ── 显示亲和性 + GDI 截屏（端口自 GlassSpike）──
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

        private readonly Window _window;
        private readonly Image _capture;   // GlassCapture：填满胶囊的模糊图
        private readonly BlurEffect _blur; // GlassBlur：可调半径
        private readonly Border _tint;     // GlassTint：底色叠层
        private DispatcherTimer? _timer;
        private IntPtr _hwnd;
        private bool _running;

        /// <param name="window">主窗（取 hwnd + 判 IsVisible）。</param>
        /// <param name="capture">XAML 里的 GlassCapture Image（其 Effect 须为 BlurEffect）。</param>
        /// <param name="tint">XAML 里的 GlassTint Border。</param>
        public LiquidGlassRenderer(Window window, Image capture, Border tint)
        {
            _window = window;
            _capture = capture;
            _blur = (BlurEffect)capture.Effect!;
            _tint = tint;
        }

        /// <summary>启用自渲染：排除截屏 + 显示图层 + 起抓屏定时器。幂等（已运行则只刷新参数）。</summary>
        public void Start(IntPtr hwnd)
        {
            _hwnd = hwnd;
            if (_running) { UpdateSettings(); return; }
            _running = true;
            SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);
            _capture.Visibility = Visibility.Visible;
            _tint.Visibility = Visibility.Visible;
            UpdateSettings();
            _timer = new DispatcherTimer { Interval = IntervalFromFps() };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        /// <summary>停用自渲染：撤销排除截屏 + 隐藏图层 + 停定时器。</summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;
            if (_timer != null) { _timer.Stop(); _timer = null; }
            _capture.Visibility = Visibility.Collapsed;
            _tint.Visibility = Visibility.Collapsed;
            _capture.Source = null;
            try { if (_hwnd != IntPtr.Zero) SetWindowDisplayAffinity(_hwnd, WDA_NONE); } catch { }
        }

        /// <summary>按设置刷新模糊半径 / 底色 / 帧率。运行中调即时生效。</summary>
        public void UpdateSettings()
        {
            _blur.Radius = DisplaySettings.Instance.GlassBlurRadius;

            // 底色：深=黑 alpha / 浅=白 alpha，alpha 由浓度 0→100 线性 [0,255]
            bool isDark = !DisplaySettings.Instance.IsLight();
            double t = Math.Clamp(DisplaySettings.Instance.GlassTintIntensity, 0.0, 100.0) / 100.0;
            byte a = (byte)Math.Round(255.0 * t);
            var baseColor = isDark ? Colors.Black : Colors.White;
            _tint.Background = new SolidColorBrush(Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B));

            if (_timer != null)
                _timer.Interval = IntervalFromFps();
        }

        private TimeSpan IntervalFromFps()
        {
            int fps = Math.Clamp(DisplaySettings.Instance.GlassCaptureFps, 1, 60);
            return TimeSpan.FromMilliseconds(1000.0 / fps);
        }

        private void Tick()
        {
            // 窗口隐藏（全屏抑制）时不抓屏，省 CPU；可见才更新
            if (!_window.IsVisible) return;
            var bmp = CaptureBehind();
            if (bmp != null) _capture.Source = bmp;
        }

        /// <summary>抓本窗所在矩形处的"窗后桌面"：EXCLUDEFROMCAPTURE 让本窗在截屏里不可见，故无反馈。</summary>
        private BitmapSource? CaptureBehind()
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
    }
}
