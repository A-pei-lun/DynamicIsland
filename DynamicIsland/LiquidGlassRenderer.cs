using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using DynamicIsland.Island;
using DynamicIsland.LiquidGlass;

namespace DynamicIsland
{
    /// <summary>
    /// 液态玻璃门面。按 CaptureMode 选择后端：GPU（WinGC+D3D11+D3D9Ex 共享纹理+D3DImage）
    /// 或 Hlsl 兼容（BitBlt+WPF ShaderEffect）。Auto 模式 GPU 构造失败/连续空帧自动回退 Hlsl。
    /// 只委托 IGlassBackend，不关心后端如何工作。
    /// </summary>
    internal sealed class LiquidGlassRenderer
    {
        private readonly Window _window;
        private readonly Image _captureH;       // Hlsl H-pass 宿主
        private readonly Rectangle _capture;    // Hlsl V-pass 宿主
        private readonly Image _d3dImage;       // GPU D3DImage 宿主
        private readonly Border _tint;
        private IGlassBackend? _backend;
        private IntPtr _hwnd;

        public string BackendName => _backend?.Name ?? "未启动";

        public LiquidGlassRenderer(Window window, Image captureH, Rectangle capture, Image d3dImage, Border tint)
        {
            _window = window;
            _captureH = captureH;
            _capture = capture;
            _d3dImage = d3dImage;
            _tint = tint;
        }

        public void Start(IntPtr hwnd)
        {
            _hwnd = hwnd;
            if (_backend != null && _backend.IsRunning)
            {
                _backend.UpdateSettings(DisplaySettings.Instance);
                return;
            }
            CreateAndStart();
        }

        public void Stop()
        {
            _backend?.Stop();
            _backend = null;
        }

        public void UpdateSettings() => _backend?.UpdateSettings(DisplaySettings.Instance);

        /// <summary>抓屏后端热切换（CaptureMode 变更时调，不重启进程）。</summary>
        public void SwitchBackend()
        {
            _backend?.Stop();
            _backend = null;
            if (_hwnd != IntPtr.Zero) CreateAndStart();
        }

        public void SetBackdrop(BackdropStrength b) => _backend?.SetBackdrop(b);
        public void SetSleep(bool on) => _backend?.SetSleep(on);

        private void CreateAndStart()
        {
            var mode = DisplaySettings.Instance.CaptureMode;
            if (mode == CaptureMode.Hlsl) { StartHlsl(); return; }

            // Auto / Gpu：试 GPU，失败回退 Hlsl
            try
            {
                var gpu = new GpuGlassBackend(_window, _d3dImage, _tint);
                gpu.FallbackRequested += OnGpuFallback;
                _backend = gpu;
                gpu.Start(_hwnd);
                if (gpu.IsRunning) return; // GPU 启动成功
            }
            catch { /* 构造异常 -> 回退 */ }
            // GpuGlassBackend.Start 内部失败会吞异常 + Raise FallbackRequested（BeginInvoke 异步），
            // 此处 IsRunning=false 同步回退 Hlsl，异步回调见 _backend 已非 Gpu 则跳过，不重复切。
            StartHlsl();
        }

        private void OnGpuFallback()
        {
            // 运行中连续空帧（后台线程触发）：UI 线程切 Hlsl
            _window.Dispatcher.BeginInvoke(() =>
            {
                if (_backend is GpuGlassBackend) StartHlsl();
            });
        }

        private void StartHlsl()
        {
            _backend?.Stop();
            _backend = new HlslGlassBackend(_window, _captureH, _capture, _tint);
            _backend.Start(_hwnd);
        }
    }
}
