using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DynamicIsland.Island;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Application = System.Windows.Application;

namespace DynamicIsland.Alerts
{
    /// <summary>
    /// 蓝牙设备提醒源：用 WinRT <see cref="DeviceWatcher"/> 监听已配对蓝牙设备的连接/断开。
    ///
    /// 同时运行 Classic (BR/EDR) 和 LE (Low Energy) 两个 watcher，
    /// 各自按配对状态过滤——已配对才算"用户关心的设备"。
    ///
    /// 实现要点：
    /// - 初始枚举期间（启动时已连接的所有设备）不报警，避免启动就刷屏。
    /// - 枚举完成后，后续 Added/Removed 才是真正的新连接/断开事件。
    /// - Removed 事件只带 Id 不带 Name，所以维护一个 name cache（Added 时写入）。
    /// - AlertHost.Enqueue 自带 Dispatcher 调度，任意线程回调均可安全调用。
    /// - 注意：若用户关掉蓝牙，所有已连接设备一起断开，会触发若干条 Removed。
    ///   这是合理行为——用户看到"耳机已断开"就知道蓝牙没了。
    /// </summary>
    public sealed class BluetoothAlertSource : IDisposable
    {
        private readonly AlertHost _host;
        private DeviceWatcher? _classicWatcher;
        private DeviceWatcher? _leWatcher;
        private bool _started;

        // 枚举阶段：跟踪当前已连接的设备 ID，避免后续 Removed 报警无名字
        private readonly HashSet<string> _connectedIds = new(128);
        private readonly Dictionary<string, string> _nameCache = new(128); // id → name

        // 枚举计数器：两个 watcher 各自触发一次 EnumerationCompleted
        // 只有两个都完成才算 _fullyEnumerated
        private int _enumerationCompletedCount;
        private bool _fullyEnumerated;

        private const string IsConnectedKey = "System.Devices.Aep.IsConnected";

        // 支持 BT 属性请求的额外属性列表（字符串数组）
        private static readonly string[] RequestedProperties = { IsConnectedKey };

        public BluetoothAlertSource(AlertHost host) => _host = host;

        /// <summary>启动两个 watcher（Classic + LE）。蓝牙不可用 / 不支持时静默退化。</summary>
        public void Start()
        {
            if (_started) return;
            _started = true;

            try
            {
                // 经典蓝牙 (BR/EDR)：音箱、耳机、键盘等
                var classicSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
                _classicWatcher = DeviceInformation.CreateWatcher(
                    classicSelector,
                    RequestedProperties,
                    DeviceInformationKind.AssociationEndpoint);
                _classicWatcher.Added += OnDeviceAdded;
                _classicWatcher.Removed += OnDeviceRemoved;
                _classicWatcher.EnumerationCompleted += OnEnumerationCompleted;
                _classicWatcher.Start();
            }
            catch
            {
                // 蓝牙不可用——静默，不阻塞启动
            }

            try
            {
                // 蓝牙 LE：鼠标、键盘、手环等
                var leSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                _leWatcher = DeviceInformation.CreateWatcher(
                    leSelector,
                    RequestedProperties,
                    DeviceInformationKind.AssociationEndpoint);
                _leWatcher.Added += OnDeviceAdded;
                _leWatcher.Removed += OnDeviceRemoved;
                _leWatcher.EnumerationCompleted += OnEnumerationCompleted;
                _leWatcher.Start();
            }
            catch
            {
                // 同上
            }
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;

            RemoveWatcher(ref _classicWatcher);
            RemoveWatcher(ref _leWatcher);

            _connectedIds.Clear();
            _nameCache.Clear();
            _enumerationCompletedCount = 0;
            _fullyEnumerated = false;
        }

        private static void RemoveWatcher(ref DeviceWatcher? watcher)
        {
            if (watcher == null) return;
            try
            {
                watcher.Stop();
            }
            catch
            {
                // 允许从一个已停止的 watcher 再次 Stop（不抛异常）
            }
            watcher = null;
        }

        // ─── 回调 ───────────────────────────────────────────────────

        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation info)
        {
            if (!DisplaySettings.Instance.EnableBluetoothAlert) return;
            if (string.IsNullOrEmpty(info.Name)) return;

            // 缓存名字，为 Removed（只有 Id）准备
            lock (_nameCache)
            {
                _nameCache[info.Id] = info.Name;
            }

            bool isConnected = ReadIsConnected(info.Properties);
            if (!isConnected) return; // 已配对但未连接，忽略

            bool isNew;
            lock (_connectedIds)
            {
                isNew = _connectedIds.Add(info.Id);
            }

            // 枚举完成后才报警——避免启动时已有的设备全弹一遍
            if (!_fullyEnumerated || !isNew) return;

            _host.Enqueue(new SimpleAlert(
                $"bt.{info.Id}",
                $"{info.Name} 已连接",
                "蓝牙设备",
                IsAudioDevice(info) ? "🎧" : "⌨",
                TimeSpan.FromSeconds(2.5),
                priority: 30, kind: AlertKind.Summary));
        }

        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate info)
        {
            if (!DisplaySettings.Instance.EnableBluetoothAlert) return;

            bool wasConnected;
            lock (_connectedIds)
            {
                wasConnected = _connectedIds.Remove(info.Id);
            }

            if (!_fullyEnumerated || !wasConnected) return;

            string? name;
            lock (_nameCache)
            {
                _nameCache.TryGetValue(info.Id, out name);
                _nameCache.Remove(info.Id);
            }

            _host.Enqueue(new SimpleAlert(
                $"bt.{info.Id}",
                $"{name ?? "蓝牙设备"} 已断开",
                "蓝牙设备",
                "🔌",
                TimeSpan.FromSeconds(2.5),
                priority: 30, kind: AlertKind.Summary));
        }

        private void OnEnumerationCompleted(DeviceWatcher sender, object args)
        {
            _enumerationCompletedCount++;
            // 两个 watcher 都完成后才算完整枚举
            if (_enumerationCompletedCount >= 2)
                _fullyEnumerated = true;
        }

        // ─── 辅助 ───────────────────────────────────────────────────

        private static bool ReadIsConnected(IReadOnlyDictionary<string, object> props)
        {
            if (props.TryGetValue(IsConnectedKey, out var val) && val is bool b)
                return b;
            return false;
        }

        /// <summary>粗略判断设备是否为音频类（耳机/音箱）。用于选 emoji 图标。</summary>
        private static bool IsAudioDevice(DeviceInformation info)
        {
            // 蓝牙设备类别位编码：Class of Device (CoD)
            // Major Service = Audio (CoD & 0x200000) 或 Major Device = Audio/Video (CoD & 0x1F00 = 0x400)
            // 此处用最宽泛的启发式：名字含常见耳机/音箱关键词
            if (string.IsNullOrEmpty(info.Name)) return false;
            var n = info.Name.ToLowerInvariant();
            return n.Contains("airpods") || n.Contains("buds") || n.Contains("headphone")
                || n.Contains("earphone") || n.Contains("speaker") || n.Contains("earbud")
                || n.Contains("beat") || n.Contains("bose") || n.Contains("sony")
                || n.Contains("jbl") || n.Contains("sennheiser") || n.Contains("soundcore")
                || n.Contains("galaxy buds") || n.Contains("pixel buds");
        }

        public void Dispose()
        {
            Stop();
        }
    }
}