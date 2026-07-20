using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

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
    /// <summary>灵动岛背景材质模式（AllowsTransparency=False + DWM 系统材质）。</summary>
    public enum BackdropMode
    {
        /// <summary>DWM Acrylic：模糊身后桌面，深色调（默认）。</summary>
        Acrylic,
        /// <summary>全透明：无 backdrop，直接看穿到桌面。白字浮于桌面，亮桌面下可读性差。</summary>
        Transparent,
        /// <summary>DWM Mica：平涂暗色无模糊（小窗偏闷）。</summary>
        Mica,
        /// <summary>液态玻璃：自渲染可调半径高斯模糊。排除截屏 + BitBlt 抓身后桌面 + HLSL 双遍高斯着色器（GPU ps_3_0），
        /// 比 DWM 亚克力更可控（半径/底色/帧率全可调）。CPU 开销在 BitBlt 抓屏与每帧 GDI 位图创建/销毁，不在模糊。窗口基座走 state2 透穿，玻璃叠在上面。</summary>
        LiquidGlass,
    }

    /// <summary>液态玻璃抓屏后端。Auto 默认（GPU 失败/连续空帧自动回退 Hlsl）；Gpu 强制 GPU；Hlsl 强制兼容模式。</summary>
    public enum CaptureMode
    {
        /// <summary>自动：优先 GPU（WinGC+D3D11+D3D9Ex 共享纹理），失败回退 Hlsl。</summary>
        Auto,
        /// <summary>强制 GPU 硬件加速（不回退，失败则无玻璃）。</summary>
        Gpu,
        /// <summary>强制 Hlsl 兼容模式（BitBlt+WPF ShaderEffect）。</summary>
        Hlsl,
    }

    /// <summary>灵动岛主题模式。System 跟随 Windows 个性化（默认）；Light/Dark 强制浅/深色。</summary>
    public enum ThemeMode
    {
        /// <summary>跟随系统：读 HKCU\...\Themes\Personalize\AppsUseLightTheme。</summary>
        System,
        /// <summary>强制浅色：深色文字 + 浅色材质底。</summary>
        Light,
        /// <summary>强制深色：白字 + 深色材质底。</summary>
        Dark,
    }

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

        // 背景材质：Acrylic 默认（DWM 模糊）/ Transparent 全透明 / Mica 平涂
        private BackdropMode _backdropMode = BackdropMode.Acrylic;
        public BackdropMode BackdropMode { get => _backdropMode; set => Set(ref _backdropMode, value); }

        // 亚克力模糊度（实为底色浓度 tint）：0–100，越大底色越浓。
        // 仅 Acrylic 模式生效；走 Win10 accent 路径的 GradientColor alpha，跨收起/展开态一致。
        // 默认 50：比旧固定底色淡，能稍微看清身后桌面。(模糊本身由 BlurEnabled 开关控，DWM 固定不可调。)
        private double _blurIntensity = 50.0;
        public double BlurIntensity { get => _blurIntensity; set => Set(ref _blurIntensity, Math.Clamp(value, 0.0, 100.0)); }

        // 亚克力模糊开关：开=state4 亚克力模糊，关=state2 锐利零模糊。仅 Acrylic 模式生效。
        // (state4 模糊量 DWM 固定，想"更低的模糊"只能关模糊落 state2。默认开。)
        private bool _blurEnabled = true;
        public bool BlurEnabled { get => _blurEnabled; set => Set(ref _blurEnabled, value); }

        // ── 液态玻璃：自渲染可调半径高斯模糊。仅 LiquidGlass 模式生效。
        // DWM 任何路径都不开放模糊半径，要可调半径只能自渲染（见 LiquidGlassRenderer）。
        // 半径：BlurEffect.Radius，0=无模糊（≈锐利看穿），越大越糊。默认 22（≈spike 验证值）。
        private double _glassBlurRadius = 22.0;
        public double GlassBlurRadius { get => _glassBlurRadius; set => Set(ref _glassBlurRadius, Math.Clamp(value, 0.0, 100.0)); }

        // 底色浓度：玻璃叠层底色 alpha（深=黑/浅=白），0≈无色全透，100=强底色。与半径独立。默认 40。
        private double _glassTintIntensity = 40.0;
        public double GlassTintIntensity { get => _glassTintIntensity; set => Set(ref _glassTintIntensity, Math.Clamp(value, 0.0, 100.0)); }

        // 金边：顶部高光 + 底部阴影 + 描边，增强玻璃质感。复用 MainWindow 现有 TopHighlight/BottomHighlight/BorderBrush。默认开。
        private bool _glassEdgeEnabled = true;
        public bool GlassEdgeEnabled { get => _glassEdgeEnabled; set => Set(ref _glassEdgeEnabled, value); }

        // 抓屏帧率：LiquidGlassRenderer 每秒抓身后桌面次数。越高越流畅越耗 CPU。
        // 注：模糊走 GPU（HLSL ShaderEffect ps_3_0），CPU 开销在 BitBlt 抓屏 + 每帧 GDI 位图创建/销毁，非 BlurEffect。默认 30。
        private int _glassCaptureFps = 30;
        private int _maxGlassCaptureFps = 144;

        public int GlassCaptureFps
        {
            get => _glassCaptureFps;
            set
            {
                int clamped = (int)Math.Clamp(value, 1, _maxGlassCaptureFps);
                if (_glassCaptureFps == clamped) return;
                _glassCaptureFps = clamped;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlassCaptureFps)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUltraMode)));
                ScheduleSave();
            }
        }

        /// <summary>滑块上限，跟随当前目标显示器的刷新率动态更新。默认 144。</summary>
        public int MaxGlassCaptureFps
        {
            get => _maxGlassCaptureFps;
            set
            {
                if (value <= 0) value = 144;
                if (value == _maxGlassCaptureFps) return;
                _maxGlassCaptureFps = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MaxGlassCaptureFps)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUltraMode)));
                ScheduleSave();
                if (_glassCaptureFps > _maxGlassCaptureFps)
                    GlassCaptureFps = _maxGlassCaptureFps;
            }
        }

        /// <summary>Ultra 模式：滑块拉到显示器刷新率顶。滑块彩虹始终生效；胶囊边框彩虹需另外开 UltraEffectEnabled。</summary>
        public bool IsUltraMode => _glassCaptureFps >= _maxGlassCaptureFps && _maxGlassCaptureFps >= 60;

        // "阳光彩虹小玻璃"：胶囊边框流动彩虹开关
        private bool _ultraEffectEnabled;
        public bool UltraEffectEnabled
        {
            get => _ultraEffectEnabled;
            set
            {
                if (_ultraEffectEnabled == value) return;
                _ultraEffectEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UltraEffectEnabled)));
                ScheduleSave();
            }
        }

        // 液态玻璃抓屏后端：Auto（GPU 优先，失败回退 Hlsl）/ Gpu / Hlsl。仅 LiquidGlass 模式生效。默认 Auto。
        private CaptureMode _captureMode = CaptureMode.Auto;
        public CaptureMode CaptureMode { get => _captureMode; set => Set(ref _captureMode, value); }

        // Cherry Studio 工作通知：轮询本地 Cherry API，agent 完成时提醒。默认关。
        private bool _enableCherryAlert;
        public bool EnableCherryAlert { get => _enableCherryAlert; set => Set(ref _enableCherryAlert, value); }

        // Cherry Studio API Key（Bearer，取自 Cherry Studio 设置）。空则不轮询。
        private string _cherryApiKey = "";
        public string CherryApiKey { get => _cherryApiKey; set => Set(ref _cherryApiKey, value ?? ""); }

        // 主题：System 跟随系统（默认）/ Light / Dark
        private ThemeMode _themeMode = ThemeMode.System;
        public ThemeMode ThemeMode { get => _themeMode; set => Set(ref _themeMode, value); }

        // 岛文字颜色：TextColorPresets 的索引。0=跟随主题（默认）。
        private int _textColorIndex = 0;
        public int TextColorIndex { get => _textColorIndex; set => Set(ref _textColorIndex, Math.Clamp(value, 0, TextColorPresets.Count - 1)); }

        public static readonly IReadOnlyList<TextColorPreset> TextColorPresets = new[]
        {
            new TextColorPreset("跟随主题", null),
            new TextColorPreset("纯白", Colors.White),
            new TextColorPreset("纯黑", Colors.Black),
            new TextColorPreset("浅灰", Color.FromRgb(0xAA, 0xAA, 0xAA)),
            new TextColorPreset("强调蓝", Color.FromRgb(0x4C, 0xC2, 0xFF)),
        };

        public readonly record struct TextColorPreset(string Name, Color? Color);

        // Pro 模式：启用实验性功能（DXGI 硬件加速玻璃等）。默认关。
        private bool _proMode = false;
        public bool ProMode { get => _proMode; set => Set(ref _proMode, value); }

        // 抽纸动画起始缩放：0=从一点出，1=不缩放。默认 0.6（缩到六成再放大）。
        private double _tissueStartScale = 0.6;
        public double TissueStartScale { get => _tissueStartScale; set => Set(ref _tissueStartScale, Math.Clamp(value, 0.3, 1.0)); }

        // 动画曲线：MotionToken.CurvePresets 的索引。0=Quadratic（默认），改后即时生效。
        private int _motionCurveIndex = 0;
        public int MotionCurveIndex
        {
            get => _motionCurveIndex;
            set
            {
                int clamped = Math.Clamp(value, 0, MotionToken.CurvePresets.Count - 1);
                var old = _motionCurveIndex;
                Set(ref _motionCurveIndex, clamped);
                if (_motionCurveIndex != old)
                    MotionToken.SetActiveCurve(MotionToken.CurvePresets[clamped]);
            }
        }

        /// <summary>解析当前应当使用的主题明暗：System→读注册表 AppsUseLightTheme；Light→true；Dark→false。
        /// 集中在此供主岛与设置窗共用。注：用全限定 DynamicIsland.ThemeMode，避开 net10 WPF Window.ThemeMode 预览属性的同名遮蔽。</summary>
        public bool IsLight()
        {
            return ThemeMode switch
            {
                DynamicIsland.ThemeMode.Light => true,
                DynamicIsland.ThemeMode.Dark => false,
                _ => IsWindowsLightTheme(), // System
            };
        }

        /// <summary>读 HKCU\...\Themes\Personalize\AppsUseLightTheme。读不到按深色（false）。</summary>
        private static bool IsWindowsLightTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int val)
                    return val == 1;
            }
            catch { }
            return false;
        }

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
            EnableCherryAlert = _enableCherryAlert,
            CherryApiKey = _cherryApiKey,
            DownloadFolderPath = _downloadFolderPath,
            EnableFullScreenSuppress = _enableFullScreenSuppress,
            EnableSystemNotification = _enableSystemNotification,
            TargetMonitorIndex = _targetMonitorIndex,
            BackdropMode = _backdropMode,
            BlurIntensity = _blurIntensity,
            BlurEnabled = _blurEnabled,
            GlassBlurRadius = _glassBlurRadius,
            GlassTintIntensity = _glassTintIntensity,
            GlassEdgeEnabled = _glassEdgeEnabled,
            GlassCaptureFps = _glassCaptureFps,
            MaxGlassCaptureFps = _maxGlassCaptureFps,
            MotionCurveIndex = _motionCurveIndex,
            TextColorIndex = _textColorIndex,
            ProMode = _proMode,
            TissueStartScale = _tissueStartScale,
            ThemeMode = _themeMode,
            UltraEffectEnabled = _ultraEffectEnabled,
            CaptureMode = _captureMode,
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
            EnableCherryAlert = s.EnableCherryAlert;
            CherryApiKey = s.CherryApiKey ?? "";
            DownloadFolderPath = s.DownloadFolderPath ?? "";
            EnableFullScreenSuppress = s.EnableFullScreenSuppress;
            EnableSystemNotification = s.EnableSystemNotification;
            // 负数视为主屏；过大不在这里钳（不知道当前接几块屏），MainWindow 用时再兜底
            TargetMonitorIndex = Math.Max(0, s.TargetMonitorIndex);
            // 手改 JSON 写了非法枚举值时回默认，免得 UI 下标越界
            BackdropMode = Enum.IsDefined(typeof(BackdropMode), s.BackdropMode) ? s.BackdropMode : BackdropMode.Acrylic;
            BlurIntensity = Math.Clamp(s.BlurIntensity, 0.0, 100.0);
            BlurEnabled = s.BlurEnabled;
            GlassBlurRadius = Math.Clamp(s.GlassBlurRadius, 0.0, 100.0);
            GlassTintIntensity = Math.Clamp(s.GlassTintIntensity, 0.0, 100.0);
            GlassEdgeEnabled = s.GlassEdgeEnabled;
            GlassCaptureFps = (int)Math.Clamp(s.GlassCaptureFps, 1, 360);
            MaxGlassCaptureFps = s.MaxGlassCaptureFps > 0 ? s.MaxGlassCaptureFps : 144;
            MotionCurveIndex = Math.Clamp(s.MotionCurveIndex, 0, MotionToken.CurvePresets.Count - 1);
            TextColorIndex = Math.Clamp(s.TextColorIndex, 0, TextColorPresets.Count - 1);
            ProMode = s.ProMode;
            TissueStartScale = Math.Clamp(s.TissueStartScale, 0.3, 1.0);
            ThemeMode = Enum.IsDefined(typeof(ThemeMode), s.ThemeMode) ? s.ThemeMode : ThemeMode.System;
            UltraEffectEnabled = s.UltraEffectEnabled;
            CaptureMode = Enum.IsDefined(typeof(CaptureMode), s.CaptureMode) ? s.CaptureMode : CaptureMode.Auto;
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
            public bool EnableCherryAlert { get; set; } = false;
            public string? CherryApiKey { get; set; } = "";
            public string? DownloadFolderPath { get; set; } = "";
            public bool EnableFullScreenSuppress { get; set; } = true;
            public bool EnableSystemNotification { get; set; } = true;
            public int TargetMonitorIndex { get; set; } = 0;
            public BackdropMode BackdropMode { get; set; } = BackdropMode.Acrylic;
            public double BlurIntensity { get; set; } = 50.0;
            public bool BlurEnabled { get; set; } = true;
            public double GlassBlurRadius { get; set; } = 22.0;
            public double GlassTintIntensity { get; set; } = 40.0;
            public bool GlassEdgeEnabled { get; set; } = true;
            public int GlassCaptureFps { get; set; } = 30;
            public int MaxGlassCaptureFps { get; set; } = 144;
            public int MotionCurveIndex { get; set; } = 0;
            public bool ProMode { get; set; } = false;
            public int TextColorIndex { get; set; } = 0;
            public double TissueStartScale { get; set; } = 0.6;
            public ThemeMode ThemeMode { get; set; } = ThemeMode.System;
            public bool UltraEffectEnabled { get; set; }
            public CaptureMode CaptureMode { get; set; } = CaptureMode.Auto;
        }
    }
}
