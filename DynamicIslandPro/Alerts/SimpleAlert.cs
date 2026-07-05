using System;
using DynamicIslandPro.Island;

namespace DynamicIslandPro.Alerts
{
    /// <summary>
    /// 最简 <see cref="IIslandAlert"/> 实现：构造时给定全部字段。
    /// 用于测试菜单、以及未来一次性、无状态来源的提醒。
    /// </summary>
    public sealed class SimpleAlert : IIslandAlert
    {
        public SimpleAlert(string id, string title, string? subtitle = null,
                           string? icon = null, TimeSpan? duration = null, int priority = 0,
                           AlertAction? action = null)
        {
            Id = id;
            Title = title;
            Subtitle = subtitle;
            Icon = icon;
            // 默认日常档：~2.5s。重要提醒（低电量等）由调用方显式传 4s。
            Duration = duration ?? TimeSpan.FromSeconds(2.5);
            Priority = priority;
            Action = action;
        }

        public string Id { get; }
        public string Title { get; }
        public string? Subtitle { get; }
        public string? Icon { get; }
        public TimeSpan Duration { get; }
        public int Priority { get; }
        public AlertAction? Action { get; }
    }
}
