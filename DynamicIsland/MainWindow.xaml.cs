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
        // ─── 尺寸常量（基准，按 1920×1080 屏 1:1 设计；其它分辨率按 _scale 缩放）──
        private const double CollapsedWidth = 200;
        private const double CollapsedHeight = 40;
        private const double HoverWidth = 228;
        private const double HoverHeight = 48;
        private const double ExpandedWidth = 720;
        private const double ExpandedHeight = 240;
        private const double ScreenWidthFraction = 2.0 / 5.0;
        private const double AlertWidth = 420;
        private const double AlertHeight = 40;
        private const double AlertStartWidth = 280;
        private const double TopMargin = 12;
        private const double CornerRadiusBase = 20;

        // ─── 状态机 ────────────────────────────────────────────────
        private enum DisplayState { Collapsed, Hovered, Expanded, Alert }
        private DisplayState _state = DisplayState.Collapsed;
        private DisplayState _stateBeforeAlert = DisplayState.Collapsed;

        // ─── 渐隐收回序列 ──────────────────────────────────────────
        private static readonly TimeSpan ShrinkDelayToHover = TimeSpan.FromSeconds(1.0);
        private static readonly TimeSpan ShrinkDelayToCollapsed = TimeSpan.FromSeconds(0.5);
        private enum ShrinkPhase { None, ToHover, ToCollapsed }
        private ShrinkPhase _shrinkPhase = ShrinkPhase.None;
        private readonly DispatcherTimer _shrinkTimer;
        private bool _suppressLeaveCollapse;

        // ─── 数据源 ────────────────────────────────────────────────
        private readonly IslandHost _host;
        private readonly MediaSource _media;
        private readonly SystemResourceSource _system;
        private readonly IslandDashboard _dashboard;

        // ─── 瞬时提醒 ──────────────────────────────────────────────
        private readonly AlertHost _alerts = new();
        private readonly AlertStats _stats = new();
        private readonly SystemNotifier _notifier = new();
        private readonly AlertView _alertView = new();
        private readonly BatteryAlertSource _battery;
        private readonly ClipboardAlertSource _clipboard;
        private readonly UsbAlertSource _usb;
        private readonly BluetoothAlertSource _bluetooth;
        private readonly NetworkAlertSource _network;
        private readonly DownloadAlertSource _download;

        // ─── 托盘图标 ──────────────────────────────────────────────
        private readonly TrayIcon _tray;

        // ─── 全屏抑制 ──────────────────────────────────────────────
        private readonly FullScreenDetector _fullScreen;

        // Hover 动效专用的 BorderBrush（Color 不含 alpha，Opacity 控制透明度）
        private readonly SolidColorBrush _hoverBorderBrush = new(Colors.White) { Opacity = 0.2 };

        // 动画版本号：用于 DelayedInvoke 取消已过时的延迟步骤
        private int _animationToken;

        // ─── 分辨率缩放 ────────────────────────────────────────────
        private double _scale = 1.0;

        // ─── 目标显示器 ────────────────────────────────────────────
        private MonitorInfo _targetMonitor = null!;

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
        private readonly Color _lightBg = Color.FromArgb(0xCC, 0xE8, 0xE8, 0xE8);
        private readonly Color _lightBorder = Color.FromArgb(0x28, 0x00, 0x00, 0x00);
        private readonly Color _lightText = Colors.Black;
        private readonly Color _lightHighlight = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
        private readonly Color _lightHighlightEnd = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);

        private readonly Color _darkBg = Color.FromArgb(0xBF, 0x1A, 0x1A, 0x1A);
        private readonly Color _darkBorder = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        private readonly Color _darkText = Colors.White;
        private readonly Color _darkHighlight = Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF);
        private readonly Color _darkHighlightEnd = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);

        private static readonly IEasingFunction Ease =
            new QuadraticEase { EasingMode = EasingMode.EaseOut };

        public MainWindow()
        {
            InitializeComponent();

            _host = new IslandHost();
            _host.Register(_media = new MediaSource());
            _host.Register(_system = new SystemResourceSource());
            _host.Register(new ClockSource());

            IIslandPanel[] panels = { new MediaPanel(_media), new NotificationPanel(_alerts), new StatsPanel(_stats) };
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
            _alerts.CurrentChanged += OnAlertChanged;
            _alertView.ActionInvoked += (_, _) => Dispatcher.BeginInvoke(new Action(() => _alerts.DismissCurrent()));

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

            DisplaySettings.Instance.PropertyChanged += OnDisplaySettingsChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsTopologyChanged;

            _shrinkTimer = new DispatcherTimer();
            _shrinkTimer.Tick += OnShrinkTick;

            Loaded += OnLoaded;
            Closed += OnClosed;
            Deactivated += OnDeactivated;
        }

        // ─── 生命周期 ──────────────────────────────────────────────
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

            _fullScreen.Start(new WindowInteropHelper(this).Handle);
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
            _fullScreen.IsFullScreenChanged -= OnFullScreenChanged;
            _fullScreen.Dispose();
            _alerts.CurrentChanged -= OnAlertChanged;
            _alerts.Dispose();
            _stats.Dispose();
            _notifier.Dispose();
            _tray.Dispose();
            _shrinkTimer.Stop();
            _shrinkTimer.Tick -= OnShrinkTick;
        }

        private void OnDeactivated(object? sender, EventArgs e)
        {
            if (_suppressLeaveCollapse) return;

            if (_alerts.Current != null)
                _alerts.DismissCurrent();
            if (_state != DisplayState.Collapsed)
            {
                CancelShrink();
                CollapseTo(DisplayState.Collapsed, 150);
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
            bool isLight = IsWindowsLightTheme();

            var bg = isLight ? _lightBg : _darkBg;
            var text = isLight ? _lightText : _darkText;
            var hl = isLight ? _lightHighlight : _darkHighlight;
            var hlEnd = isLight ? _lightHighlightEnd : _darkHighlightEnd;

            GlassBorder.Background = new SolidColorBrush(bg);

            var borderColor = isLight ? _lightBorder : _darkBorder;
            _hoverBorderBrush.Color = Color.FromArgb(255, borderColor.R, borderColor.G, borderColor.B);
            _hoverBorderBrush.Opacity = 0.2;
            GlassBorder.BorderBrush = _hoverBorderBrush;

            var textBrush = new SolidColorBrush(text);
            StatusText.Foreground = textBrush;
            TextElement.SetForeground(GlassBorder, textBrush);

            TopHighlight.Background = new LinearGradientBrush(hl, hlEnd, 90);
            TopHighlight.Opacity = 0.55;

            var hlSoft = Color.FromArgb((byte)(hl.A / 2), hl.R, hl.G, hl.B);
            BottomHighlight.Background = new LinearGradientBrush(hlEnd, hlSoft, 90);
        }

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
            RootGrid.LayoutTransform = new ScaleTransform(_scale, _scale);
            RootGrid.Width = CollapsedWidth;
            RootGrid.Height = CollapsedHeight;
            GlassBorder.CornerRadius = new CornerRadius(CornerRadiusBase * _scale);

            Width = CollapsedWidth * _scale;
            Height = CollapsedHeight * _scale;
            CenterWindow(Width);

            StatusText.MaxWidth = Math.Max(0, CollapsedWidth - 24);
            StatusText.MaxViewWidth = Math.Max(0, MaxCollapsedBaseWidth(CollapsedWidth) - 24);
        }

        // ─── 交互 ──────────────────────────────────────────────────
        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
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
            if (_suppressLeaveCollapse) return;

            switch (_state)
            {
                case DisplayState.Expanded:
                    StartShrink(ShrinkPhase.ToHover, ShrinkDelayToHover);
                    break;

                case DisplayState.Hovered:
                    PlayHoverOut(() => CollapseTo(DisplayState.Collapsed, 80));
                    break;
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            CancelShrink();

            if (_alerts.Current != null)
            {
                _alerts.DismissCurrent();
                return;
            }

            if (_state == DisplayState.Expanded)
                return;

            _state = DisplayState.Expanded;
            AnimateExpandSequenced();
            RefreshDisplay();
        }

        private void CollapseTo(DisplayState target, int ms)
        {
            _state = target;
            RefreshDisplay();
        }

        // ─── 渐隐收回序列 ──────────────────────────────────────────
        private void StartShrink(ShrinkPhase phase, TimeSpan delay)
        {
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
                    PlayHoverIn();
                    StartShrink(ShrinkPhase.ToCollapsed, ShrinkDelayToCollapsed);
                    break;

                case ShrinkPhase.ToCollapsed:
                    _shrinkPhase = ShrinkPhase.None;
                    PlayHoverOut(() => CollapseTo(DisplayState.Collapsed, 150));
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
            if (_alerts.Current != null)
            {
                StatusText.Visibility = Visibility.Collapsed;
                if (ExpandedHost.Content != null) ExpandedHost.Content = null;
                ExpandedHost.Visibility = Visibility.Collapsed;
                if (!ReferenceEquals(AlertSlot.Content, _alertView))
                    AlertSlot.Content = _alertView;
                AlertSlot.Visibility = Visibility.Visible;
                return;
            }

            AlertSlot.Visibility = Visibility.Collapsed;
            if (AlertSlot.Content != null) AlertSlot.Content = null;

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

        private double MaxCollapsedBaseWidth(double baseMin)
            => Math.Max(baseMin + 40, _targetMonitor.Bounds.Width / _scale * ScreenWidthFraction);

        private void FitCollapsedWidthToText()
        {
            double baseMin = _state == DisplayState.Hovered ? HoverWidth : CollapsedWidth;
            double baseMax = MaxCollapsedBaseWidth(baseMin);

            double desired = MeasureStatusTextWidth() + 24;
            double target = Math.Clamp(desired, baseMin, baseMax);

            StatusText.MaxWidth = Math.Max(0, target - 24);
            StatusText.MaxViewWidth = Math.Max(0, baseMax - 24);

            double h = _state == DisplayState.Hovered ? HoverHeight : CollapsedHeight;
            if (Math.Abs(RootGrid.Width - target) > 0.5 ||
                Math.Abs(RootGrid.Height - h) > 0.5)
            {
                AnimateTo(target, h, 160);
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
                    if (_alerts.Current == null && _state != DisplayState.Alert)
                        _state = IsMouseOver ? DisplayState.Hovered : DisplayState.Collapsed;
                    UpdateFullScreenSuppression();
                    RefreshDisplay();
                }
            });
        }

        private void UpdateFullScreenSuppression()
        {
            bool suppress = DisplaySettings.Instance.EnableFullScreenSuppress
                && _fullScreen.IsFullScreen
                && _alerts.Current == null;

            Visibility = suppress ? Visibility.Hidden : Visibility.Visible;
        }

        // ─── 瞬时提醒 ──────────────────────────────────────────────
        private void OnAlertChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (_alerts.Current != null)
                {
                    if (_state != DisplayState.Alert)
                        _stateBeforeAlert = _state;
                    CancelShrink();
                    _state = DisplayState.Alert;
                    _alertView.DataContext = _alerts.Current;

                    SnapTo(AlertStartWidth, AlertHeight);
                    PlayHeartbeatAnimation();
                    PlaySweepLight();
                    AnimateTo(AlertWidth, AlertHeight, 450);

                    RefreshDisplay();
                    UpdateFullScreenSuppression();
                }
                else
                {
                    CancelShrink();
                    _state = IsMouseOver ? DisplayState.Hovered : DisplayState.Collapsed;
                    RefreshDisplay();
                    if (_state == DisplayState.Hovered) PlayHoverIn();
                    UpdateFullScreenSuppression();
                }
            });
        }

        private void TestAlert_Click(object sender, RoutedEventArgs e)
        {
            _alerts.Enqueue(new SimpleAlert(
                "test", "测试提醒", "这是一条灵动岛提醒", "🔔",
                TimeSpan.FromSeconds(2.5), priority: 10));
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
                FitCollapsedWidthToText();
            else if (_state == DisplayState.Expanded)
                AnimateTo(ExpandedWidth, ExpandedHeight, 200);
            else if (_state == DisplayState.Alert)
                AnimateTo(AlertWidth, AlertHeight, 200);
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
                StartShrink(ShrinkPhase.ToHover, ShrinkDelayToHover);
        }

        // ─── 设置窗 ────────────────────────────────────────────────
        private SettingsWindow? _settingsWindow;
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
                            StartShrink(ShrinkPhase.ToHover, ShrinkDelayToHover);
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
        private void AnimateTo(double baseW, double baseH, int ms)
        {
            var toW = baseW * _scale;
            var toH = baseH * _scale;
            var toLeft = _targetMonitor.Bounds.Left + (_targetMonitor.Bounds.Width - toW) / 2;

            AnimateProperty(this, WidthProperty, toW, ms);
            AnimateProperty(this, HeightProperty, toH, ms);
            AnimateProperty(this, LeftProperty, toLeft, ms);
            AnimateProperty(RootGrid, FrameworkElement.WidthProperty, baseW, ms);
            AnimateProperty(RootGrid, FrameworkElement.HeightProperty, baseH, ms);
        }

        private void AnimateExpandSequenced()
        {
            double baseW = ExpandedWidth;
            double baseH = ExpandedHeight;

            var toW = baseW * _scale;
            var toH = baseH * _scale;
            var toLeft = _targetMonitor.Bounds.Left + (_targetMonitor.Bounds.Width - toW) / 2;

            AnimateProperty(this, WidthProperty, toW, 180);
            AnimateProperty(this, LeftProperty, toLeft, 180);
            AnimateProperty(RootGrid, FrameworkElement.WidthProperty, baseW, 180);

            int token = ++_animationToken;
            DelayedInvoke(180, () =>
            {
                if (token != _animationToken) return;
                AnimateProperty(this, HeightProperty, toH, 120);
                AnimateProperty(RootGrid, FrameworkElement.HeightProperty, baseH, 120);
            });
        }

        private void SnapTo(double baseW, double baseH)
        {
            var toW = baseW * _scale;
            var toH = baseH * _scale;
            var toLeft = _targetMonitor.Bounds.Left + (_targetMonitor.Bounds.Width - toW) / 2;
            Width = toW; Height = toH; Left = toLeft;
            RootGrid.Width = baseW; RootGrid.Height = baseH;
        }

        private void PlayHeartbeatAnimation()
        {
            var grid = _alertView.HeartbeatTarget;
            var scale = grid.RenderTransform as ScaleTransform;
            if (scale == null)
            {
                scale = new ScaleTransform(1, 1);
                grid.RenderTransform = scale;
                grid.RenderTransformOrigin = new Point(0.5, 0.5);
            }

            AnimateDp(scale, ScaleTransform.ScaleXProperty, 0.97, 80, new QuadraticEase { EasingMode = EasingMode.EaseOut });
            AnimateDp(scale, ScaleTransform.ScaleYProperty, 0.97, 80, new QuadraticEase { EasingMode = EasingMode.EaseOut });

            int token1 = ++_animationToken;
            DelayedInvoke(80, () =>
            {
                if (token1 != _animationToken) return;
                AnimateDp(scale, ScaleTransform.ScaleXProperty, 1.02, 220, new QuadraticEase { EasingMode = EasingMode.EaseOut });
                AnimateDp(scale, ScaleTransform.ScaleYProperty, 1.02, 220, new QuadraticEase { EasingMode = EasingMode.EaseOut });
            });

            int token2 = ++_animationToken;
            DelayedInvoke(80 + 220, () =>
            {
                if (token2 != _animationToken) return;
                AnimateDp(scale, ScaleTransform.ScaleXProperty, 1.0, 150, new QuadraticEase { EasingMode = EasingMode.EaseOut });
                AnimateDp(scale, ScaleTransform.ScaleYProperty, 1.0, 150, new QuadraticEase { EasingMode = EasingMode.EaseOut });
            });
        }

        private void PlaySweepLight()
        {
            var sweep = _alertView.SweepLight;
            sweep.Opacity = 0;

            AnimateDp(sweep, UIElement.OpacityProperty, 1, 80, new QuadraticEase { EasingMode = EasingMode.EaseIn });

            var stop = _alertView.SweepStop;
            int token1 = ++_animationToken;
            DelayedInvoke(80, () =>
            {
                if (token1 != _animationToken) return;
                AnimateDp(stop, GradientStop.OffsetProperty, 1.0, 540, new QuadraticEase { EasingMode = EasingMode.EaseInOut });
            });

            int token2 = ++_animationToken;
            DelayedInvoke(80 + 540, () =>
            {
                if (token2 != _animationToken) return;
                AnimateDp(sweep, UIElement.OpacityProperty, 0, 80, new QuadraticEase { EasingMode = EasingMode.EaseOut });
            });
        }

        // ─── Hover 动效 ──────────────────────────────────────────────
        /// <summary>Hover 进入：边框 20%→35%，高光约 30%→50%。</summary>
        private void PlayHoverIn()
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            const int ms = 120;

            AnimateDp(_hoverBorderBrush, System.Windows.Media.Brush.OpacityProperty, 0.35, ms, ease);
            AnimateDp(TopHighlight, UIElement.OpacityProperty, 1.0, ms, ease);
        }

        /// <summary>Hover 退出：回到原始态。onDone 在动画完成后调用。</summary>
        private void PlayHoverOut(Action? onDone = null)
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            const int ms = 100;

            AnimateDp(_hoverBorderBrush, System.Windows.Media.Brush.OpacityProperty, 0.20, ms, ease);
            AnimateDp(TopHighlight, UIElement.OpacityProperty, 0.65, ms, ease, onDone);
        }

        private static void AnimateProperty(FrameworkElement target, DependencyProperty prop, double to, int ms)
        {
            var anim = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = Ease,
                FillBehavior = FillBehavior.Stop
            };
            anim.Completed += (_, _) =>
            {
                target.BeginAnimation(prop, null);
                target.SetCurrentValue(prop, to);
            };
            target.BeginAnimation(prop, anim);
        }

        private static void AnimateDp(DependencyObject target, DependencyProperty prop, double to, double ms,
            IEasingFunction ease, Action? onDone = null)
        {
            var anim = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            anim.Completed += (_, _) =>
            {
                (target as IAnimatable)?.BeginAnimation(prop, null);
                target.SetCurrentValue(prop, to);
                onDone?.Invoke();
            };
            (target as IAnimatable)?.BeginAnimation(prop, anim);
        }

        private static void DelayedInvoke(int ms, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
        }
    }
}
