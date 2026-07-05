namespace DynamicIslandPro.LiquidGlass
{
    /// <summary>
    /// 液态玻璃渲染后端接口。
    /// 所有实现（DXGI/WPF HLSL/Software）都走同一套接口，
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

        /// <summary>人类可读名称，供设置窗展示（"DXGI 硬件加速" / "HLSL 兼容模式"）。</summary>
        string Name { get; }
    }
}
