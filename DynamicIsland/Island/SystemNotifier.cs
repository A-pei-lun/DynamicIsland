using System;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DynamicIsland.Island
{
    /// <summary>
    /// 把灵动岛提醒同步发到 Windows 通知中心。
    ///
    /// 动机：用户全屏游戏/离开电脑时看不到岛上的提醒，同步进通知中心可回看。
    /// 与"监听其它应用通知"（P2 原计划，unpackaged 做不到）相反——这是**发送**方向。
    ///
    /// 用 ToastNotificationManagerCompat（Microsoft.Toolkit.Uwp.Notifications）：
    /// 它专为 unpackaged 桌面应用设计，**自动**注册 AUMID + 开始菜单快捷方式，
    /// 不需要手动 COM 互操作。失败静默（通知系统不可用时不拖垮岛）。
    ///
    /// 只在真正展示给用户时发（AlertHost.Activate 调，与统计/历史同时机）——
    /// 排队/被打断未展示的不发，避免通知中心被堆。
    /// </summary>
    public sealed class SystemNotifier : IDisposable
    {
        private bool _disposed;

        public SystemNotifier() { }

        /// <summary>
        /// 发一条提醒到通知中心。标题作首行文本，副标题作次行。
        /// </summary>
        public void Send(IIslandAlert alert)
        {
            if (_disposed) return;

            try
            {
                var builder = new ToastContentBuilder()
                    .AddText(alert.Title);

                if (!string.IsNullOrEmpty(alert.Subtitle))
                    builder.AddText(alert.Subtitle);

                // Show() 内部走 ToastNotificationManagerCompat.CreateToastNotifier().Show()，
                // 首次调用自动注册 AUMID 快捷方式（幂等）。
                builder.Show();
            }
            catch
            {
                // 通知系统异常不拖垮岛功能
            }
        }

        public void Dispose()
        {
            _disposed = true;
            // ToastNotificationManagerCompat 无需显式 Unregister（进程退出即清理）
        }
    }
}
