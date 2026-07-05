using System;

namespace DynamicIslandPro.Island
{
    /// <summary>
    /// 瞬时插队提醒。与 <see cref="IIslandSource"/>（持续型数据源）平行但生命周期不同：
    /// source 长期占岛、由 host 仲裁；alert 是一次性"闪现"，由 <see cref="AlertHost"/>
    /// 排队展示若干秒后自动消失，优先级高于任何 source——alert 激活期间无论岛屿处于
    /// 收起/悬停/展开态，都强制切到提醒视图。
    ///
    /// 典型场景：充电接入、电量低、U 盘插入、剪贴板复制、下载完成……
    /// </summary>
    public interface IIslandAlert
    {
        /// <summary>唯一标识，便于去重/日志。</summary>
        string Id { get; }

        /// <summary>主标题，单行短文本。</summary>
        string Title { get; }

        /// <summary>副标题（电量百分比、文件名等），可空。</summary>
        string? Subtitle { get; }

        /// <summary>emoji 或文字字形（"⚡"/"🔋"/"🔔"），可空表示无图标。</summary>
        string? Icon { get; }

        /// <summary>
        /// 展示时长，到点自动消失。<see cref="TimeSpan.Zero"/> 表示不自动消失（需手动关闭）。
        /// </summary>
        TimeSpan Duration { get; }

        /// <summary>
        /// 优先级。数值大的先出队；且新入队 alert 优先级严格高于当前展示中的提醒时，
        /// 会打断当前、被中断的回队列等候重新展示（重新激活时 Duration 重置）。
        /// 参考取值：低电量=80 / 充电=50 / 截图=40 / USB=35 / 蓝牙=30 / 测试=10。
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// 可选的动作按钮。非 null 时，提醒胶囊右侧渲染一个紧凑按钮（标签取 <see cref="AlertAction.Label"/>），
        /// 点击执行 <see cref="AlertAction.Callback"/> 后关闭当前提醒；null 表示该提醒无动作，
        /// 点击胶囊任意处照旧关闭提醒。典型：下载完成 →「打开」打开文件所在目录。
        /// </summary>
        AlertAction? Action { get; }
    }

    /// <summary>
    /// 提醒的动作按钮：一个标签 + 一个回调。alert 携带它即表示胶囊右侧可点出一个动作按钮。
    /// 回调执行后由 AlertView 通知 AlertHost 关闭当前提醒。
    /// </summary>
    public sealed class AlertAction
    {
        /// <summary>按钮文字（"打开"/"打开文件夹"等短词）。</summary>
        public string Label { get; }

        /// <summary>点击时执行的回调。在 UI 线程触发（按钮 Click）。</summary>
        public Action Callback { get; }

        public AlertAction(string label, Action callback)
        {
            Label = label;
            Callback = callback;
        }
    }
}
