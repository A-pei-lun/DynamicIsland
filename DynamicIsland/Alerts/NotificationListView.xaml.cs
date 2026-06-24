using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DynamicIsland.Island;

namespace DynamicIsland.Alerts
{
    /// <summary>
    /// 最近通知列表视图。绑定 <see cref="AlertHost.History"/>，展示最近 N 条提醒。
    /// 历史变化时重建列表；定时器（15s）刷新各条目的相对时间文案。
    /// </summary>
    public partial class NotificationListView : UserControl
    {
        private readonly AlertHost _host;
        private readonly DispatcherTimer _refreshTimer;
        private IReadOnlyList<AlertHistoryEntry> _entries = Array.Empty<AlertHistoryEntry>();

        public NotificationListView(AlertHost host)
        {
            InitializeComponent();
            _host = host;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _refreshTimer.Tick += (_, _) => RefreshRelativeTimes();
            // 仅本视图可见时跑定时器（Loaded/Unloaded 由 dashboard 翻页触发）——省电
            Loaded += (_, _) =>
            {
                Rebuild();
                _host.HistoryChanged += OnHistoryChanged;
                _refreshTimer.Start();
            };
            Unloaded += (_, _) =>
            {
                _refreshTimer.Stop();
                _host.HistoryChanged -= OnHistoryChanged;
            };
        }

        private void OnHistoryChanged(object? sender, EventArgs e)
        {
            // 历史变了（新提醒来了）：重建列表 + 立即算一次相对时间
            Rebuild();
            RefreshRelativeTimes();
        }

        /// <summary>
        /// 历史条目动作按钮点击：重新执行该条提醒当时的动作（如下载完成→再次打开文件所在目录）。
        /// sender 是 Button，DataContext 是 AlertHistoryEntry；Callback 是闭包捕获当时上下文，仍有效。
        /// 失败静默（与实时提醒动作按钮一致），不关闭任何东西（历史条目不是当前提醒）。
        /// </summary>
        private void ItemAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn
                && btn.DataContext is AlertHistoryEntry entry
                && entry.Action != null)
            {
                try { entry.Action.Callback(); }
                catch { /* 动作失败不卡住 UI */ }
                e.Handled = true;
            }
        }

        private void Rebuild()
        {
            // 按重要程度（Priority）降序排序。OrderByDescending 是稳定排序，同优先级保持
            // 原相对顺序——History 本身 Insert(0) 已是时间倒序，故同级别内仍是最新在前。
            // 防止重要通知（低电量 80 等）被后续低优先级通知挤到列表下方看不到。
            _entries = _host.History.OrderByDescending(e => e.Alert.Priority).ToList();
            List.ItemsSource = _entries;
        }

        /// <summary>遍历当前列表条目，按各条 Time 重算相对时间文案。</summary>
        private void RefreshRelativeTimes()
        {
            if (_entries.Count == 0) return;
            var now = DateTime.Now;
            foreach (var e in _entries)
                e.RelativeTime = FormatRelative(now - e.Time, e.Time);
        }

        /// <summary>把时间差转成"刚刚/30秒前/3分钟前/2小时前/M-d HH:mm"等文案。</summary>
        private static string FormatRelative(TimeSpan delta, DateTime time)
        {
            if (delta.TotalSeconds < 15) return "刚刚";
            if (delta.TotalMinutes < 1) return $"{(int)delta.TotalSeconds}秒前";
            if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}分钟前";
            if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}小时前";
            // 超过一天：相对文案太粗，直接显示日期时间
            return time.ToString("M-d HH:mm");
        }
    }
}
