using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Island;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Direct3D9;
using Windows.Win32.Graphics.Dxgi.Common;

namespace DynamicIsland.LiquidGlass
{
    /// <summary>
    /// GPU 液态玻璃后端：WinGC 抓屏 + D3D11 模糊 + D3D9/D3D11 共享纹理 + D3DImage 显示。
    ///
    /// 数据流（后台线程 FrameArrived）：
    ///   WinGC monitor 纹理 --CopySubresourceRegion(岛矩形)--> inputTex(岛尺寸)
    ///      --GpuBlur H/V--> 共享纹理(D3D11 RTV) + Flush
    ///      --UI Dispatcher--> D3DImage.SetBackBuffer(surf9) + Lock/DirtyRect/Unlock
    ///
    /// WinGC FreeThreaded 回调 ThreadPool，UI 零阻塞（vs Hlsl 的 BitBlt 在 UI 线程 4.2ms）。
    /// WinGC 限 60fps（方案一接受）。无 KeyedMutex，靠 Flush + D3DImage.Lock/Unlock 同步。
    /// 构造失败 / 连续 3 帧空 -> FallbackRequested，由 LiquidGlassRenderer 切 Hlsl。
    /// </summary>
    internal sealed class GpuGlassBackend : IGlassBackend
    {
        const uint WDA_NONE = 0x00000000;
        const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        const uint MONITOR_DEFAULTTOPRIMARY = 1;

        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rc);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr hmon, ref MONITORINFO info);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        private readonly Window _window;
        private readonly Image _glassImage;
        private readonly D3DImage _d3dImage = new();
        private readonly Border _tint;
        private readonly Dispatcher _dispatcher;

        private ID3D11Device? _dev11;
        private ID3D11DeviceContext? _ctx11;
        private WinGCCapture? _capture;
        private GpuBlur? _blur;
        private D3D11Interop? _interop;
        private ID3D11Texture2D? _inputTex;

        private IntPtr _hwnd;
        private uint _texW, _texH;
        private bool _backBufferSet;
        private int _emptyFrames;
        private int _sleepSkipper;

        private double _radius;
        private BackdropStrength _backdrop = BackdropStrength.Subtle;
        private bool _sleep;
        private int _presentCount; // 诊断：PresentOnUI 被调次数
        private int _frameCount;  // 诊断：OnFrameArrived 被调次数

        // ── 阶段 1C 分段诊断 ──
        private int _sizeOk;
        private int _copyOk;
        private int _blurReturn;
        private int _flushOk;
        private int _queueOk;
        private int _renderFail;
        private int _fallbackSuppressed;
        private string _lastStage = "none";
        private string _lastFail = "none";
        private int _lastRenderHr;

        public bool IsRunning { get; private set; }
        public string Name => $"GPU 硬件加速 (r={_radius:0.0}, {_texW}x{_texH}, mapFail={_blur?.MapFailCount ?? 0}, present={_presentCount}, frame={_frameCount}, wgc={_capture?.WinGcFrameCount ?? -1}, wgcFail={_capture?.WinGcExtractFailCount ?? -1}, cast={_capture?.WinGcCastFail ?? -1}, hr={_capture?.WinGcHrFail ?? -1}, lastHr={_capture?.WinGcLastHr ?? 0}, nativeQiHr={_capture?.WinGcNativeQiHr ?? 0}, surfType={_capture?.WinGcSurfaceType ?? "?"}, size={_sizeOk}, copy={_copyOk}, blur={_blurReturn}, flush={_flushOk}, queue={_queueOk}, renderFail={_renderFail}, stage={_lastStage}, fail={_lastFail}, renderHr={_lastRenderHr}, hold={_fallbackSuppressed})";

        /// <summary>构造失败 / 连续空帧时触发，请求 LiquidGlassRenderer 回退 Hlsl。</summary>
        public event Action? FallbackRequested;

        public GpuGlassBackend(Window window, Image glassImage, Border tint)
        {
            _window = window;
            _glassImage = glassImage;
            _glassImage.Source = _d3dImage;
            _tint = tint;
            _dispatcher = window.Dispatcher;
        }

        public void Start(IntPtr hwnd)
        {
            _hwnd = hwnd;
            if (IsRunning) { UpdateSettings(DisplaySettings.Instance); return; }

            try
            {
                PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, default,
                    D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, ReadOnlySpan<D3D_FEATURE_LEVEL>.Empty, 7,
                    out _dev11, out _, out _ctx11);

                _blur = new GpuBlur(_dev11, _ctx11);
                _interop = new D3D11Interop(_dev11, _ctx11);
                _capture = new WinGCCapture(_dev11);
                _capture.FrameArrived += OnFrameArrived;

                SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);
                _glassImage.Visibility = Visibility.Visible;
                _tint.Visibility = Visibility.Visible;

                if (!UpdateIslandSize())
                    throw new InvalidOperationException("无法读取窗口尺寸");

                IntPtr hmon = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTOPRIMARY);
                _capture.Start(hmon);

                IsRunning = true;
                UpdateSettings(DisplaySettings.Instance);
            }
            catch
            {
                Stop();
                FallbackRequested?.Invoke();
            }
        }

        public void Stop()
        {
            if (!IsRunning && _capture == null && _dev11 == null) return;
            IsRunning = false;

            if (_capture != null) { _capture.FrameArrived -= OnFrameArrived; _capture.Dispose(); _capture = null; }
            _blur?.Dispose(); _blur = null;
            _interop?.Dispose(); _interop = null;
            Rel(_inputTex); _inputTex = null;

            // 解绑 D3DImage back buffer（UI 线程）
            // tint 同步隐藏：避免与回退 Hlsl.Start（同步显 tint）的异步 BeginInvoke 竞争导致 tint 被后置关闭
            try { _tint.Visibility = Visibility.Collapsed; } catch { }
            try
            {
                _dispatcher.BeginInvoke(() =>
                {
                    try { _d3dImage.Lock(); _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero); _d3dImage.Unlock(); } catch { }
                    _glassImage.Visibility = Visibility.Collapsed;
                });
            }
            catch { }
            _backBufferSet = false;

            try { if (_hwnd != IntPtr.Zero) SetWindowDisplayAffinity(_hwnd, WDA_NONE); } catch { }

            Rel(_ctx11); _ctx11 = null;
            Rel(_dev11); _dev11 = null;
            _texW = _texH = 0;
        }

        public void UpdateSettings(DisplaySettings s)
        {
            _radius = s.GlassBlurRadius;
            if (_blur != null && _texW > 0) _blur.Configure(_texW, _texH, _radius);

            bool isDark = !s.IsLight();
            double t = Math.Clamp(s.GlassTintIntensity, 0.0, 100.0) / 100.0;
            t *= BackdropMultiplier();
            if (_sleep) t = Math.Min(1.0, t * 1.25);
            t = Math.Clamp(t, 0.0, 1.0);
            byte a = (byte)Math.Round(255.0 * t);
            var baseColor = isDark ? Colors.Black : Colors.White;
            _tint.Background = new SolidColorBrush(Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B));
        }

        public void SetBackdrop(BackdropStrength b)
        {
            _backdrop = b;
            if (IsRunning) UpdateSettings(DisplaySettings.Instance);
        }

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

        // ── 后台线程：WinGC 帧到达 ──

        private void OnFrameArrived(ID3D11Texture2D frameTex, uint monW, uint monH)
        {
            if (!IsRunning || _dev11 == null || _ctx11 == null || _blur == null || _interop == null) return;
            _frameCount++;

            // 省电：跳半帧
            if (_sleep && (_sleepSkipper++ & 1) == 0) return;

            try
            {
                // ── 1. 尺寸与资源检查 ──
                _lastStage = "size";
                if (!GetWindowRect(_hwnd, out var rc)) { RegisterEmpty("window-rect"); return; }
                uint w = (uint)(rc.Right - rc.Left), h = (uint)(rc.Bottom - rc.Top);
                if (w == 0 || h == 0) { RegisterEmpty("zero-size"); return; }
                if (w != _texW || h != _texH)
                {
                    _lastStage = "resize";
                    if (!UpdateIslandSize(w, h)) { RegisterEmpty("resize-false"); return; }
                }

                // ── 2. 监器裁剪计算 ──
                _lastStage = "bounds";
                IntPtr hmon = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTOPRIMARY);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                GetMonitorInfoW(hmon, ref mi);
                uint sx = (uint)(rc.Left - mi.rcMonitor.Left);
                uint sy = (uint)(rc.Top - mi.rcMonitor.Top);
                if (sx + w > monW) w = monW > sx ? monW - sx : 0;
                if (sy + h > monH) h = monH > sy ? monH - sy : 0;
                if (w == 0 || h == 0) { RegisterEmpty("crop-empty"); return; }

                // ── 3. 资源有效性 ──
                _lastStage = "resource";
                if (_inputTex == null || _interop.OutputRtv == null) { RegisterEmpty("resource-null"); return; }
                _sizeOk++;

                // ── 4. 纹理复制 ──
                // 注意：frameTex 来自 Marshal.GetObjectForIUnknown，其 RCW 不支持直接
                // 转换为 ID3D11Resource（CsWin32 内部会抛出 InvalidCastException）。
                // 手动走 COM QueryInterface 获取 ID3D11Resource 指针。
                _lastStage = "copy";
                // frameTex 的 RCW 不支持 ID3D11Resource 接口转换。
                // 方案：从 frameTex 的 IUnknown* 创建新 RCW，再转型为 ID3D11Resource。
                IntPtr frameUnk = Marshal.GetIUnknownForObject(frameTex);
                object frameObj;
                try
                {
                    frameObj = Marshal.GetObjectForIUnknown(frameUnk);
                }
                finally
                {
                    Marshal.Release(frameUnk);
                }
                var srcResource = frameObj as ID3D11Resource;
                if (srcResource == null)
                {
                    RegisterEmpty("copy-no-resource");
                    return;
                }
                var box = new D3D11_BOX { left = sx, top = sy, front = 0, right = sx + w, bottom = sy + h, back = 1 };
                _ctx11.CopySubresourceRegion((ID3D11Resource)_inputTex, 0, 0, 0, 0, srcResource, 0, box);
                _copyOk++;

                // ── 5. 模糊 ──
                _lastStage = "blur";
                _blur.Blur(_inputTex, _interop.OutputRtv);
                _blurReturn++;

                // ── 6. Flush ──
                _lastStage = "flush";
                _interop.Flush();
                _flushOk++;

                _emptyFrames = 0;

                // ── 7. UI 调度 ──
                _lastStage = "queue";
                _dispatcher.BeginInvoke(PresentOnUI);
                _queueOk++;

                _lastStage = "complete";
            }
            catch (Exception ex)
            {
                _renderFail++;
                _lastRenderHr = ex.HResult;
                RegisterEmpty(_lastStage);
                _lastFail = ex.GetType().Name; // 保留异常类型，不会被 RegisterEmpty 覆盖
            }
        }

        private void RegisterEmpty(string reason)
        {
            _lastFail = reason;
            if (++_emptyFrames >= 3)
            {
                _emptyFrames = 0;
                // 强制 GPU 模式：保留现场，不触发回退（阶段 1C 诊断）
                if ((int)(object)DisplaySettings.Instance.CaptureMode == 1) // Gpu
                {
                    _fallbackSuppressed++;
                    return;
                }
                FallbackRequested?.Invoke();
            }
        }

        // ── UI 线程：D3DImage 呈现 ──

        private void PresentOnUI()
        {
            if (!IsRunning || _interop?.Surface9 == null) return;
            _presentCount++;
            try
            {
                if (!_backBufferSet)
                {
                    IntPtr p = Marshal.GetComInterfaceForObject(_interop.Surface9, typeof(IDirect3DSurface9));
                    _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, p);
                    Marshal.Release(p); // D3DImage 内部 AddRef，平衡我的 GetComInterface
                    _backBufferSet = true;
                }
                _d3dImage.Lock();
                _d3dImage.AddDirtyRect(new Int32Rect(0, 0, (int)_texW, (int)_texH));
                _d3dImage.Unlock();
            }
            catch { }
        }

        // ── 尺寸/纹理管理 ──

        private bool UpdateIslandSize() => UpdateIslandSize(null, null);

        private bool UpdateIslandSize(uint? forceW, uint? forceH)
        {
            uint w, h;
            if (forceW.HasValue && forceH.HasValue) { w = forceW.Value; h = forceH.Value; }
            else
            {
                if (!GetWindowRect(_hwnd, out var rc)) return false;
                w = (uint)(rc.Right - rc.Left); h = (uint)(rc.Bottom - rc.Top);
                if (w == 0 || h == 0) return false;
            }
            if (w == _texW && h == _texH && _inputTex != null) return true;
            if (_dev11 == null || _interop == null || _blur == null) return false;

            // 尺寸变：重建 inputTex + 共享纹理 + 中间纹理（SetBackBuffer 也要重设）
            Rel(_inputTex);
            _inputTex = CreateInputTexture(w, h);

            _interop.EnsureSize(w, h);
            _blur.Configure(w, h, _radius);

            // surf9 换了，UI 线程重设 back buffer
            _backBufferSet = false;
            _texW = w; _texH = h;
            return true;
        }

        private ID3D11Texture2D CreateInputTexture(uint w, uint h)
        {
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = w, Height = h, MipLevels = 1, ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                CPUAccessFlags = 0, MiscFlags = 0,
            };
            _dev11!.CreateTexture2D(desc, null, out ID3D11Texture2D tex);
            return tex;
        }

        private static void Rel(object? o)
        {
            if (o is not null) try { Marshal.ReleaseComObject(o); } catch { }
        }
    }
}
