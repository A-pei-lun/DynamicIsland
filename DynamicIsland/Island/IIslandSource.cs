using System;
using System.Windows;

namespace DynamicIsland.Island
{
    /// <summary>
    /// 持续型数据源。每个功能（时钟、音乐、系统资源……）实现一个。
    /// 当数据变了就通过 <see cref="Changed"/> 通知 <see cref="IslandHost"/>，
    /// 由 host 仲裁出当前应该展示哪个源。
    /// </summary>
    public interface IIslandSource : IDisposable
    {
        /// <summary>唯一标识，例如 "clock"、"media"。便于日志、设置面板引用。</summary>
        string Id { get; }

        /// <summary>
        /// 优先级。多个源同时 IsActive 时，数值最高的胜出。
        /// 约定：兜底类（时钟）= 0；普通信息（系统资源）= 50；
        /// 用户关注度高（音乐）= 100；瞬时插队由 IIslandAlert 处理，与此无关。
        /// </summary>
        int Priority { get; }

        /// <summary>当前是否有内容可展示。返回 false 表示"我现在没东西要显示"。</summary>
        bool IsActive { get; }

        /// <summary>收起/悬停态显示的文字（单行短文本）。</summary>
        string CollapsedText { get; }

        /// <summary>
        /// 展开态文字，作为没有 <see cref="ExpandedView"/> 时的回退。
        /// 简单 source（时钟、CPU 数字）只填这个；复杂 source 走 <see cref="ExpandedView"/>。
        /// 返回 null 表示沿用 <see cref="CollapsedText"/>。
        /// </summary>
        string? ExpandedText { get; }

        /// <summary>
        /// 自带的展开态视图。返回非 null 时 MainWindow 会把它塞进展开区域，
        /// 此时 <see cref="ExpandedText"/> 被忽略。整个 source 生命周期内复用同一个实例。
        /// </summary>
        FrameworkElement? ExpandedView { get; }

        /// <summary>
        /// 收起态期望宽度（px）。返回 null 表示"根据 CollapsedText 自动测量"。
        /// 非 null 时作为最小宽度参与夹算，宿主保证不低于 Collapsed 下限、不高于屏幕上限。
        /// </summary>
        double? DesiredCollapsedWidth => null;

        /// <summary>当前源的情绪色。Neutral=跟随系统，Media=紫，等。用于边光/高光染色。</summary>
        IslandMood Mood => IslandMood.Neutral;

        /// <summary>数据变化时触发。Host 收到后会重新仲裁并刷新 UI。</summary>
        event EventHandler? Changed;

        /// <summary>开始采集/订阅外部事件。Host 启动时调用。</summary>
        void Start();

        /// <summary>停止。Host 关闭时调用。</summary>
        void Stop();
    }
}
