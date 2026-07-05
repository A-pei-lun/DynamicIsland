using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace DynamicIslandPro
{
    /// <summary>
    /// 液态玻璃自渲染器（门面）。对内按环境挑选渲染后端（DXGI 硬件加速 / HLSL 兼容模式），
    /// 对外只暴露 Start/Stop/UpdateSettings，MainWindow 不感知后端细节。
    ///
    /// 探测逻辑（Probe）：
    /// - RDP 远程桌面 → 直接走 HlslGlassBackend
    /// - 物理机/本地登录 → 先试 DxgiGlassBackend（TODO），失败降级 HlslGlassBackend
    /// </summary>
    internal sealed class LiquidGlassRenderer
    {
        private readonly Window _window;
        private readonly Image _captureH;     // 中间层：H-pass 着色器
        private readonly Rectangle _capture;  // 最终层：V-pass 着色器
        private readonly Border _tint;
        private LiquidGlass.IGlassBackend? _backend;

        public LiquidGlassRenderer(Window window, Image captureH, Rectangle capture, Border tint)
        {
            _window = window;
            _captureH = captureH;
            _capture = capture;
            _tint = tint;
        }

        public string BackendName => _backend?.Name ?? "未启动";

        public void Start(IntPtr hwnd)
        {
            if (_backend != null && _backend.IsRunning)
            {
                _backend.UpdateSettings(DisplaySettings.Instance);
                return;
            }

            _backend = Probe();
            _backend.Start(hwnd);
        }

        public void Stop()
        {
            _backend?.Stop();
            _backend = null;
        }

        public void UpdateSettings()
        {
            _backend?.UpdateSettings(DisplaySettings.Instance);
        }

        private LiquidGlass.IGlassBackend Probe()
        {
            if (SystemParameters.IsRemoteSession)
                goto Hlsl;

            // TODO: 方案 B 就绪后在此试建 DxgiGlassBackend

        Hlsl:
            return new LiquidGlass.HlslGlassBackend(_window, _captureH, _capture, _tint);
        }
    }
}
