using Windows.Win32.Graphics.Direct3D11;

namespace DynamicIsland.LiquidGlass
{
    /// <summary>
    /// 桌面抓屏抽象。事件驱动（后台线程回调），交付 D3D11 纹理给模糊/显示管线。
    /// WinGC FreeThreaded 在 ThreadPool 触发，不阻塞 UI 线程。
    /// </summary>
    internal interface IDesktopCapture : IDisposable
    {
        /// <summary>
        /// 新帧到达（后台线程）。texture 仅在回调内有效：回调返回后底层帧即被 WinGC 回收，
        /// 消费方须在回调内完成 CopySubresourceRegion 等全部 GPU 读取，不得跨回调持有。
        /// （WinGC 与模糊共用同一 D3D11 设备，写/读命令同队列串行，无跨设备同步需求。）
        /// </summary>
        event Action<ID3D11Texture2D, uint, uint>? FrameArrived;

        bool IsRunning { get; }

        /// <summary>人类可读名称，供设置窗展示。</summary>
        string Name { get; }

        /// <summary>开始抓取指定显示器。hmonitor = 目标屏 HMONITOR。</summary>
        void Start(IntPtr hmonitor);

        void Stop();
    }
}
