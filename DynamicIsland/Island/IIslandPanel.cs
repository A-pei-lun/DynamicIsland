using System;
using System.Windows;

namespace DynamicIsland.Island
{
    /// <summary>
    /// 展开态仪表盘里的一个"页面"。每个功能（媒体控制、系统详情、以及后续要加的
    /// 天气/通知/待办……）实现一个，由 <see cref="IslandDashboard"/> 用滚轮翻页切换。
    ///
    /// 与 <see cref="IIslandSource"/> 的区别：
    /// - source 决定"收起/悬停态谁占岛"（持续仲裁）；
    /// - panel 是展开态的展示页，可同时存在多个，滚轮切换，互不抢占。
    /// 系统资源固定条不属于任何 panel，永远钉在仪表盘底部。
    /// </summary>
    public interface IIslandPanel
    {
        /// <summary>唯一标识，便于日志/调试。</summary>
        string Id { get; }

        /// <summary>展示顺序，小的在前。约定留出区段：媒体=10，系统=20，后续按 30/40…递增。</summary>
        int Order { get; }

        /// <summary>当前是否可用。返回 false 时此页不参与翻页（如无媒体会话时隐藏媒体页）。</summary>
        bool IsAvailable { get; }

        /// <summary><see cref="IsAvailable"/> 变化时触发，dashboard 据此重算可用页集合。</summary>
        event EventHandler AvailabilityChanged;

        /// <summary>本页的视图。整个 panel 生命周期内复用同一实例。</summary>
        FrameworkElement View { get; }
    }
}
