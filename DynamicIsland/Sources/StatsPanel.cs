using System;
using System.Windows;
using DynamicIsland.Island;

namespace DynamicIsland.Sources
{
    /// <summary>
    /// 统计页：展开态的"提醒统计"页。常驻可用——统计是长期累计的，即使为 0 也显示（不像通知页空了就隐藏）。
    /// View <see cref="StatsView"/> 订阅 AlertStats.Changed 自更新。
    ///
    /// Order=30，排第三页（媒体=10、通知=20 之后）。统计是长期累计的常驻页，放最后。
    /// </summary>
    public sealed class StatsPanel : IIslandPanel
    {
        private readonly StatsView _view;

        public StatsPanel(AlertStats stats)
        {
            _view = new StatsView(stats);
        }

        public string Id => "stats";
        public int Order => 30;

        // 统计页常驻：即使总数为 0 也展示（让用户知道有这页、可查统计）
        public bool IsAvailable => true;

        public FrameworkElement View => _view;

        // 常驻页，永不失效，无可用性变化事件
#pragma warning disable CS0067 // 事件未使用（接口要求）
        public event EventHandler? AvailabilityChanged;
#pragma warning restore CS0067
    }
}
