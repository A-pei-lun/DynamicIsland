using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

        public SettingsWindow()
        {
            InitializeComponent();
            DataContext = DisplaySettings.Instance;

            // 显示位置：首次填充 + 监听拓扑/索引变化
            PopulateMonitorCombo();
            DisplaySettings.Instance.PropertyChanged += OnSettingsChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplayTopologyChanged;

            // 开机启动：读注册表回写 UI（ground truth 在注册表，不在 DisplaySettings）
            _suppressAutoStartEvent = true;
            AutoStartCheck.IsChecked = AutoStart.IsEnabled;
            _suppressAutoStartEvent = false;

            Closed += (_, _) =>
            {
                DisplaySettings.Instance.PropertyChanged -= OnSettingsChanged;
                SystemEvents.DisplaySettingsChanged -= OnDisplayTopologyChanged;
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
        }

        private void OnDisplayTopologyChanged(object? sender, EventArgs e)
        {
            // 接拔屏：刷新列表
            Dispatcher.Invoke(PopulateMonitorCombo);
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
