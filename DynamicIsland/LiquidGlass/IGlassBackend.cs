using DynamicIsland.Island;

namespace DynamicIsland.LiquidGlass
{
    /// <summary>
    /// 液态玻璃渲染后端接口。
    /// 所有实现（GPU D3D11+D3D9Ex / WPF HLSL 兼容）都走同一套接口，
    /// LiquidGlassRenderer 作为门面只委托，不关心后端如何工作。
    /// </summary>
    internal interface IGlassBackend
    {
        /// <summary>启动渲染。hwnd 主窗句柄，首次启动和降级切换用。</summary>
        void Start(IntPtr hwnd);

        /// <summary>停止渲染，释放后端资源。</summary>
        void Stop();

        /// <summary>运行时更新参数（半径/底色/帧率/金边），即时生效。</summary>
        void UpdateSettings(DisplaySettings s);

        /// <summary>当前是否正在运行。</summary>
        bool IsRunning { get; }

        /// <summary>人类可读名称，供设置窗展示（"GPU 硬件加速" / "HLSL 兼容模式"）。</summary>
        string Name { get; }

        /// <summary>按 tier 设置背景强度档位（Subtle/Medium/Strong）。仅 LiquidGlass 模式有效。</summary>
        void SetBackdrop(BackdropStrength b);

        /// <summary>省电态：帧率减半 + 微暗底色。任何交互应立即退出。</summary>
        void SetSleep(bool on);
    }
}
