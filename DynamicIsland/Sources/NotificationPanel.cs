using System;
using System.Windows;
using DynamicIsland.Alerts;
using DynamicIsland.Island;

namespace DynamicIsland.Sources
{
    /// <summary>
    /// 最近通知页：展开态的"最近通知"页。**常驻可用**——即使历史为空也占位
    /// （显示"📭 暂无通知"），翻页时页数稳定、用户始终知道有这页可查。
    /// 列表视图 <see cref="NotificationListView"/> 监听 AlertHost.HistoryChanged 自更新。
    ///
    /// Order=20，排第二页（媒体=10 之后）。
    /// </summary>
    public sealed class NotificationPanel : IIslandPanel
    {
        private readonly NotificationListView _view;

        public NotificationPanel(AlertHost alerts)
        {
            _view = new NotificationListView(alerts);
        }

        public string Id => "notifications";
        public int Order => 20;

        // 常驻：即使历史为空也展示（空列表显示占位提示，不隐藏整页）
        public bool IsAvailable => true;

        public FrameworkElement View => _view;

        // 常驻页，永不失效，无可用性变化事件
#pragma warning disable CS0067 // 事件未使用（接口要求）
        public event EventHandler? AvailabilityChanged;
#pragma warning restore CS0067
    }
}
