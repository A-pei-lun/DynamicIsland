using System.Windows;
using DynamicIsland.Island;

namespace DynamicIsland.Sources
{
    /// <summary>
    /// 媒体控制页：复用 <see cref="MediaExpandedView"/>，DataContext 绑到 <see cref="MediaSource"/>。
    /// **常驻可用**——无媒体会话时由 MediaExpandedView 内的空状态层显示"🎵 暂无媒体播放"占位，
    /// 不再随 HasMedia 隐藏整页，翻页时页数稳定。
    ///
    /// Order=10，排第一页。
    /// </summary>
    public sealed class MediaPanel : IIslandPanel
    {
        private readonly MediaExpandedView _view;

        public MediaPanel(MediaSource media)
        {
            _view = new MediaExpandedView { DataContext = media };
        }

        public string Id => "media";
        public int Order => 10;

        // 常驻：无媒体会话时显示占位，不隐藏整页
        public bool IsAvailable => true;

        public FrameworkElement View => _view;

        // 常驻页，永不失效，无可用性变化事件
#pragma warning disable CS0067 // 事件未使用（接口要求）
        public event EventHandler? AvailabilityChanged;
#pragma warning restore CS0067
    }
}
