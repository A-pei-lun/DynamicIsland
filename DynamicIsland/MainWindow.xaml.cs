using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Alerts;
using DynamicIsland.Island;
using DynamicIsland.Sources;
using Microsoft.Win32;

namespace DynamicIsland
{
    public partial class MainWindow : Window
    {
        // ─── 尺寸（基准，按 1920×1080 1:1 设计；其它分辨率按 _scale 缩放）───
        // 所有固定尺寸集中在 IslandLayout 记录中。
        private const double TopMargin = 12;

        // ─── 当前布局快照（单一真相源）──────────────────────────────
        private IslandLayout _currentLayout = IslandLayout.Collapsed;

        // ─── 状态机 ────────────────────────────────────────────────
        private enum DisplayState { Collapsed, Hovered, Expanded }
        private DisplayState _state = DisplayState.Collapsed;
        // Alert 不占状态机：Collapsed/Hovered=内联替换，Expanded=静默入历史。
        // 记录 alert 出现前的状态，dismiss 后恢复。
        private DisplayState? _preAlertState;

        // ─── 渐隐收回序列 ──────────────────────────────────────────
        private enum ShrinkPhase { None, ToHover, ToCollapsed }
        private ShrinkPhase _shrinkPhase = ShrinkPhase.None;
        private readonly DispatcherTimer _shrinkTimer;
        private bool _suppressLeaveCollapse;

        // ─── 数据源 ────────────────────────────────────────────────
        private readonly IslandHost _host;
        private readonly MediaSource _media;
        private readonly SystemResourceSource _system;
        private readonly WeatherSource _weather;
        private readonly IslandDashboard _dashboard;

        // ─── 瞬时提醒 ──────────────────────────────────────────────
        private readonly AlertHost _alerts = new();
        private readonly AlertStats _stats = new();
        private readonly SystemNotifier _notifier = new();
        private AlertAction? _inlineAlertAction; // 内联消息当前的动作按钮（点击时执行+关闭）
        private readonly BatteryAlertSource _battery;
        private readonly ClipboardAlertSource _clipboard;
        private readonly UsbAlertSource _usb;
        private readonly BluetoothAlertSource _bluetooth;
        private readonly NetworkAlertSource _network;
        private readonly DownloadAlertSource _download;
        private readonly CherryAlertSource _cherry;

        // ─── 托盘图标 ──────────────────────────────────────────────
        private readonly TrayIcon _tray;

        // ─── 全屏抑制 ──────────────────────────────────────────────
        private readonly FullScreenDetector _fullScreen;
        // 当前是否处于「全屏抑制」隐藏态。仅在该标志翻转时播抽纸动画，避免每次回灌重复触发。
        private bool _isSuppressed;
        private DispatcherTimer? _idleTimer;
        private bool _sleeping;
        private static readonly TimeSpan SleepIdleTimeout = TimeSpan.FromMinutes(5);

        // Hover 动效专用的 BorderBrush（Color 不含 alpha，Opacity 控制透明度）
        private readonly SolidColorBrush _hoverBorderBrush = new(Colors.White) { Opacity = 0.2 };

        // 动画版本号：用于 DelayedInvoke 取消已过时的延迟步骤
        private int _animationToken;

        // ─── 分辨率缩放 ────────────────────────────────────────────
        private double _scale = 1.0;

        // ─── 目标显示器 ────────────────────────────────────────────
        private MonitorInfo _targetMonitor = null!;

        // ─── 液态玻璃自渲染器（仅 LiquidGlass 模式启停）──
        private LiquidGlassRenderer? _glass;

        /// <summary>主窗单例，供设置窗查询运行时状态（如实际玻璃后端名）。</summary>
        public static MainWindow? Instance { get; private set; }

        /// <summary>当前实际生效的玻璃后端名（Auto 模式可能回退到 Hlsl）。</summary>
        public string GlassBackendName => _glass?.BackendName ?? "未启动";

        // ─── Win32 API ─────────────────────────────────────────────
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int style);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_BORDER = 0x00800000;
        private const int WS_DLGFRAME = 0x00400000;

