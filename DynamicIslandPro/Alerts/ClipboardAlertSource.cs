using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DynamicIslandPro.Island;

namespace DynamicIslandPro.Alerts
{
    /// <summary>
    /// 剪贴板提醒源：监听系统剪贴板变化（Win32 AddClipboardFormatListener），
    /// 当剪贴板内容是图像时投递"截图已复制"提醒——覆盖 Win+Shift+S 截图、
    /// 截图工具、部分浏览器右键复制图片等场景。
    ///
    /// 实现要点：
    /// - AddClipboardFormatListener 向指定 HWND 注册，剪贴板变化时该窗口收到 WM_CLIPBOARDUPDATE。
    /// - 需要在 WPF 窗口上挂 HwndSource.AddHook 接收消息，回调 OnClipboardUpdate。
    /// - 判定图像：优先 IsClipboardFormatAvailable(CF_DIB)，回退 CF_BITMAP/CF_DIBV5。
    /// - 防刷屏：每次剪贴板变化只投一条；不读图像数据（避免大图卡顿），只判格式。
    /// </summary>
    public sealed class ClipboardAlertSource : IDisposable
    {
        private readonly AlertHost _host;
        private HwndSource? _source;
        private IntPtr _hwnd;
        private bool _listening;

        // 防抖：截图工具（Win+Shift+S 等）一次截图会触发多次 WM_CLIPBOARDUPDATE
        //（先写中间状态再写最终图），不去重会导致一次截图弹多条通知。
        // 1.5s 内的连续图像写入只投第一条。
        private DateTime _lastFiredUtc = DateTime.MinValue;
        private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1.5);

        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private const uint CF_DIB = 8;
        private const uint CF_BITMAP = 2;
        private const uint CF_DIBV5 = 17;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsClipboardFormatAvailable(uint format);

        public ClipboardAlertSource(AlertHost host) => _host = host;

        /// <summary>把监听挂到指定 WPF 窗口。在窗口 Loaded 后调用。</summary>
        public void Attach(Window window)
        {
            if (_listening) return;
            _source = PresentationSource.FromVisual(window) as HwndSource
                      ?? new HwndSource(new HwndSourceParameters("di-clip") { Width = 0, Height = 0 });
            _hwnd = _source.Handle;
            _source.AddHook(WndProc);
            _listening = AddClipboardFormatListener(_hwnd);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE && DisplaySettings.Instance.EnableClipboardAlert)
            {
                // 判定是否图像：CF_DIB（截图工具默认写入）优先
                bool isImage = IsClipboardFormatAvailable(CF_DIB)
                            || IsClipboardFormatAvailable(CF_DIBV5)
                            || IsClipboardFormatAvailable(CF_BITMAP);
                if (isImage)
                {
                    // 防抖：一次截图会触发多次剪贴板更新，1.5s 内只投第一条
                    var now = DateTime.UtcNow;
                    if (now - _lastFiredUtc >= Debounce)
                    {
                        _lastFiredUtc = now;
                        _host.Enqueue(new SimpleAlert(
                            "clipboard.screenshot", "截图已复制", "可粘贴到任意位置", "🖼",
                            TimeSpan.FromSeconds(2.5), priority: 40));
                    }
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_listening)
            {
                try { RemoveClipboardFormatListener(_hwnd); } catch { }
                _listening = false;
            }
            _source?.RemoveHook(WndProc);
            _source = null;
        }
    }
}
