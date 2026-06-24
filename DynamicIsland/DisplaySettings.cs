using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;

namespace DynamicIsland
{
    /// <summary>
    /// 全局设置单例（INPC 让 UI 实时联动）。
    ///
    /// - 显示项：系统条 CPU/RAM/GPU/VRAM/NET 显隐
    /// - 阈值：CPU/RAM 点亮阈值（滞回退出阈值=点亮-10）
    /// - 提醒开关：电池/剪贴板提醒可关
    ///
    /// P6 持久化（2026-06-22）：所有字段读写 %AppData%\DynamicIsland\settings.json
    /// - 启动 App.OnStartup 调 Load()；属性变更 500ms debounce 自动落盘；进程退出 OnExit 调 SaveNow() 兜尾
    /// - 文件不存在/JSON 损坏/字段缺失 → 保留默认值，不抛
    /// </summary>
    public sealed class DisplaySettings : INotifyPropertyChanged
    {
        public static DisplaySettings Instance { get; } = new();

        // 与 error.log 同目录
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DynamicIsland");
        private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
        };

        // 显示项
        private bool _showCpu = true;
        private bool _showRam = true;
        private bool _showGpu = true;
        private bool _showVram = true;
        private bool _showNet = true;

        public bool ShowCpu { get => _showCpu; set => Set(ref _showCpu, value); }
        public bool ShowRam { get => _showRam; set => Set(ref _showRam, value); }
        public bool ShowGpu { get => _showGpu; set => Set(ref _showGpu, value); }
        public bool ShowVram { get => _showVram; set => Set(ref _showVram, value); }
        public bool ShowNet { get => _showNet; set => Set(ref _showNet, value); }

        // 阈值（点亮门槛，单位 %）。退出阈值 = 该值 - 10，由 SystemResourceSource 算。
        private double _cpuActivate = 70.0;
        private double _ramActivate = 80.0;

        public double CpuActivate { get => _cpuActivate; set => Set(ref _cpuActivate, value); }
        public double RamActivate { get => _ramActivate; set => Set(ref _ramActivate, value); }

        // 提醒开关
        private bool _enableBatteryAlert = true;
        private bool _enableClipboardAlert = true;
        private bool _enableUsbAlert = true;
        private bool _enableBluetoothAlert = true;
        private bool _enableNetworkAlert = true;
        private bool _enableDownloadAlert = true;

        public bool EnableBatteryAlert { get => _enableBatteryAlert; set => Set(ref _enableBatteryAlert, value); }
        public bool EnableClipboardAlert { get => _enableClipboardAlert; set => Set(ref _enableClipboardAlert, value); }
        public bool EnableUsbAlert { get => _enableUsbAlert; set => Set(ref _enableUsbAlert, value); }
        public bool EnableBluetoothAlert { get => _enableBluetoothAlert; set => Set(ref _enableBluetoothAlert, value); }
        public bool EnableNetworkAlert { get => _enableNetworkAlert; set => Set(ref _enableNetworkAlert, value); }
        public bool EnableDownloadAlert { get => _enableDownloadAlert; set => Set(ref _enableDownloadAlert, value); }

        // 下载提醒：监控的文件夹路径。空字符串=自动检测系统 Downloads 文件夹
        private string _downloadFolderPath = "";

        public string DownloadFolderPath { get => _downloadFolderPath; set => Set(ref _downloadFolderPath, value); }

        // 行为：检测到全屏应用（游戏/全屏视频）覆盖灵动岛所在屏时自动隐藏，
        // 留出完整视野；期间收到的提醒仍会短暂弹出展示完再隐藏。
        private bool _enableFullScreenSuppress = true;

        public bool EnableFullScreenSuppress { get => _enableFullScreenSuppress; set => Set(ref _enableFullScreenSuppress, value); }

        // 系统通知：把岛上的提醒同步发到 Windows 通知中心，用户不在岛前时（全屏/离开）可回看。
        private bool _enableSystemNotification = true;

        public bool EnableSystemNotification { get => _enableSystemNotification; set => Set(ref _enableSystemNotification, value); }

        // 显示位置：岛挂在哪一块显示器（按 MonitorEnumerator 重排后的索引，0=主屏）
        // 越界（拔屏、配置文件手改）由 MainWindow 兜底为主屏，不在这里钳。
        private int _targetMonitorIndex = 0;

        public int TargetMonitorIndex { get => _targetMonitorIndex; set => Set(ref _targetMonitorIndex, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        // 持久化状态
        private readonly DispatcherTimer _saveTimer;
        private bool _suspendSave;     // Load 期间屏蔽自动落盘
        private bool _loaded;          // Load 未跑过时不调度（避免 Instance 静态构造里 ctor 触发）

        private DisplaySettings()
        {
            // 500ms debounce：Slider 拖动连续触发不会刷盘
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            ScheduleSave();
        }

        private void ScheduleSave()
        {
            if (_suspendSave || !_loaded) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>
        /// 启动期调一次。文件不存在/损坏一律静默用默认值。
        /// </summary>
        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    Instance._loaded = true;
                    return;
                }
                var json = File.ReadAllText(SettingsPath);
                var snap = JsonSerializer.Deserialize<Snapshot>(json, JsonOpts);
                if (snap is not null)
                {
                    Instance._suspendSave = true;
                    Instance.ApplySnapshot(snap);
                    Instance._suspendSave = false;
                }
            }
            catch
            {
                // 损坏不阻塞启动，保留默认值
            }
            finally
            {
                Instance._loaded = true;
            }
        }

