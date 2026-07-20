using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Island;

namespace DynamicIsland.LiquidGlass
{
    /// <summary>
    /// 方案 A 兼容模式后端：BitBlt 抓窗后桌面 + HLSL 可分离高斯模糊（GPU ps_3_0）。
    ///
    /// 管线：
    ///   BitBlt → BitmapSource                            CPU 抓屏
    ///      ↓
    ///   GlassCaptureH.Source = captured bitmap            H-pass Image（隐藏）
    ///   GlassCaptureH.Effect = GaussianBlurHEffect        GPU 水平高斯
    ///      ↓
    ///   GlassCapture.Source = VisualBrush(GlassCaptureH)  VisualBrush 读 H 结果
    ///   GlassCapture.Effect = GaussianBlurVEffect         GPU 垂直高斯
    ///      ↓
    ///   最终画面（胶囊圆角裁剪）
    ///
    /// 关键：VisualBrush 捕获视觉树渲染输出（含 Effect），链式合成全程走 WPF DirectX 合成管线，
    /// 不经过软件渲染。两遍 9-tap 可分离高斯，等效 81-tap 2D 高斯。
    /// </summary>
    internal sealed class HlslGlassBackend : IGlassBackend
    {
        // ── GDI 截屏 ──
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
        private readonly Image _captureH;       // 中间层：H-pass 着色器（Image，喂 BitBlt Source）
        private readonly Rectangle _capture;    // 最终层：V-pass 着色器（Rectangle，Fill=VisualBrush 读 H-pass 结果）
        private readonly Border _tint;
        private readonly GaussianBlurHEffect _hEffect;
        private readonly GaussianBlurVEffect _vEffect;
        private DispatcherTimer? _timer;
        private IntPtr _hwnd;
        private double _radius;
        private BackdropStrength _backdrop = BackdropStrength.Subtle;
        private bool _sleep;

        public bool IsRunning { get; private set; }
        public string Name => "HLSL 兼容模式";

        public HlslGlassBackend(Window window, Image captureH, Rectangle capture, Border tint)
        {
            _window = window;
            _captureH = captureH;
            _captureH.Effect = null; // 动态赋 H-pass
            _capture = capture;
            _capture.Effect = null; // 动态赋 V-pass
            _tint = tint;
            _hEffect = new GaussianBlurHEffect();
            _vEffect = new GaussianBlurVEffect();
        }

        public void Start(IntPtr hwnd)
        {
            _hwnd = hwnd;
            if (IsRunning) { UpdateSettings(DisplaySettings.Instance); return; }
            IsRunning = true;

            SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);

            // 搭链：H-pass Image 抓屏 → BitmapCache 缓存含着色器的渲染结果；
            //       V-pass Rectangle 用 VisualBrush 读缓存 → 再跑 V-pass 着色器。
            _captureH.Effect = _hEffect;
            _captureH.CacheMode = new BitmapCache(1.0);
            _captureH.Visibility = Visibility.Visible;
            _capture.Fill = new VisualBrush(_captureH) { Stretch = Stretch.Fill };
            _capture.Effect = _vEffect;
            _capture.Visibility = Visibility.Visible;
            _tint.Visibility = Visibility.Visible;

            UpdateSettings(DisplaySettings.Instance);

            _timer = new DispatcherTimer { Interval = IntervalFromFps() };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            if (_timer != null) { _timer.Stop(); _timer = null; }
            _captureH.Visibility = Visibility.Collapsed;
            _captureH.Source = null;
            _captureH.Effect = null;
            _capture.Visibility = Visibility.Collapsed;
            _capture.Fill = null;
            _capture.Effect = null;
            _tint.Visibility = Visibility.Collapsed;
            try { if (_hwnd != IntPtr.Zero) SetWindowDisplayAffinity(_hwnd, WDA_NONE); } catch { }
        }

        public void UpdateSettings(DisplaySettings s)
        {
            _radius = s.GlassBlurRadius;

            UpdateTexelSize(); // 尺寸可能已变（展开/收起），每帧 Tick 也会更新

            bool isDark = !s.IsLight();
            double t = Math.Clamp(s.GlassTintIntensity, 0.0, 100.0) / 100.0;
            // Backdrop 强度按 tier 缩放底色（Subtle 更透、Strong 更浓）；Sleep 再叠一层微暗。
            t *= BackdropMultiplier();
            if (_sleep) t = Math.Min(1.0, t * 1.25);
            t = Math.Clamp(t, 0.0, 1.0);
            byte a = (byte)Math.Round(255.0 * t);
            var baseColor = isDark ? Colors.Black : Colors.White;
            _tint.Background = new SolidColorBrush(Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B));

            if (_timer != null)
                _timer.Interval = IntervalFromFps();
        }

        /// <summary>按 tier 设置背景强度档位（Subtle/Medium/Strong）。仅 LiquidGlass 模式有效，非运行态缓存。</summary>
        public void SetBackdrop(BackdropStrength b)
        {
            _backdrop = b;
            if (IsRunning) UpdateSettings(DisplaySettings.Instance);
        }

        /// <summary>省电态：帧率减半 + 微暗底色。任何交互应立即退出。</summary>
        public void SetSleep(bool on)
        {
            _sleep = on;
            if (IsRunning) UpdateSettings(DisplaySettings.Instance);
        }

        private double BackdropMultiplier() => _backdrop switch
        {
            BackdropStrength.Subtle => 0.8,
            BackdropStrength.Medium => 1.0,
            BackdropStrength.Strong => 1.2,
            _ => 1.0,
        };

        private void UpdateTexelSize()
        {
            double w = _captureH.ActualWidth;
            double h = _captureH.ActualHeight;
            if (w < 1 || h < 1) { w = 360; h = 40; }

            // 9-tap 核有效半径上限：跨距过大时离散采样点间距过宽 → 产生条纹。
            // 水平和垂直分别按各自维度钳制。
            double maxRH = w / 6.0;  // 展开态 720px → 120; 收起态 360px → 60
            double maxRV = h / 6.0;  // 展开态 240px → 40;  收起态 40px → 6
            double rH = Math.Min(_radius, maxRH);
            double rV = Math.Min(_radius, maxRV);

            _hEffect.TexelSize = new Point(rH / w, 0);
            _vEffect.TexelSize = new Point(0, rV / h);
        }

        private TimeSpan IntervalFromFps()
        {
            int fps = Math.Clamp(DisplaySettings.Instance.GlassCaptureFps, 1, DisplaySettings.Instance.MaxGlassCaptureFps);
            if (_sleep) fps = Math.Max(1, fps / 2); // 省电：帧率减半
            return TimeSpan.FromMilliseconds(1000.0 / fps);
        }

        private void Tick()
        {
            if (!_window.IsVisible) return;
            var bmp = CaptureBehind();
            if (bmp == null) return;

            UpdateTexelSize(); // 窗口尺寸可能在动画中变了
            _captureH.Source = bmp;
        }

        // ── BitBlt ──
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
