using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DynamicIsland
{
    /// <summary>
    /// 独立设置窗口。DataContext = DisplaySettings.Instance（INPC 单例，UI 双向绑定即时生效）。
    /// 改值后 500ms debounce 自动落盘到 %AppData%\DynamicIsland\settings.json。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        // 抑制 ComboBox.SelectionChanged 与 DisplaySettings 的回环：
        // 程序填充 / 反向回灌时会触发 SelectionChanged，要避免它再写回 DisplaySettings。
        private bool _suppressMonitorComboEvent;
        private bool _suppressBackdropComboEvent;
        private bool _suppressThemeComboEvent;
        private bool _suppressCurveComboEvent;
        private bool _suppressTextColorComboEvent;
        private bool _suppressCaptureComboEvent;

        public SettingsWindow()
        {
            InitializeComponent();
            DataContext = DisplaySettings.Instance;

            // 显示位置：首次填充 + 监听拓扑/索引变化
            PopulateMonitorCombo();
            // 外观：背景材质下拉
            PopulateBackdropCombo();
            // 外观：液态玻璃抓屏后端下拉
            PopulateCaptureCombo();
            // 实际后端名刷新（Auto 模式可能回退 Hlsl，定时显示真实后端）
            var backendTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            backendTimer.Tick += (_, _) => UpdateActiveBackend();
            backendTimer.Start();
            UpdateActiveBackend();
            // 外观：主题下拉
            PopulateThemeCombo();
            // 外观：动画曲线下拉
            PopulateCurveCombo();
            // 外观：文字颜色下拉
            PopulateTextColorCombo();
            // 主题画刷按当前设置落色（XAML 默认深色，运行时按 IsLight 覆写）
            ApplyTheme();
            // 按当前材质模式切亚克力/液态玻璃参数段显隐
            UpdateBackdropParamVisibility();
            UpdateBranding();
            DisplaySettings.Instance.PropertyChanged += OnSettingsChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplayTopologyChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            // 开机启动
            _suppressAutoStartEvent = true;
            AutoStartCheck.IsChecked = AutoStart.IsEnabled;
            _suppressAutoStartEvent = false;

            // 滑块彩虹：初始化 + 监听 IsUltraMode
            ApplySliderRainbow(DisplaySettings.Instance.IsUltraMode);

            Closed += (_, _) =>
            {
                if (_rainbowActive)
                {
                    _rainbowActive = false;
                    CompositionTarget.Rendering -= OnSliderRainbowFrame;
                }
                DisplaySettings.Instance.PropertyChanged -= OnSettingsChanged;
                SystemEvents.DisplaySettingsChanged -= OnDisplayTopologyChanged;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            };
        }

        /// <summary>
        /// 开机启动复选框：写注册表 HKCU\...\Run\DynamicIsland。
        /// _suppressAutoStartEvent 防止程序初始化时 IsChecked 赋值触发回环。
        /// </summary>
        private bool _suppressAutoStartEvent;
        private void AutoStartCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressAutoStartEvent) return;
            bool desired = AutoStartCheck.IsChecked == true;
            bool ok = AutoStart.SetEnabled(desired);
            if (!ok)
            {
                // 写注册表失败（极少见）：回滚 UI 到真实状态
                _suppressAutoStartEvent = true;
                AutoStartCheck.IsChecked = AutoStart.IsEnabled;
                _suppressAutoStartEvent = false;
            }
        }

        /// <summary>
        /// 恢复默认：二次确认后全字段回滚 + 立即写盘。INPC 推回 UI 自动刷新（Slider/CheckBox 都跟着回原值）。
        /// </summary>
        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                this,
                "确定要将所有设置恢复为默认值吗？\n\n这会重置所有显示项、阈值与提醒开关。",
                "恢复默认设置",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);

            if (result == MessageBoxResult.OK)
            {
                DisplaySettings.ResetToDefaults();
            }
        }

        // ─── 显示位置 ──────────────────────────────────────────────
        private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();

        private void PopulateMonitorCombo()
        {
            _suppressMonitorComboEvent = true;
            try
            {
                _monitors = MonitorEnumerator.EnumerateMonitors();
                MonitorCombo.Items.Clear();
                foreach (var m in _monitors)
                    MonitorCombo.Items.Add(m.Display + $" @{m.RefreshRate}Hz");

                int idx = DisplaySettings.Instance.TargetMonitorIndex;
                if (idx < 0 || idx >= MonitorCombo.Items.Count) idx = 0;
                if (MonitorCombo.Items.Count > 0)
                    MonitorCombo.SelectedIndex = idx;

                // 同步抓屏帧率上限 = 当前显示器刷新率
                SyncMaxFps(idx);
            }
            finally
            {
                _suppressMonitorComboEvent = false;
            }
        }

        private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressMonitorComboEvent) return;
            int idx = MonitorCombo.SelectedIndex;
            if (idx < 0) return;
            if (DisplaySettings.Instance.TargetMonitorIndex != idx)
                DisplaySettings.Instance.TargetMonitorIndex = idx;
            SyncMaxFps(idx);
        }

        private void SyncMaxFps(int monitorIndex)
        {
            if (monitorIndex >= 0 && monitorIndex < _monitors.Count)
                DisplaySettings.Instance.MaxGlassCaptureFps = _monitors[monitorIndex].RefreshRate;
        }

        // ─── 外观：背景材质 ────────────────────────────────────────
        // 项顺序须与 BackdropMode 枚举一致：0=Acrylic / 1=Transparent / 2=Mica / 3=LiquidGlass
        private void PopulateBackdropCombo()
        {
            _suppressBackdropComboEvent = true;
            try
            {
                BackdropCombo.Items.Clear();
                BackdropCombo.Items.Add("亚克力（模糊）");   // BackdropMode.Acrylic = 0
                BackdropCombo.Items.Add("全透明");           // BackdropMode.Transparent = 1
                BackdropCombo.Items.Add("云母（平涂）");     // BackdropMode.Mica = 2
                if (DisplaySettings.Instance.ProMode)
                    BackdropCombo.Items.Add("液态玻璃");     // BackdropMode.LiquidGlass = 3

                int mode = (int)DisplaySettings.Instance.BackdropMode;
                int idx = ComboIndexFromMode(mode);
                BackdropCombo.SelectedIndex = idx;
            }
            finally { _suppressBackdropComboEvent = false; }
        }

        private static int ComboIndexFromMode(int mode)
        {
            // LiquidGlass (3) without Pro: clamp to Acrylic (0)
            if (mode == (int)BackdropMode.LiquidGlass && !DisplaySettings.Instance.ProMode)
                return 0;
            if (mode < 0) return 0;
            return mode; // Acrylic=0, Transparent=1, Mica=2, LiquidGlass=3 (Pro only)
        }

        private BackdropMode ModeFromComboIndex(int idx)
        {
            bool pro = DisplaySettings.Instance.ProMode;
            // Without Pro, indices are Acrylic→0, Transparent→1, Mica→2
            // With Pro, indices are Acrylic→0, Transparent→1, Mica→2, LiquidGlass→3
            return (BackdropMode)idx;
        }

        private void BackdropCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressBackdropComboEvent) return;
            int idx = BackdropCombo.SelectedIndex;
            if (idx < 0) return;
            var newMode = ModeFromComboIndex(idx);
            DisplaySettings.Instance.BackdropMode = newMode;
            UpdateBackdropParamVisibility();
        }

        // ─── 外观：液态玻璃抓屏后端 ──────────────────────────────
        // 项顺序须与 CaptureMode 枚举一致：0=Auto / 1=Gpu / 2=Hlsl
        private void PopulateCaptureCombo()
        {
            _suppressCaptureComboEvent = true;
            try
            {
                CaptureCombo.Items.Clear();
                CaptureCombo.Items.Add("Auto（GPU 优先）");  // CaptureMode.Auto = 0
                CaptureCombo.Items.Add("GPU 硬件加速");      // CaptureMode.Gpu = 1
                CaptureCombo.Items.Add("HLSL 兼容模式");     // CaptureMode.Hlsl = 2
                CaptureCombo.SelectedIndex = (int)DisplaySettings.Instance.CaptureMode;
            }
            finally { _suppressCaptureComboEvent = false; }
        }

        private void CaptureCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCaptureComboEvent) return;
            int idx = CaptureCombo.SelectedIndex;
            if (idx < 0) return;
            DisplaySettings.Instance.CaptureMode = (CaptureMode)idx;
        }

        /// <summary>显示当前实际生效的玻璃后端名（查 MainWindow 单例；Auto 可能已回退）。</summary>
        private void UpdateActiveBackend()
        {
            ActiveBackendText.Text = "当前生效：" + (MainWindow.Instance?.GlassBackendName ?? "未启动");
        }

        /// <summary>
        /// 按当前材质模式切“亚克力 / 液态玻璃”参数段显隐。
        /// AcrylicSection 仅 Acrylic 模式显示（模糊开关 + 底色浓度）；GlassSection 仅 LiquidGlass 模式显示（半径/底色/金边/帧率）。
        /// 其余模式（全透明 / 云母）两者皆隐。在 ctor 末尾、BackdropCombo 改选、OnSettingsChanged(BackdropMode) 各调一次。
        /// </summary>
        private void UpdateBackdropParamVisibility()
        {
            var mode = DisplaySettings.Instance.BackdropMode;
            AcrylicSection.Visibility = mode == BackdropMode.Acrylic ? Visibility.Visible : Visibility.Collapsed;
            GlassSection.Visibility = mode == BackdropMode.LiquidGlass ? Visibility.Visible : Visibility.Collapsed;
        }

        // ─── 外观：主题 ──────────────────────────────────────────
        // 项顺序须与 ThemeMode 枚举一致：0=System / 1=Light / 2=Dark
        private void PopulateThemeCombo()
        {
            _suppressThemeComboEvent = true;
            try
            {
                ThemeCombo.Items.Clear();
                ThemeCombo.Items.Add("跟随系统");
                ThemeCombo.Items.Add("浅色");
                ThemeCombo.Items.Add("深色");
                int idx = (int)DisplaySettings.Instance.ThemeMode;
                if (idx < 0 || idx >= ThemeCombo.Items.Count) idx = 0;
                ThemeCombo.SelectedIndex = idx;
            }
            finally { _suppressThemeComboEvent = false; }
        }

        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressThemeComboEvent) return;
            int idx = ThemeCombo.SelectedIndex;
            if (idx < 0) return;
            DisplaySettings.Instance.ThemeMode = (ThemeMode)idx;
        }

        private void PopulateCurveCombo()
        {
            _suppressCurveComboEvent = true;
            try
            {
                CurveCombo.Items.Clear();
                foreach (var c in MotionToken.CurvePresets)
                    CurveCombo.Items.Add(c.Name);
                int idx = DisplaySettings.Instance.MotionCurveIndex;
                if (idx < 0 || idx >= CurveCombo.Items.Count) idx = 0;
                CurveCombo.SelectedIndex = idx;
            }
            finally { _suppressCurveComboEvent = false; }
        }

        private void PopulateTextColorCombo()
        {
            _suppressTextColorComboEvent = true;
            try
            {
                TextColorCombo.Items.Clear();
                foreach (var c in DisplaySettings.TextColorPresets)
                    TextColorCombo.Items.Add(c.Name);
                int idx = DisplaySettings.Instance.TextColorIndex;
                if (idx < 0 || idx >= TextColorCombo.Items.Count) idx = 0;
                TextColorCombo.SelectedIndex = idx;
            }
            finally { _suppressTextColorComboEvent = false; }
        }

        private void TextColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressTextColorComboEvent) return;
            int idx = TextColorCombo.SelectedIndex;
            if (idx < 0) return;
            DisplaySettings.Instance.TextColorIndex = idx;
        }

        private void CurveCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCurveComboEvent) return;
            int idx = CurveCombo.SelectedIndex;
            if (idx < 0) return;
            DisplaySettings.Instance.MotionCurveIndex = idx;
        }

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DisplaySettings.TargetMonitorIndex))
            {
                // 外部改了索引（如 ResetToDefaults）→ 同步 ComboBox 选中
                Dispatcher.Invoke(() =>
                {
                    int idx = DisplaySettings.Instance.TargetMonitorIndex;
                    if (idx < 0 || idx >= MonitorCombo.Items.Count) idx = 0;
                    if (MonitorCombo.Items.Count > 0 && MonitorCombo.SelectedIndex != idx)
                    {
                        _suppressMonitorComboEvent = true;
                        try { MonitorCombo.SelectedIndex = idx; }
                        finally { _suppressMonitorComboEvent = false; }
                    }
                });
            }
            else if (e.PropertyName == nameof(DisplaySettings.CaptureMode))
            {
                Dispatcher.Invoke(() =>
                {
                    int idx = (int)DisplaySettings.Instance.CaptureMode;
                    if (CaptureCombo.Items.Count > 0 && CaptureCombo.SelectedIndex != idx)
                    {
                        _suppressCaptureComboEvent = true;
                        try { CaptureCombo.SelectedIndex = idx; }
                        finally { _suppressCaptureComboEvent = false; }
                    }
                });
            }
            else if (e.PropertyName == nameof(DisplaySettings.BackdropMode))
            {
                Dispatcher.Invoke(() =>
                {
                    int idx = ComboIndexFromMode((int)DisplaySettings.Instance.BackdropMode);
                    if (BackdropCombo.Items.Count > 0 && BackdropCombo.SelectedIndex != idx)
                    {
                        _suppressBackdropComboEvent = true;
                        try { BackdropCombo.SelectedIndex = idx; }
                        finally { _suppressBackdropComboEvent = false; }
                    }
                    UpdateBackdropParamVisibility();
                });
            }
            else if (e.PropertyName == nameof(DisplaySettings.ThemeMode))
            {
                Dispatcher.Invoke(() =>
                {
                    int idx = (int)DisplaySettings.Instance.ThemeMode;
                    if (idx < 0 || idx >= ThemeCombo.Items.Count) idx = 0;
                    if (ThemeCombo.Items.Count > 0 && ThemeCombo.SelectedIndex != idx)
                    {
                        _suppressThemeComboEvent = true;
                        try { ThemeCombo.SelectedIndex = idx; }
                        finally { _suppressThemeComboEvent = false; }
                    }
                    ApplyTheme();
                });
            }
            else if (e.PropertyName == nameof(DisplaySettings.TextColorIndex))
            {
                Dispatcher.Invoke(() =>
                {
                    int idx = DisplaySettings.Instance.TextColorIndex;
                    if (idx < 0 || idx >= TextColorCombo.Items.Count) idx = 0;
                    if (TextColorCombo.Items.Count > 0 && TextColorCombo.SelectedIndex != idx)
                    {
                        _suppressTextColorComboEvent = true;
                        try { TextColorCombo.SelectedIndex = idx; }
                        finally { _suppressTextColorComboEvent = false; }
                    }
                });
            }
            else if (e.PropertyName == nameof(DisplaySettings.MotionCurveIndex))
            {
                Dispatcher.Invoke(() =>
                {
                    int idx = DisplaySettings.Instance.MotionCurveIndex;
                    if (idx < 0 || idx >= CurveCombo.Items.Count) idx = 0;
                    if (CurveCombo.Items.Count > 0 && CurveCombo.SelectedIndex != idx)
                    {
                        _suppressCurveComboEvent = true;
                        try { CurveCombo.SelectedIndex = idx; }
                        finally { _suppressCurveComboEvent = false; }
                    }
                });
            }
            else if (e.PropertyName == nameof(DisplaySettings.ProMode))
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateBranding();
                    if (!DisplaySettings.Instance.ProMode
                        && DisplaySettings.Instance.BackdropMode == BackdropMode.LiquidGlass)
                    {
                        DisplaySettings.Instance.BackdropMode = BackdropMode.Acrylic;
                    }
                    PopulateBackdropCombo();
                    UpdateBackdropParamVisibility();
                });
            }
            else if (e.PropertyName == nameof(DisplaySettings.IsUltraMode))
            {
                Dispatcher.Invoke(() => ApplySliderRainbow(DisplaySettings.Instance.IsUltraMode));
            }
        }

        private void UpdateBranding()
        {
            bool pro = DisplaySettings.Instance.ProMode;
            string name = pro ? "DynamicIsland Pro" : "DynamicIsland";
            SidebarBrand.Text = name;
            AboutAppName.Text = name;
        }

        private void OnDisplayTopologyChanged(object? sender, EventArgs e)
        {
            // 接拔屏：刷新列表
            Dispatcher.Invoke(PopulateMonitorCombo);
        }

        // ─── 标题栏深色（DWM，配合主题）──────────────────────────
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int cbValue);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyTitleBarDark();
        }

        /// <summary>按当前主题把标题栏刷成深/浅色（DWMWA_USE_IMMERSIVE_DARK_MODE）。SourceInitialized 之后才有效。</summary>
        private void ApplyTitleBarDark()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int dark = DisplaySettings.Instance.IsLight() ? 0 : 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
            catch { }
        }

        // ─── 主题：设置窗跟随主题 ──────────────────────────────────
        // Win11 设置风格调色板：实色卡片 + 强调色 + 细分割线。覆写 Window.Resources 语义画刷，
        // DynamicResource 引用即时联动。深/浅两套分别给全量画刷（含强调色、轨道、分割线等）。
        private void ApplyTheme()
        {
            bool isLight = DisplaySettings.Instance.IsLight();

            Resources["SettingsWindowBg"] = new SolidColorBrush(isLight ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x1A, 0x1A, 0x1A));
            Resources["SettingsNavBg"]    = new SolidColorBrush(isLight ? Color.FromRgb(0xED, 0xED, 0xED) : Color.FromRgb(0x1F, 0x1F, 0x1F));
            Resources["SettingsText"]     = new SolidColorBrush(isLight ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Colors.White);
            Resources["SettingsTextMuted"]= new SolidColorBrush(isLight ? Color.FromArgb(0x66, 0x00, 0x00, 0x00) : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

            if (isLight)
            {
                Resources["SettingsCardBg"]       = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                Resources["SettingsCardBorder"]   = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE5));
                Resources["SettingsControlBg"]    = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                Resources["SettingsControlBorder"]= new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xC7));
                Resources["SettingsInputBg"]      = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                Resources["SettingsPopupBg"]      = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                Resources["SettingsAccent"]       = new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0));
                Resources["SettingsAccentSoft"]   = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x67, 0xC0));
                Resources["SettingsNavHoverBg"]   = new SolidColorBrush(Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
                Resources["SettingsToggleOff"]    = new SolidColorBrush(Color.FromRgb(0x8B, 0x8B, 0x8B));
                Resources["SettingsTrackBg"]      = new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xC7));
                Resources["SettingsDivider"]      = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00));
            }
            else
            {
                Resources["SettingsCardBg"]       = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
                Resources["SettingsCardBorder"]   = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
                Resources["SettingsControlBg"]    = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                Resources["SettingsControlBorder"]= new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
                Resources["SettingsInputBg"]      = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                Resources["SettingsPopupBg"]      = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
                Resources["SettingsAccent"]       = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
                Resources["SettingsAccentSoft"]   = new SolidColorBrush(Color.FromArgb(0x33, 0x4C, 0xC2, 0xFF));
                Resources["SettingsNavHoverBg"]   = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
                Resources["SettingsToggleOff"]    = new SolidColorBrush(Color.FromRgb(0x5C, 0x5C, 0x5C));
                Resources["SettingsTrackBg"]      = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
                Resources["SettingsDivider"]      = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            }

            ApplyTitleBarDark();
        }

        private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
        {
            // ThemeMode=System 时，系统切浅/深色 → 重刷画刷（ThemeMode 属性本身没变，INPC 不会触发）
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
                Dispatcher.Invoke(ApplyTheme);
        }

        /// <summary>
        /// 浏览并选择下载监控文件夹。选择后写入 DisplaySettings.DownloadFolderPath。
        /// </summary>
        /// <summary>动画设置保存按钮：立即写盘（不等 500ms debounce）。</summary>
        private void SaveAnimation_Click(object sender, RoutedEventArgs e)
        {
            DisplaySettings.SaveNow();
            var origBg = SaveAnimationBtn.Background;
            SaveAnimationBtn.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            timer.Tick += (_, _) => { timer.Stop(); SaveAnimationBtn.Background = origBg; };
            timer.Start();
        }

        // ─── 滑块彩虹（帧率拉满时滑块轨道 + "MAX" 文字变彩虹）──────
        private LinearGradientBrush? _sliderRainbowBrush;
        private System.Windows.Controls.Border? _sliderFillBorder;
        private bool _rainbowActive;

        private System.Windows.Controls.Border? FindSliderFillBorder()
        {
            FpsSlider.ApplyTemplate();
            var track = FpsSlider.Template.FindName("PART_Track", FpsSlider) as System.Windows.Controls.Primitives.Track;
            var btn = track?.DecreaseRepeatButton;
            if (btn == null) return null;
            btn.ApplyTemplate();
            return btn.Template.FindName("Bd", btn) as System.Windows.Controls.Border
                ?? System.Windows.Media.VisualTreeHelper.GetChild(btn, 0) as System.Windows.Controls.Border;
        }

        private void ApplySliderRainbow(bool active)
        {
            _sliderFillBorder ??= FindSliderFillBorder();
            if (_sliderFillBorder == null) return;

            if (active)
            {
                _sliderRainbowBrush = new LinearGradientBrush
                {
                    StartPoint = new System.Windows.Point(0, 0),
                    EndPoint = new System.Windows.Point(1, 0),
                };
                for (int i = 0; i < 6; i++)
                    _sliderRainbowBrush.GradientStops.Add(new GradientStop(Colors.Red, i / 5.0));

                _sliderFillBorder.Background = _sliderRainbowBrush;

                if (!_rainbowActive)
                {
                    _rainbowActive = true;
                    CompositionTarget.Rendering += OnSliderRainbowFrame;
                }
            }
            else
            {
                if (_rainbowActive)
                {
                    _rainbowActive = false;
                    CompositionTarget.Rendering -= OnSliderRainbowFrame;
                }
                _sliderFillBorder.Background = (Brush)Resources["SettingsAccent"];
                FpsValueText.Foreground = (Brush)Resources["SettingsText"];
            }
        }

        private void OnSliderRainbowFrame(object? sender, EventArgs e)
        {
            var b = _sliderRainbowBrush;
            if (b == null) return;
            double t = (Environment.TickCount % 3000) / 3000.0;
            for (int i = 0; i < b.GradientStops.Count; i++)
            {
                double hue = (t + i / (double)b.GradientStops.Count) % 1.0;
                var color = ColorUtils.HsvToColor(hue * 360.0, 0.85, 1.0);
                b.GradientStops[i].Color = color;
                b.GradientStops[i].Offset = (t + i / (double)b.GradientStops.Count) % 1.0;
            }
            double textHue = (t + 0.5) % 1.0;
            FpsValueText.Foreground = new SolidColorBrush(ColorUtils.HsvToColor(textHue * 360.0, 0.9, 1.0));
        }

        private void BrowseDownloadFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择下载监控文件夹",
                InitialDirectory = DisplaySettings.Instance.DownloadFolderPath,
            };

            // 如果当前路径为空或无效，默认定位到系统 Downloads
            string currentPath = DisplaySettings.Instance.DownloadFolderPath;
            if (string.IsNullOrWhiteSpace(currentPath) || !Directory.Exists(currentPath))
            {
                dialog.InitialDirectory = Alerts.DownloadAlertSource.ResolveWatchFolder();
            }

            if (dialog.ShowDialog() == true)
            {
                DisplaySettings.Instance.DownloadFolderPath = dialog.FolderName;
            }
        }
    }
}
