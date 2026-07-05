using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DynamicIslandPro
{
    /// <summary>
    /// 系统托盘图标。纯 P/Invoke 实现（Shell_NotifyIcon），不依赖 WinForms。
    ///
    /// 行为：
    /// - 左键单击 → SettingsRequested 事件（由 MainWindow 响应打开设置窗）
    /// - 右键单击 → Win32 弹出菜单（设置 / 测试提醒 / 开机启动切换 / 退出）
    ///
    /// 用零尺寸隐藏 HwndSource 接收系统回调消息，与 MainWindow 的 Hwnd 解耦。
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        // ─── 事件 ──────────────────────────────────────────────────
        public event EventHandler? SettingsRequested;
        public event EventHandler? TestAlertRequested;
        public event EventHandler? ExitRequested;

        // ─── Win32 ─────────────────────────────────────────────────
        private const int WM_TRAYICON = 0x8000; // WM_APP
        private const int ICON_ID = 1;

        private const int NIM_ADD = 0;
        private const int NIM_MODIFY = 1;
        private const int NIM_DELETE = 2;
        private const int NIM_SETVERSION = 4;

        private const int NIF_MESSAGE = 0x01;
        private const int NIF_ICON = 0x02;
        private const int NIF_TIP = 0x04;

        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_COMMAND = 0x0111;
        private const int WM_NULL = 0x0000;

        // 右键菜单命令 ID
        private const int CMD_SETTINGS = 1001;
        private const int CMD_TEST_ALERT = 1002;
        private const int CMD_AUTOSTART = 1003;
        private const int CMD_EXIT = 1004;

        private const int MF_STRING = 0x0000;
        private const int MF_SEPARATOR = 0x0800;
        private const int MF_CHECKED = 0x0008;
        private const int MF_UNCHECKED = 0x0000;
        private const int TPM_RIGHTBUTTON = 0x0002;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int cmd, ref NOTIFYICONDATA data);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, int uFlags, IntPtr uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll")]
        private static extern bool TrackPopupMenu(IntPtr hMenu, int uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, int nIcons);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            // 以下为 Vista+ 字段，留空但计入 size
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        // ─── 实例 ─────────────────────────────────────────────────
        private HwndSource? _source;
        private IntPtr _hwnd;
        private IntPtr _hIcon;
        private bool _iconIsShared;  // true=LR_SHARED 加载的图标，不能 DestroyIcon
        private NOTIFYICONDATA _nid;
        private bool _created;
        private bool _disposed;

        public TrayIcon()
        {
            // 创建零尺寸隐藏窗口接收 WM_TRAYICON / WM_COMMAND
            var param = new HwndSourceParameters("TrayIconMsgWindow")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,  // 无边框不可见
                ParentWindow = IntPtr.Zero,
            };
            _source = new HwndSource(param) { RootVisual = null };
            _hwnd = _source.Handle;
            _source.AddHook(WndProc);

            _hIcon = LoadAppIcon(out _iconIsShared);
            InitNotifyIcon();
        }

        /// <summary>初始化 NOTIFYICONDATA 并注册到系统托盘。</summary>
        private void InitNotifyIcon()
        {
            _nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = ICON_ID,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _hIcon,
                szTip = "DynamicIsland",
                guidItem = Guid.Empty,
            };
            _created = Shell_NotifyIcon(NIM_ADD, ref _nid);

            // 设置版本：WM_TRAYICON 收到的是鼠标消息号而非已处理标志
            if (_created)
                Shell_NotifyIcon(NIM_SETVERSION, ref _nid);
        }

        /// <summary>
        /// 从进程 exe 提取 16×16 图标。提取失败则回退到标准应用程序图标。
        /// 注意：ExtractIconEx 取得的图标需 DestroyIcon；LR_SHARED 取得的不能销毁。
        /// </summary>
        private static IntPtr LoadAppIcon(out bool isShared)
        {
            isShared = false;
            string exe = GetExePath();
            if (!string.IsNullOrEmpty(exe))
            {
                int count = ExtractIconEx(exe, 0, out _, out var hSmall, 1);
                if (count > 0 && hSmall != IntPtr.Zero)
                    return hSmall;
            }

            // 回退：标准应用图标（共享资源，无需销毁）
            isShared = true;
            return LoadImage(IntPtr.Zero, new IntPtr(32512 /* IDI_APPLICATION */), IMAGE_ICON, 16, 16, LR_SHARED);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr LoadImage(IntPtr hinst, IntPtr lpszName, int uType, int cxDesired, int cyDesired, int fuLoad);
        private const int IMAGE_ICON = 1;
        private const int LR_SHARED = 0x8000;

        private static string GetExePath()
        {
            try { return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty; }
            catch { return string.Empty; }
        }

        // ─── 消息处理 ──────────────────────────────────────────────
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_TRAYICON)
            {
                int mouseMsg = lParam.ToInt32();
                if (mouseMsg == WM_LBUTTONUP)
                {
                    SettingsRequested?.Invoke(this, EventArgs.Empty);
                    handled = true;
                }
                else if (mouseMsg == WM_RBUTTONUP)
                {
                    ShowContextMenu();
                    handled = true;
                }
            }
            else if (msg == WM_COMMAND)
            {
                int cmdId = wParam.ToInt32() & 0xFFFF; // LOWORD
                HandleCommand(cmdId);
                handled = true;
            }

            return IntPtr.Zero;
        }

        // ─── 右键菜单 ──────────────────────────────────────────────
        private void ShowContextMenu()
        {
            GetCursorPos(out POINT pt);

            IntPtr hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return;

            try
            {
                AppendMenu(hMenu, MF_STRING, (IntPtr)CMD_SETTINGS, "设置");
                AppendMenu(hMenu, MF_STRING, (IntPtr)CMD_TEST_ALERT, "测试提醒");
                AppendMenu(hMenu, MF_SEPARATOR, IntPtr.Zero, null);

                bool autoStart = AutoStart.IsEnabled;
                int flags = MF_STRING | (autoStart ? MF_CHECKED : MF_UNCHECKED);
                AppendMenu(hMenu, flags, (IntPtr)CMD_AUTOSTART, "开机启动");

                AppendMenu(hMenu, MF_SEPARATOR, IntPtr.Zero, null);
                AppendMenu(hMenu, MF_STRING, (IntPtr)CMD_EXIT, "退出");

                // 标准托盘菜单模式：SetForegroundWindow + TrackPopupMenu + PostMessage
                SetForegroundWindow(_hwnd);
                TrackPopupMenu(hMenu, TPM_RIGHTBUTTON, pt.x, pt.y, 0, _hwnd, IntPtr.Zero);
                PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                DestroyMenu(hMenu);
            }
        }

        private void HandleCommand(int cmdId)
        {
            switch (cmdId)
            {
                case CMD_SETTINGS:
                    SettingsRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CMD_TEST_ALERT:
                    TestAlertRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CMD_AUTOSTART:
                    AutoStart.SetEnabled(!AutoStart.IsEnabled);
                    break;
                case CMD_EXIT:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        // ─── 工具提示（主窗口文本变化时可调） ──────────────────────
        public void SetTooltip(string text)
        {
            if (_disposed || !_created) return;
            _nid.szTip = text?.Length <= 127 ? text : "DynamicIsland";
            Shell_NotifyIcon(NIM_MODIFY, ref _nid);
        }

        // ─── 生命周期 ──────────────────────────────────────────────
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_created)
            {
                _nid.uFlags = NIF_MESSAGE; // 只删除，不修改其他字段
                Shell_NotifyIcon(NIM_DELETE, ref _nid);
                _created = false;
            }

            if (_source != null)
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }

            // ExtractIconEx 返回的图标需 DestroyIcon；LR_SHARED 加载的不可销毁
            if (_hIcon != IntPtr.Zero && !_iconIsShared)
            {
                DestroyIcon(_hIcon);
            }
            _hIcon = IntPtr.Zero;

            _hwnd = IntPtr.Zero;
        }
    }
}