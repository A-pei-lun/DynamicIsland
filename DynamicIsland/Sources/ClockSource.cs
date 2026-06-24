using System;
using System.Windows;
using System.Windows.Threading;
using DynamicIsland.Island;

namespace DynamicIsland.Sources
{
    /// <summary>
    /// 兜底数据源：始终 active，显示当前时间。
    /// 没有其他源（比如音乐）顶上时由它保底，灵动岛不会出现"空白"状态。
    /// </summary>
    public sealed class ClockSource : IIslandSource
    {
        private DispatcherTimer? _timer;

        public string Id => "clock";

        public int Priority => 0;            // 永远最低，让所有"真正的信息"都能盖过它

        public bool IsActive => true;        // 兜底，永不熄灭

        public string CollapsedText => $"🕒 {DateTime.Now:HH:mm}";

        public string? ExpandedText
        {
            get
            {
                var now = DateTime.Now;
                return $"🕒 {now:yyyy-MM-dd dddd}\n{now:HH:mm:ss}";
            }
        }

        // 时钟用纯文字就够，不自带视图
        public FrameworkElement? ExpandedView => null;

        public event EventHandler? Changed;

        public void Start()
        {
            // 1 秒一次：HH:mm 在收起态只有分钟跳变会被看到，但展开态需要秒级精度
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            _timer.Start();
        }

        public void Stop()
        {
            _timer?.Stop();
            _timer = null;
        }

        public void Dispose() => Stop();
    }
}