        // ─── 主题色 ────────────────────────────────────────────────
        private readonly Color _lightBorder = Color.FromArgb(0x28, 0x00, 0x00, 0x00);
        private readonly Color _lightText = Colors.Black;
        private readonly Color _lightHighlight = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
        private readonly Color _lightHighlightEnd = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);

        private readonly Color _darkBorder = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        private readonly Color _darkText = Colors.White;
        private readonly Color _darkHighlight = Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF);
        private readonly Color _darkHighlightEnd = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);


        public MainWindow()
        {
            InitializeComponent();
            Instance = this;
            _glass = new LiquidGlassRenderer(this, GlassCaptureH, GlassCapture, GlassD3DImage, GlassTint);

            _host = new IslandHost();
            _host.Register(_media = new MediaSource());
            _host.Register(_system = new SystemResourceSource());
            _host.Register(new ClockSource());
            _weather = new WeatherSource();
            _host.Register(_weather);

            IIslandPanel[] panels = { new MediaPanel(_media), new NotificationPanel(_alerts), new WeatherPanel(_weather), new StatsPanel(_stats) };
            _dashboard = new IslandDashboard(panels, _system);

            _stats.Load();
            _alerts.Stats = _stats;
            _alerts.Notifier = _notifier;
            _battery = new BatteryAlertSource(_alerts);
            _clipboard = new ClipboardAlertSource(_alerts);
            _usb = new UsbAlertSource(_alerts);
            _bluetooth = new BluetoothAlertSource(_alerts);
            _network = new NetworkAlertSource(_alerts);
            _download = new DownloadAlertSource(_alerts);
            _cherry = new CherryAlertSource(_alerts);
            _alerts.CurrentChanged += OnAlertChanged;

            _tray = new TrayIcon();
            _tray.SettingsRequested += (_, _) => Dispatcher.Invoke(() => OpenSettings_Click(this, new RoutedEventArgs()));
            _tray.TestAlertRequested += (_, _) => Dispatcher.Invoke(() => TestAlert_Click(this, new RoutedEventArgs()));
            _tray.ExitRequested += (_, _) => Dispatcher.BeginInvoke(new Action(Close));

            _fullScreen = new FullScreenDetector(() => _targetMonitor?.PhysicalBounds ?? Rect.Empty);
            _fullScreen.IsFullScreenChanged += OnFullScreenChanged;

            RefreshTargetMonitor();
            ComputeScale();
            ApplyScale();
            _host.Updated += OnHostUpdated;

            UpdateWindowTitle();
            DisplaySettings.Instance.PropertyChanged += OnDisplaySettingsChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsTopologyChanged;

            _shrinkTimer = new DispatcherTimer();
            _shrinkTimer.Tick += OnShrinkTick;

            Loaded += OnLoaded;
            Closed += OnClosed;
            Deactivated += OnDeactivated;
        }

        // ─── 生命周期 ──────────────────────────────────────────────
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // AllowsTransparency=False 下合成目标默认不透明（黑），置透明让 DWM backdrop 透出
            if (PresentationSource.FromVisual(this) is HwndSource src)
            {
                if (src.CompositionTarget != null)
                    src.CompositionTarget.BackgroundColor = Colors.Transparent;
                src.AddHook(WndProc); // P2: 接 WM_DPCHANGED，DPI 变时重算 _scale + 重布局 + 玻璃重抓
            }
            ApplyBackdrop();
        }

        // ─── P2: DPI 变更钩子（多显示器混合 DPI / DPI 滑块变化时系统发此消息）──
        private const int WM_DPCHANGED = 0x02E0;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DPCHANGED)
            {
                // 不设 handled=true：让 WPF 照常处理（自动重缩放视觉树），我们只额外排队刷新
                // _scale 与玻璃。丢到 Dispatcher 队尾，避免和当前动画帧/布局 pass 打架。
                Dispatcher.BeginInvoke(HandleDpiChanged);
            }
            return IntPtr.Zero;
        }

        private void HandleDpiChanged()
        {
            // 复用 MoveToCurrentMonitor：RefreshTargetMonitor（重新枚举，P1 后每屏 DPI 正确）
            // + ComputeScale（新 Bounds.Height -> 新 _scale）+ ApplyScale + 按当前 _state 重布局。
            // 玻璃下帧 Tick 按新窗口尺寸重抓，UpdateSettings 促其刷新 texel。
            MoveToCurrentMonitor();
            _glass?.UpdateSettings();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RemoveFrame();
            ApplySystemTheme();
            SystemEvents.UserPreferenceChanged += OnThemeChanged;

            _host.Start();
            _battery.Start();
            _clipboard.Attach(this);
            _usb.Attach(this);
            _bluetooth.Start();
            _network.Start();
            _download.Start();
            _cherry.Start();

            _fullScreen.Start(new WindowInteropHelper(this).Handle);

            // Ultra 彩虹光效：如果设置里帧率已拉到顶，启动就开
            ApplyUltraEffect();

            // Sleep 省电：5min 无交互（hover/click/alert/expand）降玻璃帧率 + 微暗，不变尺寸。
            _idleTimer = new DispatcherTimer { Interval = SleepIdleTimeout };
            _idleTimer.Tick += (_, _) => EnterSleep();
            _idleTimer.Start();
            MouseMove += (_, _) => ResetIdle();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            SystemEvents.UserPreferenceChanged -= OnThemeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsTopologyChanged;
            DisplaySettings.Instance.PropertyChanged -= OnDisplaySettingsChanged;
            _host.Updated -= OnHostUpdated;
            _host.Dispose();
            _battery.Stop();
            _clipboard.Dispose();
            _usb.Dispose();
            _bluetooth.Dispose();
            _network.Dispose();
            _download.Dispose();
            _cherry.Dispose();
            _fullScreen.IsFullScreenChanged -= OnFullScreenChanged;
            _fullScreen.Dispose();
            _alerts.CurrentChanged -= OnAlertChanged;
            _alerts.Dispose();
            _stats.Dispose();
            _notifier.Dispose();
            _tray.Dispose();
            _shrinkTimer.Stop();
            _shrinkTimer.Tick -= OnShrinkTick;
            _idleTimer?.Stop();
            _glass?.Stop();
            // Ultra 彩虹：清理定时器防泄漏
            if (_ultraTimer != null)
            {
                _ultraTimer.Stop();
                _ultraTimer.Tick -= OnUltraTick;
                _ultraTimer = null;
                _ultraActive = false;
            }
        }

        private void OnDeactivated(object? sender, EventArgs e)
        {
            if (_suppressLeaveCollapse) return;

            if (_alerts.Current != null)
                _alerts.DismissCurrent();
            if (_state != DisplayState.Collapsed)
            {
                CancelShrink();
                CollapseTo(DisplayState.Collapsed);
            }
        }

        private void RemoveFrame()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int style = GetWindowLong(hwnd, GWL_STYLE);
                style &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME);
                SetWindowLong(hwnd, GWL_STYLE, style);
            }
            catch { }
        }

        // ─── DWM 背景材质 ──────────────────────────────────────────
        /// <summary>
        /// 应用系统材质（Acrylic/Mica/全透明）+ 系统圆角 + 深色模式。幂等，切换模式/主题时重调。
        /// 前提：OnSourceInitialized 已把 CompositionTarget 置透明、AllowsTransparency=False。
        /// </summary>
        private void ApplyBackdrop()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            // 主题 + 模糊均来自设置：System 主题读注册表；Acrylic 模糊开关 + 底色浓度(见 WindowBackdrop)
            var mode = DisplaySettings.Instance.BackdropMode;
            bool isLight = DisplaySettings.Instance.IsLight();
            WindowBackdrop.Apply(hwnd, mode,
                isDark: !isLight,
                blurEnabled: DisplaySettings.Instance.BlurEnabled,
                tintIntensity: DisplaySettings.Instance.BlurIntensity);

            // Mica：DWM 在 borderless 窗口上渲染纯黑（WindowBackdrop 内已不挂 backdrop），由 GlassBorder 平涂实色兜底。
            // LiquidGlass：基座已 state2 透穿，GlassBorder 透明让自渲染层显出，启渲染器（LiquidGlassRenderer 抓身后桌面+模糊）。
            // Acrylic/Transparent：GlassBorder 透明让 accent 透出。
            if (mode == BackdropMode.Mica)
            {
                _glass?.Stop();
                GlassBorder.Background = new SolidColorBrush(isLight ? Color.FromRgb(0xE8, 0xE8, 0xE8) : Color.FromRgb(0x1A, 0x1A, 0x1A));
            }
            else if (mode == BackdropMode.LiquidGlass)
            {
                GlassBorder.Background = Brushes.Transparent;
                _glass?.Start(hwnd);
            }
            else
            {
                _glass?.Stop();
                GlassBorder.Background = Brushes.Transparent;
            }

            ApplyGlassEdge();
        }

        /// <summary>
        /// 液态玻璃金边显隐：仅 LiquidGlass 模式且 GlassEdgeEnabled=关 时藏顶/底高光 + 去边框；
        /// 其余情况恢复可见 + 主题化悬停边框。由 ApplyBackdrop 末尾调（ApplySystemTheme 经其末尾的 ApplyBackdrop 间接触发）。
        /// </summary>
        private void ApplyGlassEdge()
        {
            bool isLiquid = DisplaySettings.Instance.BackdropMode == BackdropMode.LiquidGlass;
            bool showEdge = !isLiquid || DisplaySettings.Instance.GlassEdgeEnabled;
            TopHighlight.Visibility = showEdge ? Visibility.Visible : Visibility.Collapsed;
            BottomHighlight.Visibility = showEdge ? Visibility.Visible : Visibility.Collapsed;

            // Ultra 激活中不动 BorderBrush
            if (_ultraActive) return;

            GlassBorder.BorderBrush = (isLiquid && !DisplaySettings.Instance.GlassEdgeEnabled) ? null : _hoverBorderBrush;
        }

        // ─── 主题 ──────────────────────────────────────────────────
        private void OnThemeChanged(object? sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General ||
                e.Category == UserPreferenceCategory.Color)
            {
                Dispatcher.Invoke(ApplySystemTheme);
            }
        }

        private void ApplySystemTheme()
        {
            bool isLight = DisplaySettings.Instance.IsLight(); // 主题来自设置：System 跟随系统 / Light / Dark

            var preset = DisplaySettings.TextColorPresets[DisplaySettings.Instance.TextColorIndex];
            var text = preset.Color ?? (isLight ? _lightText : _darkText);
            var hl = isLight ? _lightHighlight : _darkHighlight;
            var hlEnd = isLight ? _lightHighlightEnd : _darkHighlightEnd;

            // GlassBorder.Background 由 ApplyBackdrop 按 BackdropMode 设定（Mica=平涂实色兜底，其余=透明让材质透出）

            var borderColor = isLight ? _lightBorder : _darkBorder;
            _hoverBorderBrush.Color = Color.FromArgb(255, borderColor.R, borderColor.G, borderColor.B);
            _hoverBorderBrush.Opacity = 0.2;
            // GlassBorder.BorderBrush 由 ApplyGlassEdge（经 ApplyBackdrop 末尾）按模式/金边开关统一设定，不在此直接赋。

            var textBrush = new SolidColorBrush(text);
            StatusText.Foreground = textBrush;
            TextElement.SetForeground(GlassBorder, textBrush);

            TopHighlight.Background = new LinearGradientBrush(hl, hlEnd, 90);
            TopHighlight.Opacity = 0.55;

            var hlSoft = Color.FromArgb((byte)(hl.A / 2), hl.R, hl.G, hl.B);
            BottomHighlight.Background = new LinearGradientBrush(hlEnd, hlSoft, 90);

            // 主题变了，深色模式属性也跟着刷新
            ApplyBackdrop();
        }

        // ─── 位置 ──────────────────────────────────────────────────
        private void CenterWindow(double width)
        {
            Left = _targetMonitor.Bounds.Left + (_targetMonitor.Bounds.Width - width) / 2;
            Top = _targetMonitor.Bounds.Top + TopMargin * _scale;
        }

        private void RefreshTargetMonitor()
        {
            var list = MonitorEnumerator.EnumerateMonitors();
            if (list.Count == 0)
            {
                _targetMonitor = new MonitorInfo
                {
                    Index = 0,
                    Bounds = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight),
                    WorkArea = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight),
                    PhysicalBounds = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight),
                    IsPrimary = true,
                };
                return;
            }
            int idx = DisplaySettings.Instance.TargetMonitorIndex;
            if (idx < 0 || idx >= list.Count) idx = 0;
            _targetMonitor = list[idx];

            // 同步帧率上限 = 当前显示器刷新率
            if (_targetMonitor.RefreshRate > 0)
                DisplaySettings.Instance.MaxGlassCaptureFps = _targetMonitor.RefreshRate;
        }

        // ─── 分辨率缩放 ────────────────────────────────────────────
        private void ComputeScale()
        {
            var h = _targetMonitor.Bounds.Height;
            if (h <= 0) h = SystemParameters.PrimaryScreenHeight;
            _scale = Math.Clamp(h / 1080.0, 0.7, 2.0);
        }

        private void ApplyScale()
        {
            var c = IslandLayout.Collapsed;
            RootGrid.LayoutTransform = new ScaleTransform(_scale, _scale);
            RootGrid.Width = c.Width;
            RootGrid.Height = c.Height;
            GlassBorder.CornerRadius = new CornerRadius(8.0 / _scale);

            Width = c.Width * _scale;
            Height = c.Height * _scale;
            CenterWindow(Width);

            StatusText.MaxWidth = Math.Max(0, c.Width - 24);
            StatusText.MaxViewWidth = Math.Max(0, MaxCollapsedBaseWidth(c.Width) - 24);
            _currentLayout = c;
        }

        // ─── 交互 ──────────────────────────────────────────────────
        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            ResetIdle();
            CancelShrink();

            if (_state == DisplayState.Collapsed)
            {
                _state = DisplayState.Hovered;
                RefreshDisplay();
                PlayHoverIn();
            }
            else if (_state == DisplayState.Hovered)
            {
                PlayHoverIn();
            }
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_suppressLeaveCollapse || _forceExpanded) return;

            switch (_state)
            {
                case DisplayState.Expanded:
                    StartShrink(ShrinkPhase.ToHover, MotionToken.ShrinkDelayToHover);
                    break;

                case DisplayState.Hovered:
                    PlayHoverOut(() => CollapseTo(DisplayState.Collapsed));
                    break;
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            CancelShrink();

            // 内联 alert（Collapsed/Hovered）：点击岛 = 关闭提醒
            if (_alerts.Current != null && _state != DisplayState.Expanded)
            {
                _alerts.DismissCurrent();
                return;
            }

            // Card overlay 可见时（Expanded + alert）：点击岛 = 正常收起
            if (_state == DisplayState.Expanded)
                return;

            _state = DisplayState.Expanded;
            AnimateExpandSequenced();
            RefreshDisplay();
        }

        private void CollapseTo(DisplayState target)
        {
            // 缩回前关掉活跃提醒（内联或静默）
            if (_alerts.Current != null)
                _alerts.DismissCurrent();

            _state = target;
            var layout = target == DisplayState.Hovered ? IslandLayout.Hovered : IslandLayout.Collapsed;
            SnapLayout(layout);
            RefreshDisplay();
        }

        // ─── 渐隐收回序列 ──────────────────────────────────────────
        private void StartShrink(ShrinkPhase phase, TimeSpan delay)
        {
            if (_forceExpanded) return;
            _shrinkPhase = phase;
            _shrinkTimer.Interval = delay;
            _shrinkTimer.Stop();
            _shrinkTimer.Start();
        }

        private void CancelShrink()
        {
            _shrinkTimer.Stop();
            _shrinkPhase = ShrinkPhase.None;
        }

        private void OnShrinkTick(object? sender, EventArgs e)
        {
            _shrinkTimer.Stop();

            switch (_shrinkPhase)
            {
                case ShrinkPhase.ToHover:
                    _state = DisplayState.Hovered;
                    RefreshDisplay();
                    // 缩回用平滑过渡，不用 BackEase 弹跳——从 Expanded 缩下来弹跳会像上下晃
                    ApplyLayout(IslandLayout.Hovered, 250);
                    StartShrink(ShrinkPhase.ToCollapsed, MotionToken.ShrinkDelayToCollapsed);
                    break;

                case ShrinkPhase.ToCollapsed:
                    _shrinkPhase = ShrinkPhase.None;
                    PlayHoverOut(() => CollapseTo(DisplayState.Collapsed));
                    break;
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ─── 显示内容 ──────────────────────────────────────────────
        private void OnHostUpdated(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(RefreshDisplay);
        }

        private void RefreshDisplay()
        {
            // —— Island Mood ——
            if (!_ultraActive)
            {
                var mood = ResolveMood();
                if (mood != IslandMood.Neutral)
                {
                    var c = IslandMoodColors.GetColor(mood) ?? Colors.Magenta;
                    GlassBorder.BorderBrush = new SolidColorBrush(c) { Opacity = 0.55 };
                    GlassBorder.BorderThickness = new Thickness(2.0);
                    _moodBorderActive = true;
                }
                else if (_moodBorderActive)
                {
                    GlassBorder.BorderBrush = _hoverBorderBrush;
                    _hoverBorderBrush.Opacity = 0.20;
                    _moodBorderActive = false;
                }
            }

            // 内联 alert 激活中（Collapsed/Hovered）：StatusText 正在显示消息，不覆盖
            if (_alerts.Current != null && _state != DisplayState.Expanded)
            {
                ExpandedHost.Visibility = Visibility.Collapsed;
                if (ExpandedHost.Content != null) ExpandedHost.Content = null;
                return;
            }

            if (_state == DisplayState.Expanded)
            {
                StatusText.Visibility = Visibility.Collapsed;
                if (!ReferenceEquals(ExpandedHost.Content, _dashboard))
                    ExpandedHost.Content = _dashboard;
                ExpandedHost.Visibility = Visibility.Visible;
                return;
            }

            if (ExpandedHost.Content != null) ExpandedHost.Content = null;
            ExpandedHost.Visibility = Visibility.Collapsed;
            InlineAlertPanel.Visibility = Visibility.Collapsed;
            _inlineAlertAction = null;

            var src = _host.CurrentSource;
            StatusText.Text = src?.CollapsedText ?? string.Empty;
            StatusText.Visibility = Visibility.Visible;

            FitCollapsedWidthToText();
        }

        private double MeasureStatusTextWidth()
        {
            var text = StatusText.Text;
            if (string.IsNullOrEmpty(text)) return 0;
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(StatusText.FontFamily, StatusText.FontStyle,
                             StatusText.FontWeight, StatusText.FontStretch),
                StatusText.FontSize,
                StatusText.Foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return ft.Width;
        }

        // ─── 布局计算（单一真相源：所有尺寸决策归拢于此）──────────

        /// <summary>根据当前状态 + 源 + alert，计算目标 IslandLayout。</summary>
        private IslandLayout ComputeLayout()
        {
            if (_state == DisplayState.Expanded)
                return IslandLayout.Expanded;

            // Hovered 跟 Collapsed 同宽、略高 —— 见 IslandLayout.Hovered
            return ComputeCollapsedLayout();
        }

        /// <summary>收起态布局：优先用源声明的期望宽度，否则自动测量文本。</summary>
        private IslandLayout ComputeCollapsedLayout()
        {
            double desiredW = _host.CurrentDesiredWidth
                ?? (MeasureStatusTextWidth() + 24);

            double min = _state == DisplayState.Hovered
                ? IslandLayout.Hovered.Width
                : IslandLayout.Collapsed.Width;
            double max = Math.Max(min + 40,
                _targetMonitor.Bounds.Width / _scale * IslandLayout.CollapsedScreenFraction);
            double w = Math.Clamp(desiredW, min, max);

            double h = _state == DisplayState.Hovered
                ? IslandLayout.Hovered.Height
                : IslandLayout.Collapsed.Height;

            return new IslandLayout(w, h, _state == DisplayState.Hovered ? BackdropStrength.Medium : BackdropStrength.Subtle);
        }

        /// <summary>动画过渡到目标布局。</summary>
        private void ApplyLayout(IslandLayout layout, int durationMs, IEasingFunction? ease = null)
        {
            _currentLayout = layout;
            _glass?.SetBackdrop(layout.Backdrop);
            AnimateTo(layout.Width, layout.Height, durationMs, ease);
        }

        /// <summary>瞬间跳到目标布局（无动画）。</summary>
        private void SnapLayout(IslandLayout layout)
        {
            _currentLayout = layout;
            SnapTo(layout.Width, layout.Height);
        }

        private double MaxCollapsedBaseWidth(double baseMin)
            => Math.Max(baseMin + 40,
                _targetMonitor.Bounds.Width / _scale * IslandLayout.CollapsedScreenFraction);

        private void FitCollapsedWidthToText()
        {
            var layout = ComputeCollapsedLayout();

            StatusText.MaxWidth = Math.Max(0, layout.Width - 24);
            StatusText.MaxViewWidth = Math.Max(0,
                MaxCollapsedBaseWidth(_state == DisplayState.Hovered
                    ? IslandLayout.Hovered.Width
                    : IslandLayout.Collapsed.Width) - 24);

            if (Math.Abs(RootGrid.Width - layout.Width) > 0.5 ||
                Math.Abs(RootGrid.Height - layout.Height) > 0.5)
            {
                ApplyLayout(layout, 160);
            }
        }

        // ─── 全屏抑制 ──────────────────────────────────────────────
        private void OnFullScreenChanged(object? sender, bool isFullScreen)
        {
            Dispatcher.Invoke(() =>
            {
                if (isFullScreen)
                {
                    CancelShrink();
                    UpdateFullScreenSuppression();
                }
                else
                {
                    if (_alerts.Current == null)
                        _state = IsMouseOver ? DisplayState.Hovered : DisplayState.Collapsed;
                    // 先咬回合法的收起态尺寸再播抽纸出现——否则会在展开态巨大窗口上
                    // 播 slide-in，然后 RefreshDisplay 再缩回去，视觉上先大后小。
                    SnapToCurrentCollapsedSize();
                    UpdateFullScreenSuppression();
                    RefreshDisplay();
                }
            });
        }

        /// <summary>把窗口瞬间切到 Collapsed/Hovered 基准尺寸，不播动画。</summary>
        private void SnapToCurrentCollapsedSize()
        {
            var layout = _state == DisplayState.Hovered ? IslandLayout.Hovered : IslandLayout.Collapsed;
            SnapLayout(layout);
        }

        private void UpdateFullScreenSuppression()
        {
            bool suppress = DisplaySettings.Instance.EnableFullScreenSuppress
                && _fullScreen.IsFullScreen
                && _alerts.Current == null;

            // 仅在抑制态翻转时播抽纸动画；状态未变直接返回（Visibility 已由上一次动画定好）
            if (suppress == _isSuppressed) return;
            _isSuppressed = suppress;

            if (suppress)
                PlayTissueRetract();   // 进全屏：胶囊塞回顶部
            else
                PlayTissueAppear();    // 退全屏：从顶部抽出、长大成胶囊
        }

        // ─── Sleep 省电态（DisplayModifiers.Sleep）──
        // 5min 无交互 -> 降玻璃抓取帧率 + 微暗 backdrop，不变尺寸。任何交互立即退出。
        private void ResetIdle()
        {
            _idleTimer?.Stop();
            _idleTimer?.Start();
            if (_sleeping) ExitSleep();
        }

        private void EnterSleep()
        {
            if (_sleeping) return;
            _sleeping = true;
            _glass?.SetSleep(true);
        }

        private void ExitSleep()
        {
            if (!_sleeping) return;
            _sleeping = false;
            _glass?.SetSleep(false);
        }

        // ─── 抽纸动画（全屏进出）──────────────────────────────────
        // 退全屏出现：从屏幕上沿外滑入 + 顶部中心放大 + 淡入，像一张纸从顶部被抽出。
        // 进全屏收起：倒放——滑出上沿 + 缩向顶部中心 + 淡出，像被塞回。
        // 参考 vivo 平板应用中心下载任务退出到桌面的收起动画。



        private ScaleTransform EnsureTissueScale()
        {
            if (GlassBorder.RenderTransform is ScaleTransform s)
                return s;
            s = new ScaleTransform(1, 1);
            GlassBorder.RenderTransform = s;
            GlassBorder.RenderTransformOrigin = new Point(0.5, 0); // 顶部中心：向下生长
            return s;
        }

        private void PlayTissueAppear()
        {
            CancelShrink();

            // 记录当前完整尺寸（window + RootGrid），用于同步缩放
            double baseW = RootGrid.Width;
            double baseH = RootGrid.Height;
            double startScale = DisplaySettings.Instance.TissueStartScale;

            double restTop = _targetMonitor.Bounds.Top + TopMargin * _scale;
            double startTop = _targetMonitor.Bounds.Top - (baseH * startScale * _scale) - 4;

            var scale = EnsureTissueScale();

            this.BeginAnimation(TopProperty, null);
            GlassBorder.BeginAnimation(UIElement.OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            // 起始态：窗口+内容同步缩到 TissueStartScale、悬于上沿外、全透明
            scale.ScaleX = startScale;
            scale.ScaleY = startScale;
            SnapTo(baseW * startScale, baseH * startScale);
            Top = startTop;
            GlassBorder.Opacity = 0;
            Visibility = Visibility.Visible;

            var ease = MotionToken.EaseOut;
            var dur = MotionToken.Slow;
            int ms = (int)dur.TimeSpan.TotalMilliseconds;
            AnimationManager.Animate(scale, ScaleTransform.ScaleXProperty, 1.0, dur, ease);
            AnimationManager.Animate(scale, ScaleTransform.ScaleYProperty, 1.0, dur, ease);
            AnimateTo(baseW, baseH, ms, ease);
            AnimationManager.Animate(GlassBorder, UIElement.OpacityProperty, 1.0, dur, ease);
            AnimationManager.Animate(this, TopProperty, restTop, dur, ease);
        }

        private void PlayTissueRetract()
        {
            CancelShrink();

            double baseW = RootGrid.Width;
            double baseH = RootGrid.Height;
            double endScale = DisplaySettings.Instance.TissueStartScale;

            double restTop = _targetMonitor.Bounds.Top + TopMargin * _scale;
            double endTop = _targetMonitor.Bounds.Top - (baseH * endScale * _scale) - 4;

            var scale = EnsureTissueScale();
            var ease = MotionToken.EaseIn;
            var dur = MotionToken.Slow;
            int ms = (int)dur.TimeSpan.TotalMilliseconds;

            // 窗口+内容同步缩放
            AnimationManager.Animate(scale, ScaleTransform.ScaleXProperty, endScale, dur, ease);
            AnimationManager.Animate(scale, ScaleTransform.ScaleYProperty, endScale, dur, ease);
            AnimateTo(baseW * endScale, baseH * endScale, ms, ease);
            AnimationManager.Animate(GlassBorder, UIElement.OpacityProperty, 0.0, dur, ease);
            AnimationManager.Animate(this, TopProperty, endTop, dur, ease);

            int token = ++_animationToken;
            AnimationManager.DelayedInvoke(ms, () =>
            {
                if (token != _animationToken) return;
                if (!_isSuppressed) return;
                Visibility = Visibility.Hidden;
                Top = restTop;
                GlassBorder.Opacity = 1;
                scale.ScaleX = 1;
                scale.ScaleY = 1;
                SnapTo(baseW, baseH); // 恢复原始尺寸，方便下次 appear
            });
        }

        // ─── 瞬时提醒（不占状态机：Collapsed/Hovered=内联替换，Expanded=Card浮层）──
        private void OnAlertChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (_alerts.Current != null)
                {
                    var alert = _alerts.Current;
                    ResetIdle();

                    if (_state == DisplayState.Expanded)
                    {
                        // 展开态：消息静默入历史，不弹卡片、不打断浏览
                        _preAlertState = _state;
                        // AlertHost 已调用 AddToHistory；计时器到时自动 dismiss，无需任何视觉反馈
                    }
                    else
                    {
                        // 内联模式：消息替换胶囊内容（天气→消息面板），宽度自适应
                        _preAlertState = _state;
                        CancelShrink();

                        ExpandedHost.Visibility = Visibility.Collapsed;
                        if (ExpandedHost.Content != null) ExpandedHost.Content = null;

                        if (alert.Kind == AlertKind.Summary)
                        {
                            // Focus-summary 非抢占：保持当前尺寸，只把 StatusText 换成摘要（图标+标题）。
                            // 不生长、不动作按钮、不心跳，用户看了就看到，没看也不叫。超长由 Marquee 滚动。
                            InlineAlertPanel.Visibility = Visibility.Collapsed;
                            _inlineAlertAction = null;
                            string body = string.IsNullOrEmpty(alert.Subtitle)
                                ? alert.Title
                                : $"{alert.Title} · {alert.Subtitle}";
                            StatusText.Text = string.IsNullOrEmpty(alert.Icon)
                                ? body
                                : $"{alert.Icon} {body}";
                            StatusText.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            // Interactive：现有 InlineAlertPanel 路径（生长 + 动作按钮）
                            StatusText.Visibility = Visibility.Collapsed;

                            InlineAlertIcon.Text = alert.Icon ?? "";
                            InlineAlertIcon.Visibility = string.IsNullOrEmpty(alert.Icon)
                                ? Visibility.Collapsed : Visibility.Visible;
                            InlineAlertTitle.Text = alert.Title;
                            InlineAlertSubtitle.Text = alert.Subtitle ?? "";
                            InlineAlertSubtitle.Visibility = string.IsNullOrEmpty(alert.Subtitle)
                                ? Visibility.Collapsed : Visibility.Visible;
                            InlineAlertAction.Content = alert.Action?.Label ?? "";
                            InlineAlertAction.Visibility = alert.Action != null
                                ? Visibility.Visible : Visibility.Collapsed;
                            _inlineAlertAction = alert.Action;
                            InlineAlertPanel.Visibility = Visibility.Visible;

                            FitAlertWidth();
                            PlayInlineHeartbeat();
                        }
                        UpdateFullScreenSuppression();
                    }
                }
                else
                {
                    RestoreFromAlert();
                }
            });
        }

        /// <summary>测量内联消息面板宽度，动画适配岛宽。
        /// 下限 AlertMinWidth 保证从 Collapsed/Hovered 跳过来时总有可见的尺寸变化。</summary>
        private void FitAlertWidth()
        {
            // 先让 WPF 做一次 measure pass，得到面板真实所需宽度
            InlineAlertPanel.Measure(new Size(double.PositiveInfinity, IslandLayout.Collapsed.Height));
            double desired = InlineAlertPanel.DesiredSize.Width + 16;
            double target = Math.Clamp(desired, IslandLayout.AlertMinWidth, IslandLayout.AlertMaxWidth);

            // 限制标题最大宽，防超长文本撑爆
            double contentW = Math.Max(0, target - 16
                - (InlineAlertIcon.Visibility == Visibility.Visible ? 26 : 0)
                - (InlineAlertAction.Visibility == Visibility.Visible ? 70 : 0));
            InlineAlertTitle.MaxWidth = Math.Max(40, contentW);

            ApplyLayout(new IslandLayout(target, IslandLayout.Collapsed.Height), 200);
        }

        /// <summary>从 alert 恢复到叠加前的显示态。</summary>
        private void RestoreFromAlert()
        {
            CancelShrink();

            if (_preAlertState == DisplayState.Expanded)
            {
                // 展开态静默 alert：无视觉变化，状态不变
                _state = DisplayState.Expanded;
                RefreshDisplay();
            }
            else
            {
                // 内联模式：隐藏消息面板 → RefreshDisplay 恢复天气/时间
                InlineAlertPanel.Visibility = Visibility.Collapsed;
                _inlineAlertAction = null;
                _state = IsMouseOver ? DisplayState.Hovered : DisplayState.Collapsed;
                RefreshDisplay();
                if (_state == DisplayState.Hovered) PlayHoverIn();
            }
            _preAlertState = null;
            UpdateFullScreenSuppression();
        }

        /// <summary>内联消息的动作按钮点击：执行回调 → 关闭提醒。</summary>
        private void InlineAlertAction_Click(object sender, RoutedEventArgs e)
        {
            if (_inlineAlertAction != null)
            {
                try { _inlineAlertAction.Callback(); }
                catch { /* 动作失败不卡提醒 */ }
            }
            _alerts.DismissCurrent();
            e.Handled = true;
        }

        private void TestAlert_Click(object sender, RoutedEventArgs e)
        {
            _alerts.Enqueue(new SimpleAlert(
                "test", "测试提醒", "这是一条灵动岛提醒", "🔔",
                TimeSpan.FromSeconds(2.5), priority: 10));
        }

        private void TestSummary_Click(object sender, RoutedEventArgs e)
        {
            // Focus-summary 非抢占：保持当前尺寸只换内容，不生长、不动作按钮。
            _alerts.Enqueue(new SimpleAlert(
                "test.summary", "下载完成", "report.pdf · 2.3 MB", "⬇",
                TimeSpan.FromSeconds(3), priority: 40, kind: AlertKind.Summary));
        }

        private void TestActionAlert_Click(object sender, RoutedEventArgs e)
        {
            _alerts.Enqueue(new SimpleAlert(
                "test.action", "测试动作提醒", "点右侧「打开」执行动作", "🟢",
                TimeSpan.FromSeconds(5), priority: 10,
                action: new AlertAction("打开", () =>
                {
                    try { System.Diagnostics.Process.Start("explorer.exe",
                        DownloadAlertSource.ResolveWatchFolder()); }
                    catch { }
                })));
        }

        private async void TestInterrupt_Click(object sender, RoutedEventArgs e)
        {
            _alerts.Enqueue(new SimpleAlert(
                "test.low", "低优先级提醒", "应被高优先级打断", "🟦",
                TimeSpan.FromSeconds(2.5), priority: 10));
            await Task.Delay(800);
            _alerts.Enqueue(new SimpleAlert(
                "test.high", "高优先级打断!", "已抢占低优先级", "🔴",
                TimeSpan.FromSeconds(3), priority: 90));
        }

        // ─── 多显示器 ──────────────────────────────────────────────
        private void OnDisplaySettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DisplaySettings.TargetMonitorIndex))
            {
                Dispatcher.Invoke(MoveToCurrentMonitor);
            }
            else if (e.PropertyName == nameof(DisplaySettings.DownloadFolderPath))
            {
                Dispatcher.Invoke(() => _download.Restart());
            }
            else if (e.PropertyName == nameof(DisplaySettings.CherryApiKey)
                  || e.PropertyName == nameof(DisplaySettings.EnableCherryAlert))
            {
                // Cherry key / 开关变了：重启轮询（key 从无到有则开始轮询，从有到无则停止）
                Dispatcher.Invoke(() => _cherry.Restart());
            }
            else if (e.PropertyName == nameof(DisplaySettings.BackdropMode))
            {
                Dispatcher.Invoke(ApplyBackdrop);
            }
            else if (e.PropertyName == nameof(DisplaySettings.BlurIntensity)
                  || e.PropertyName == nameof(DisplaySettings.BlurEnabled))
            {
                Dispatcher.Invoke(ApplyBackdrop);
            }
            else if (e.PropertyName == nameof(DisplaySettings.ThemeMode))
            {
                // 主题变：重刷文字/边框/高光，并连带重应用材质（深浅底色）
                Dispatcher.Invoke(ApplySystemTheme);
            }
            else if (e.PropertyName == nameof(DisplaySettings.EnableFullScreenSuppress))
            {
                // 全屏抑制开关变了：立即重算（用户在全屏中关闭开关时，应即时让岛重现）
                Dispatcher.Invoke(UpdateFullScreenSuppression);
            }
            else if (e.PropertyName == nameof(DisplaySettings.CaptureMode))
            {
                // 抓屏后端切换：热切换不重启进程
                Dispatcher.Invoke(() => _glass?.SwitchBackend());
            }
            else if (e.PropertyName == nameof(DisplaySettings.GlassBlurRadius)
                  || e.PropertyName == nameof(DisplaySettings.GlassTintIntensity)
                  || e.PropertyName == nameof(DisplaySettings.GlassCaptureFps))
            {
                // 液态玻璃参数变：即时刷新渲染器（半径/底色/帧率）
                Dispatcher.Invoke(() => _glass?.UpdateSettings());
            }
            else if (e.PropertyName == nameof(DisplaySettings.GlassEdgeEnabled))
            {
                Dispatcher.Invoke(ApplyGlassEdge);
            }
            else if (e.PropertyName == nameof(DisplaySettings.TextColorIndex))
            {
                Dispatcher.Invoke(ApplySystemTheme);
            }
            else if (e.PropertyName == nameof(DisplaySettings.ProMode))
            {
                Dispatcher.Invoke(UpdateWindowTitle);
            }
            else if (e.PropertyName == nameof(DisplaySettings.IsUltraMode)
                  || e.PropertyName == nameof(DisplaySettings.UltraEffectEnabled))
            {
                Dispatcher.Invoke(ApplyUltraEffect);
            }
        }

        private void UpdateWindowTitle()
        {
            Title = DisplaySettings.Instance.ProMode ? "DynamicIsland Pro" : "DynamicIsland";
        }

        // ─── Ultra 彩虹光效（"阳光彩虹小玻璃"开关直接控制胶囊边框彩虹）─────────────
        private bool _ultraActive;
        private Brush? _ultraOldBorderBrush;
        private LinearGradientBrush? _ultraBrush;
        private DispatcherTimer? _ultraTimer;

        // ─── Island Mood（边光情绪染色，inline 在 RefreshDisplay 中应用）───
        private bool _moodBorderActive;

        private IslandMood ResolveMood()
        {
            if (_ultraActive) return IslandMood.Ultra;
            if (_alerts.Current != null)
                return AlertPriorityMood(_alerts.Current.Priority);
            return _host.CurrentMood;
        }

        private static IslandMood AlertPriorityMood(int priority) => priority switch
        {
            >= 80 => IslandMood.Critical,
            >= 60 => IslandMood.Warning,
            >= 30 => IslandMood.Info,
            _     => IslandMood.Neutral,
        };

        private void ApplyUltraEffect()
        {
            bool want = DisplaySettings.Instance.UltraEffectEnabled;
            if (want == _ultraActive) return;
            _ultraActive = want;

            if (want)
            {
                _ultraOldBorderBrush = GlassBorder.BorderBrush;
                _ultraBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    SpreadMethod = GradientSpreadMethod.Repeat,
                };
                for (int i = 0; i < 6; i++)
                    _ultraBrush.GradientStops.Add(new GradientStop(Colors.Red, i / 6.0));
                GlassBorder.BorderBrush = _ultraBrush;
                GlassBorder.BorderThickness = new System.Windows.Thickness(2.0);

                // DispatcherTimer 按显示器刷新率跑，比 CompositionTarget.Rendering 可靠
                int intervalMs = Math.Max(16, 1000 / Math.Max(DisplaySettings.Instance.MaxGlassCaptureFps, 30));
                _ultraTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMs) };
                _ultraTimer.Tick += OnUltraTick;
                _ultraTimer.Start();
            }
            else
            {
                if (_ultraTimer != null)
                {
                    _ultraTimer.Stop();
                    _ultraTimer.Tick -= OnUltraTick;
                    _ultraTimer = null;
                }
                GlassBorder.BorderBrush = _ultraOldBorderBrush;
                GlassBorder.BorderThickness = new System.Windows.Thickness(0.5);
                _ultraBrush = null;
                _moodBorderActive = false; // 让下一次 RefreshDisplay 恢复 mood 色
            }
        }

        private void OnUltraTick(object? sender, EventArgs e)
        {
            if (_ultraBrush == null) return;
            double t = (Environment.TickCount % 3000) / 3000.0;
            for (int i = 0; i < _ultraBrush.GradientStops.Count; i++)
            {
                double hue = (t + i / (double)_ultraBrush.GradientStops.Count) % 1.0;
                _ultraBrush.GradientStops[i].Color = ColorUtils.HsvToColor(hue * 360.0, 0.85, 1.0);
                _ultraBrush.GradientStops[i].Offset = (t + i / (double)_ultraBrush.GradientStops.Count) % 1.0;
            }
        }

        private void OnDisplaySettingsTopologyChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(MoveToCurrentMonitor);
        }

        private void MoveToCurrentMonitor()
        {
            RefreshTargetMonitor();
            ComputeScale();
            ApplyScale();
            if (_state == DisplayState.Collapsed || _state == DisplayState.Hovered)
            {
                // 内联 alert 激活中：按消息文本宽度适配；否则按天气/时间宽度适配
                if (_alerts.Current != null)
                    FitAlertWidth();
                else
                    FitCollapsedWidthToText();
            }
            else if (_state == DisplayState.Expanded)
            {
                ApplyLayout(IslandLayout.Expanded, 200);
            }
        }

        // ─── 右键菜单（抑制假离开收起）───────────────────────────
        private void OnContextMenuOpened(object sender, RoutedEventArgs e)
        {
            _suppressLeaveCollapse = true;
            CancelShrink();
        }

        private void OnContextMenuClosed(object sender, RoutedEventArgs e)
        {
            _suppressLeaveCollapse = false;
            CancelShrink();
            if (!IsMouseOver && _state != DisplayState.Collapsed)
                StartShrink(ShrinkPhase.ToHover, MotionToken.ShrinkDelayToHover);
        }

        // ─── 设置窗 ────────────────────────────────────────────────
        private SettingsWindow? _settingsWindow;
        private bool _forceExpanded;

        private void ForceExpand_Click(object sender, RoutedEventArgs e)
        {
            _forceExpanded = true;
            _suppressLeaveCollapse = true; // 菜单关闭不走缩回
            CancelShrink();

            if (_state != DisplayState.Expanded)
            {
                _state = DisplayState.Expanded;
                AnimateExpandSequenced();
                RefreshDisplay();
            }

            // 拿键盘焦点让 Esc 生效
            Focusable = true;
            Activate();
            Focus();
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape && _forceExpanded)
            {
                _forceExpanded = false;
                _suppressLeaveCollapse = false;
                CancelShrink();
                var target = IsMouseOver ? DisplayState.Hovered : DisplayState.Collapsed;
                _state = target;
                RefreshDisplay();
                if (target == DisplayState.Hovered)
                    PlayHoverIn();
            }
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            _suppressLeaveCollapse = true;

            if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow();
                _settingsWindow.Closed += (_, _) =>
                {
                    _settingsWindow = null;
                    _suppressLeaveCollapse = false;
                    Dispatcher.Invoke(() =>
                    {
                        if (!IsMouseOver && _state != DisplayState.Collapsed)
                            StartShrink(ShrinkPhase.ToHover, MotionToken.ShrinkDelayToHover);
                    });
                };
                _settingsWindow.Show();
            }
            else
            {
                _settingsWindow.Activate();
            }
        }

        // ─── 动画 ──────────────────────────────────────────────────
        private void AnimateTo(double baseW, double baseH, int ms, IEasingFunction? ease = null)
        {
            var e = ease ?? MotionToken.EaseOut;
            var toW = baseW * _scale;
            var toH = baseH * _scale;
            var toLeft = _targetMonitor.Bounds.Left + (_targetMonitor.Bounds.Width - toW) / 2;
            var dur = TimeSpan.FromMilliseconds(ms);

            AnimationManager.Animate(this, WidthProperty, toW, dur, e);
            AnimationManager.Animate(this, HeightProperty, toH, dur, e);
            AnimationManager.Animate(this, LeftProperty, toLeft, dur, e);
            AnimationManager.Animate(RootGrid, FrameworkElement.WidthProperty, baseW, dur, e);
            AnimationManager.Animate(RootGrid, FrameworkElement.HeightProperty, baseH, dur, e);
        }

        private void AnimateExpandSequenced()
        {
            var layout = IslandLayout.Expanded;
            double baseW = layout.Width;
            double baseH = layout.Height;

            var toW = baseW * _scale;
            var toH = baseH * _scale;
            var toLeft = _targetMonitor.Bounds.Left + (_targetMonitor.Bounds.Width - toW) / 2;

            // 阶段 1：宽度先展开，带轻微过冲（像拉开窗帘）
            var phase1 = MotionToken.Normal;
            var bounceOut = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
            AnimationManager.Animate(this, WidthProperty, toW, phase1, bounceOut);
            AnimationManager.Animate(this, LeftProperty, toLeft, phase1, bounceOut);
            AnimationManager.Animate(RootGrid, FrameworkElement.WidthProperty, baseW, phase1, bounceOut);

            int token = ++_animationToken;
            int expandMs = (int)phase1.TimeSpan.TotalMilliseconds;
            AnimationManager.DelayedInvoke(expandMs, () =>
            {
                if (token != _animationToken) return;
                // 阶段 2：高度下放，更强过冲（像纸卷弹开）
                var bounceDown = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 };
                var phase2 = MotionToken.Fast;
                AnimationManager.Animate(this, HeightProperty, toH, phase2, bounceDown);
                AnimationManager.Animate(RootGrid, FrameworkElement.HeightProperty, baseH, phase2, bounceDown);
            });

            _currentLayout = layout;
        }

        private void SnapTo(double baseW, double baseH)
        {
            var toW = baseW * _scale;
            var toH = baseH * _scale;
            var toLeft = _targetMonitor.Bounds.Left + (_targetMonitor.Bounds.Width - toW) / 2;
            Width = toW; Height = toH; Left = toLeft;
            RootGrid.Width = baseW; RootGrid.Height = baseH;
        }

        // ─── Hover 动效 ──────────────────────────────────────────────
        /// <summary>Hover 进入：边框 20%→35%，高光约 30%→50%。</summary>
        private void PlayHoverIn()
        {
            var bounceOut = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
            ApplyLayout(IslandLayout.Hovered, 180, bounceOut);
            AnimationManager.Animate(_hoverBorderBrush, System.Windows.Media.Brush.OpacityProperty, 0.35, MotionToken.Fast, MotionToken.EaseOut);
            AnimationManager.Animate(TopHighlight, UIElement.OpacityProperty, 1.0, MotionToken.Fast, MotionToken.EaseOut);
        }

        /// <summary>Hover 退出：尺寸收回 + 高光降回，一气呵成。onDone 在全部完成后调用。</summary>
        private void PlayHoverOut(Action? onDone = null)
        {
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
            ApplyLayout(IslandLayout.Collapsed, 150, easeIn);
            AnimationManager.Animate(_hoverBorderBrush, System.Windows.Media.Brush.OpacityProperty, 0.20, MotionToken.Fast, easeIn);
            AnimationManager.Animate(TopHighlight, UIElement.OpacityProperty, 0.65, MotionToken.Fast, easeIn, onDone);
        }

        // ─── 内联消息心跳（消息面板"咚"一下的物理弹跳）───
        private void PlayInlineHeartbeat()
        {
            var scale = (ScaleTransform)InlineAlertPanel.RenderTransform;

            // 阶段 1：按下（80ms）
            AnimationManager.Animate(scale, ScaleTransform.ScaleXProperty, 0.92, MotionToken.Instant, MotionToken.EaseOut);
            AnimationManager.Animate(scale, ScaleTransform.ScaleYProperty, 0.92, MotionToken.Instant, MotionToken.EaseOut);

            // 阶段 2：弹回 + 微过冲（220ms）
            int token = ++_animationToken;
            AnimationManager.DelayedInvoke(80, () =>
            {
                if (token != _animationToken) return;
                var bounce = new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.4 };
                var dur = TimeSpan.FromMilliseconds(220);
                AnimationManager.Animate(scale, ScaleTransform.ScaleXProperty, 1.0, dur, bounce);
                AnimationManager.Animate(scale, ScaleTransform.ScaleYProperty, 1.0, dur, bounce);
            });
        }


    }
}