        /// <summary>
        /// 立即同步写盘。debounce 命中 / 进程退出兜尾各用一次。
        /// </summary>
        public static void SaveNow()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(Instance.CaptureSnapshot(), JsonOpts);
                // 原子写：临时文件 + 替换，避免崩在写一半留下损坏 JSON
                var tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, SettingsPath, overwrite: true);
            }
            catch
            {
                // I/O 失败不影响进程
            }
        }

        /// <summary>
        /// 全字段回滚到默认值，立即写盘。设置窗"恢复默认"按钮调用。
        /// 走 setter → INPC → 绑定的 UI 同步刷新；不屏蔽 debounce（用户主动行为，秒落盘也行），但直接 SaveNow 更保险。
        /// </summary>
        public static void ResetToDefaults()
        {
            Instance.ApplySnapshot(new Snapshot());
            // 主动调一次，不靠 debounce——用户期望"按下立即生效落盘"
            SaveNow();
        }

        private Snapshot CaptureSnapshot() => new()
        {
            ShowCpu = _showCpu,
            ShowRam = _showRam,
            ShowGpu = _showGpu,
            ShowVram = _showVram,
            ShowNet = _showNet,
            CpuActivate = _cpuActivate,
            RamActivate = _ramActivate,
            EnableBatteryAlert = _enableBatteryAlert,
            EnableClipboardAlert = _enableClipboardAlert,
            EnableUsbAlert = _enableUsbAlert,
            EnableBluetoothAlert = _enableBluetoothAlert,
            EnableNetworkAlert = _enableNetworkAlert,
            EnableDownloadAlert = _enableDownloadAlert,
            DownloadFolderPath = _downloadFolderPath,
            EnableFullScreenSuppress = _enableFullScreenSuppress,
            EnableSystemNotification = _enableSystemNotification,
            TargetMonitorIndex = _targetMonitorIndex,
        };

        private void ApplySnapshot(Snapshot s)
        {
            // 经公共 setter 走 Set()：INPC 通知能发出（但此刻通常还没人订阅），_suspendSave=true 屏蔽写盘
            ShowCpu = s.ShowCpu;
            ShowRam = s.ShowRam;
            ShowGpu = s.ShowGpu;
            ShowVram = s.ShowVram;
            ShowNet = s.ShowNet;
            // 阈值钳到合法区间防手改 JSON 坏值
            CpuActivate = Math.Clamp(s.CpuActivate, 20.0, 95.0);
            RamActivate = Math.Clamp(s.RamActivate, 30.0, 98.0);
            EnableBatteryAlert = s.EnableBatteryAlert;
            EnableClipboardAlert = s.EnableClipboardAlert;
            EnableUsbAlert = s.EnableUsbAlert;
            EnableBluetoothAlert = s.EnableBluetoothAlert;
            EnableNetworkAlert = s.EnableNetworkAlert;
            EnableDownloadAlert = s.EnableDownloadAlert;
            DownloadFolderPath = s.DownloadFolderPath ?? "";
            EnableFullScreenSuppress = s.EnableFullScreenSuppress;
            EnableSystemNotification = s.EnableSystemNotification;
            // 负数视为主屏；过大不在这里钳（不知道当前接几块屏），MainWindow 用时再兜底
            TargetMonitorIndex = Math.Max(0, s.TargetMonitorIndex);
        }

        /// <summary>
        /// 序列化快照（POCO，避免直接序列化 INPC 类型混进 Instance/Event 等字段）。
        /// 字段缺失时取默认值——加新字段时旧 settings.json 自动兼容。
        /// </summary>
        private sealed class Snapshot
        {
            public bool ShowCpu { get; set; } = true;
            public bool ShowRam { get; set; } = true;
            public bool ShowGpu { get; set; } = true;
            public bool ShowVram { get; set; } = true;
            public bool ShowNet { get; set; } = true;
            public double CpuActivate { get; set; } = 70.0;
            public double RamActivate { get; set; } = 80.0;
            public bool EnableBatteryAlert { get; set; } = true;
            public bool EnableClipboardAlert { get; set; } = true;
            public bool EnableUsbAlert { get; set; } = true;
            public bool EnableBluetoothAlert { get; set; } = true;
            public bool EnableNetworkAlert { get; set; } = true;
            public bool EnableDownloadAlert { get; set; } = true;
            public string? DownloadFolderPath { get; set; } = "";
            public bool EnableFullScreenSuppress { get; set; } = true;
            public bool EnableSystemNotification { get; set; } = true;
            public int TargetMonitorIndex { get; set; } = 0;
        }
    }
}
