using System;
using System.Runtime.InteropServices;
using DynamicIsland.Island;
using Microsoft.Win32;

namespace DynamicIsland.Alerts
{
    /// <summary>
    /// 电量提醒源：订阅 <see cref="SystemEvents.PowerModeChanged"/>（PowerStatusChange），
    /// 在充电接入 / 拔下 / 电量过低等状态转换时向 <see cref="AlertHost"/> 投递一次性提醒。
    ///
    /// 电量数据用 kernel32 GetSystemPowerStatus P/Invoke 读取（WPF 无 SystemInformation，
    /// 这样不引入 WinForms 依赖，与 SystemResourceSource 的 P/Invoke 风格一致）。
    ///
    /// 防刷屏：PowerModeChanged 对同一次插拔可能触发多次，这里只跟踪 _wasOnPower / _wasLow
    /// 两个布尔，仅在真实转换时投递，并对低电量用滞回（≤20% 触发、≥30% 复位）。
    /// 无电池的台式机直接不订阅。
    /// </summary>
    public sealed class BatteryAlertSource
    {
        private readonly AlertHost _host;
        private bool _started;
        private bool _hasBattery;
        private bool _wasOnPower;
        private bool _wasLow;

        private const double LowThreshold = 20.0;
        private const double LowRecover = 30.0;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS sps);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;        // 0=离线 1=在线 255=未知
            public byte BatteryFlag;         // 位掩码：1=高 2=低 4=临界 8=充电中 128=无电池 255=未知
            public byte BatteryLifePercent;  // 0~100，255=未知
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        public BatteryAlertSource(AlertHost host)
        {
            _host = host;
        }

        public void Start()
        {
            if (_started) return;
            var sps = new SYSTEM_POWER_STATUS();
            if (!GetSystemPowerStatus(ref sps)) return;

            _hasBattery = (sps.BatteryFlag & 0x80) == 0 && sps.BatteryFlag != 255;
            if (!_hasBattery) return; // 台式机无电池，什么都不做

            _started = true;
            // 记录初始状态，避免启动即误报"正在充电"
            _wasOnPower = sps.ACLineStatus == 1;
            _wasLow = ToPercent(sps.BatteryLifePercent) <= LowThreshold;

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.StatusChange) return;
            if (!DisplaySettings.Instance.EnableBatteryAlert) return; // 设置面板可关

            var sps = new SYSTEM_POWER_STATUS();
            if (!GetSystemPowerStatus(ref sps)) return;

            double pct = ToPercent(sps.BatteryLifePercent);
            string pctText = $"{pct:0}%";
            bool onPower = sps.ACLineStatus == 1;

            // 充电接入
            if (onPower && !_wasOnPower)
            {
                _host.Enqueue(new SimpleAlert(
                    "battery.charging", "充电中", pctText, "⚡", TimeSpan.FromSeconds(2.5), priority: 50));
            }
            // 拔下电源（仅在电量尚可时提醒，低电量走低电量分支）
            else if (!onPower && _wasOnPower && pct > LowThreshold)
            {
                _host.Enqueue(new SimpleAlert(
                    "battery.unplugged", "已拔下电源", pctText, "🔋", TimeSpan.FromSeconds(2.5), priority: 30));
            }

            // 低电量（滞回）：仅电池供电且跌破阈值。重要档 4s，用户要留神。
            if (!onPower)
            {
                if (!_wasLow && pct <= LowThreshold)
                {
                    _wasLow = true;
                    _host.Enqueue(new SimpleAlert(
                        "battery.low", "电量低", pctText, "🔋", TimeSpan.FromSeconds(4.0), priority: 80));
                }
                else if (_wasLow && pct >= LowRecover)
                {
                    _wasLow = false;
                }
            }

            _wasOnPower = onPower;
        }

        private static double ToPercent(byte raw)
            => raw == 255 ? 0 : Math.Clamp((int)raw, 0, 100);
    }
}
