using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        public SettingsWindow()
        {
            InitializeComponent();
            DataContext = DisplaySettings.Instance;

            // 显示位置：首次填充 + 监听拓扑/索引变化
            PopulateMonitorCombo();
            // 外观：背景材质下拉
            PopulateBackdropCombo();
            // 外观：主题下拉
            PopulateThemeCombo();
            // 主题画刷按当前设置落色（XAML 默认深色，运行时按 IsLight 覆写）
            ApplyTheme();
            DisplaySettings.Instance.PropertyChanged += OnSettingsChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplayTopologyChanged;
            // 系统主题变了（且 ThemeMode=System）要重刷画刷
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            // 开机启动：读注册表回写 UI（ground truth 在注册表，不在 DisplaySettings）
            _suppressAutoStartEvent = true;
            AutoStartCheck.IsChecked = AutoStart.IsEnabled;
            _suppressAutoStartEvent = false;

            Closed += (_, _) =>
            {
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
        private void PopulateMonitorCombo()
        {
            _suppressMonitorComboEvent = true;
            try
            {
                MonitorCombo.Items.Clear();
                var monitors = MonitorEnumerator.EnumerateMonitors();
                foreach (var m in monitors)
                    MonitorCombo.Items.Add(m.Display); // 字符串项，简单够用

                int idx = DisplaySettings.Instance.TargetMonitorIndex;
                if (idx < 0 || idx >= MonitorCombo.Items.Count) idx = 0;
                if (MonitorCombo.Items.Count > 0)
                    MonitorCombo.SelectedIndex = idx;
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
        }

        // ─── 外观：背景材质 ────────────────────────────────────────
        // 项顺序须与 BackdropMode 枚举一致：0=Acrylic / 1=Transparent / 2=Mica
        private void PopulateBackdropCombo()
        {
            _suppressBackdropComboEvent = true;
            try
            {
                BackdropCombo.Items.Clear();
                BackdropCombo.Items.Add("亚克力（模糊）");
                BackdropCombo.Items.Add("全透明");
                BackdropCombo.Items.Add("云母（平涂）");
                int idx = (int)DisplaySettings.Instance.BackdropMode;
                if (idx < 0 || idx >= BackdropCombo.Items.Count) idx = 0;
                BackdropCombo.SelectedIndex = idx;
            }
            finally { _suppressBackdropComboEvent = false; }
        }

        private void BackdropCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressBackdropComboEvent) return;
            int idx = BackdropCombo.SelectedIndex;
            if (idx < 0) return;
            DisplaySettings.Instance.BackdropMode = (BackdropMode)idx;
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
            else if (e.PropertyName == nameof(DisplaySettings.BackdropMode))
            {
                Dispatcher.Invoke(() =>
                {
                    int idx = (int)DisplaySettings.Instance.BackdropMode;
                    if (idx < 0 || idx >= BackdropCombo.Items.Count) idx = 0;
                    if (BackdropCombo.Items.Count > 0 && BackdropCombo.SelectedIndex != idx)
                    {
                        _suppressBackdropComboEvent = true;
                        try { BackdropCombo.SelectedIndex = idx; }
                        finally { _suppressBackdropComboEvent = false; }
                    }
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
