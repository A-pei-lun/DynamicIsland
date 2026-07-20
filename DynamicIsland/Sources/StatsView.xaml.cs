using System.Windows;
using System.Windows.Controls;
using DynamicIsland.Island;

namespace DynamicIsland.Sources
{
    /// <summary>
    /// 统计页视图：展示提醒总数、统计天数、各类型累计次数。
    /// 订阅 <see cref="AlertStats.Changed"/> 实时刷新——每次有新提醒展示时统计页跟着更新。
    /// 清空按钮调 <see cref="AlertStats.Clear"/>（带二次确认）。
    /// </summary>
    public partial class StatsView : UserControl
    {
        private readonly AlertStats _stats;

        public StatsView(AlertStats stats)
        {
            InitializeComponent();
            _stats = stats;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _stats.Changed += OnStatsChanged;
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _stats.Changed -= OnStatsChanged;
        }

        private void OnStatsChanged(object? sender, System.EventArgs e) => Refresh();

        /// <summary>重算总数/天数/类型列表并刷新绑定。</summary>
        private void Refresh()
        {
            OverviewText.Text = $"共 {_stats.Total} 条 · {_stats.Days} 天";
            TypeList.ItemsSource = _stats.ByType;
            ClearButton.IsEnabled = _stats.Total > 0;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            // 二次确认防误清（与设置窗"恢复默认"一致）
            var result = MessageBox.Show("确定清空所有提醒统计吗？此操作不可撤销。",
                "清空统计", MessageBoxButton.OKCancel, MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (result != MessageBoxResult.OK) return;

            _stats.Clear();
        }
    }
}
