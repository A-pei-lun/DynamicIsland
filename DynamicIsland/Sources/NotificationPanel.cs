using System;
using System.Windows;
using DynamicIsland.Alerts;
using DynamicIsland.Island;

namespace DynamicIsland.Sources
{
    /// <summary>
    /// 最近通知页：展开态的"最近通知"页。有历史记录时才可用（IsAvailable）。
    /// 列表视图 <see cref="NotificationListView"/> 监听 AlertHost.HistoryChanged 自更新。
    ///
    /// Order=20，排第二页（媒体=10 之后）。历史为空时不参与翻页（不占指示点）。
    /// </summary>
    public sealed class NotificationPanel : IIslandPanel
    {
        private readonly AlertHost _alerts;
        private readonly NotificationListView _view;

        public NotificationPanel(AlertHost alerts)
        {
            _alerts = alerts;
            _view = new NotificationListView(alerts);
            _alerts.HistoryChanged += OnHistoryChanged;
        }

        public string Id => "notifications";
        public int Order => 20;

        // 有历史记录时此页才可用，避免空列表占位
        public bool IsAvailable => _alerts.History.Count > 0;

        public FrameworkElement View => _view;

        public event EventHandler? AvailabilityChanged;

        private void OnHistoryChanged(object? sender, EventArgs e)
        {
            // 历史从空→非空 或 非空→空 时，IsAvailable 翻转，通知 dashboard 重算可用页
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
