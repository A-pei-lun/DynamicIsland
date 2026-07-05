using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DynamicIslandPro.Island;

namespace DynamicIslandPro.Alerts
{
    /// <summary>
    /// USB / 可移动磁盘提醒源：监听 WM_DEVICECHANGE → DBT_DEVTYP_VOLUME，
    /// 在 U 盘、移动硬盘等可移动磁盘连接 / 断开时投递提醒。
    ///
    /// 实现要点：
    /// - WM_DEVICECHANGE 是广播消息，所有顶层窗口都能收到，无需 RegisterDeviceNotification。
    /// - 通过 HwndSource.AddHook 在 MainWindow 的消息泵里挂钩。
    /// - dbcv_unitmask 是 32 位掩码，每一位代表一个盘符（bit 0=A, bit 1=B, …, bit 25=Z）。
    /// - 卷类型判断（可移动 / 固定）用 P/Invoke GetDriveType——避免把外接 HDD 当作系统盘报。
    /// - 防刷屏：插一根 U 盘可能含多分区导致多 bit 同时亮，逐分区上报反而合理（用户看到 E:/F: 分别提醒）。
    ///   若以后嫌啰嗦，可以加 100ms 合并窗口。
    /// </summary>
    public sealed class UsbAlertSource : IDisposable
    {
        private readonly AlertHost _host;
        private HwndSource? _source;
        private IntPtr _hwnd;
        private bool _attached;

        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const int DBT_DEVTYP_VOLUME = 0x00000002;

        private const int DRIVE_REMOVABLE = 2;
        private const int DRIVE_FIXED = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct DEV_BROADCAST_HDR
        {
            public int dbch_size;
            public int dbch_devicetype;
            public int dbch_reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEV_BROADCAST_VOLUME
        {
            public int dbcv_size;
            public int dbcv_devicetype;
            public int dbcv_reserved;
            public uint dbcv_unitmask;
            public ushort dbcv_flags;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetDriveType(string lpRootPathName);

        public UsbAlertSource(AlertHost host) => _host = host;

        /// <summary>挂到主窗口的消息泵。WM_DEVICECHANGE 是广播，主窗口直接能收到。</summary>
        public void Attach(Window window)
        {
            if (_attached) return;
            _source = PresentationSource.FromVisual(window) as HwndSource
                      ?? new HwndSource(new HwndSourceParameters("di-usb") { Width = 0, Height = 0 });
            _hwnd = _source.Handle;
            _source.AddHook(WndProc);
            _attached = true;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_DEVICECHANGE) return IntPtr.Zero;
            if (!DisplaySettings.Instance.EnableUsbAlert) return IntPtr.Zero;

            int evt = wParam.ToInt32();
            if (evt != DBT_DEVICEARRIVAL && evt != DBT_DEVICEREMOVECOMPLETE) return IntPtr.Zero;
            if (lParam == IntPtr.Zero) return IntPtr.Zero;

            var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
            if (hdr.dbch_devicetype != DBT_DEVTYP_VOLUME) return IntPtr.Zero;

            var vol = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);
            // unitmask bit i = 盘符 'A'+i
            for (int i = 0; i < 26; i++)
            {
                if ((vol.dbcv_unitmask & (1u << i)) == 0) continue;

                char letter = (char)('A' + i);
                string root = $"{letter}:\\";
                int dtype = GetDriveType(root);

                // 仅可移动盘报警；固定盘（系统盘 / 内置 HDD）忽略
                if (dtype != DRIVE_REMOVABLE) continue;

                if (evt == DBT_DEVICEARRIVAL)
                    PostInsertAlert(letter, root);
                else
                    PostRemoveAlert(letter);
            }

            return IntPtr.Zero;
        }

        private void PostInsertAlert(char letter, string root)
        {
            // 卷标读取可能失败（盘没就绪 / 受保护），失败也无所谓——主标题已足够提示
            string subtitle;
            try
            {
                var di = new DriveInfo(letter.ToString());
                if (di.IsReady && !string.IsNullOrWhiteSpace(di.VolumeLabel))
                    subtitle = $"{letter}: · {di.VolumeLabel}";
                else
                    subtitle = $"{letter}:";
            }
            catch
            {
                subtitle = $"{letter}:";
            }

            _host.Enqueue(new SimpleAlert(
                $"usb.in.{letter}", "U 盘已连接", subtitle, "💾",
                TimeSpan.FromSeconds(2.5), priority: 35));
        }

        private void PostRemoveAlert(char letter)
        {
            _host.Enqueue(new SimpleAlert(
                $"usb.out.{letter}", "U 盘已断开", $"{letter}:", "💾",
                TimeSpan.FromSeconds(2.5), priority: 35));
        }

        public void Dispose()
        {
            if (_attached)
            {
                _source?.RemoveHook(WndProc);
                _attached = false;
            }
            _source = null;
        }
    }
}
