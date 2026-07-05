using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using DynamicIslandPro;

namespace DynamicIslandPro.Island
{
    /// <summary>
    /// 瞬时提醒队列管理。alert 入队后，若无正在展示的提醒则立即激活；否则排队等候。
    /// 激活的提醒展示 <see cref="IIslandAlert.Duration"/> 后自动消失，并出队下一个。
    /// <see cref="CurrentChanged"/> 在 Current 从 null↔非空 切换时触发，UI 据此切到/切出提醒视图。
    ///
    /// 打断机制：新入队 alert 的优先级严格高于当前展示中的提醒时，立即抢占——
    /// 被中断的回队列等候，重新激活时 Duration 重置。优先级不够则按优先级降序排队。
    /// 这保证低电量(80)能打断充电中(50)/截图(40)等日常提醒，但不会反过来。
    ///
    /// 历史记录：每次 Activate（真正展示给用户的时刻）入历史，滚动保留最近 N 条，
    /// 供展开态"最近通知"页查看。被中断后重新激活的 alert 会再入一条（视为一次新的展示）。
    /// </summary>
    public sealed class AlertHost : IDisposable
    {
        // 历史容量：最近 20 条。够回看"刚发生了什么"，又不无限增长。
        private const int HistoryCapacity = 20;

        private readonly Dispatcher _dispatcher;
        private readonly List<IIslandAlert> _queue = new();
        private readonly DispatcherTimer _dismissTimer;
        private IIslandAlert? _current;

        // 历史记录：索引 0 = 最新。倒序存方便取最近 N 条。
        private readonly List<AlertHistoryEntry> _history = new(HistoryCapacity);

        public AlertHost()
        {
            _dispatcher = System.Windows.Application.Current?.Dispatcher
                          ?? Dispatcher.CurrentDispatcher;
            _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _dismissTimer.Tick += OnDismissTick;
        }

        /// <summary>
        /// 提醒统计（跨重启累计各类型次数）。可选——为 null 时不记统计。
        /// MainWindow 构造后注入；在 Activate（真正展示给用户）时记录，与历史记录同一时机。
        /// </summary>
        public AlertStats? Stats { get; set; }

        /// <summary>
        /// 系统通知同步（岛→Windows 通知中心）。可选——为 null 时不同步。
        /// MainWindow 构造后注入；在 Activate（真正展示给用户）时发送，与统计/历史同时机。
        /// 是否真正发送还看 DisplaySettings.EnableSystemNotification 开关（SystemNotifier.Send 内不查，
        /// 由调用方 AlertHost 在 Activate 里按开关决定——避免 SystemNotifier 依赖 DisplaySettings）。
        /// </summary>
        public SystemNotifier? Notifier { get; set; }

        /// <summary>当前正在展示的提醒，无则 null。</summary>
        public IIslandAlert? Current => _current;

        /// <summary>Current 切换时触发（null→alert 或 alert→null 或 alert→alert）。</summary>
        public event EventHandler? CurrentChanged;

        /// <summary>历史记录变化时触发（新增条目时）。索引 0 = 最新。</summary>
        public event EventHandler? HistoryChanged;

        /// <summary>历史记录快照（只读视图）。索引 0 = 最新。勿修改。</summary>
        public IReadOnlyList<AlertHistoryEntry> History => _history;

        /// <summary>
        /// 入队一个提醒。无当前提醒则立即激活；否则：
        /// - 新提醒优先级严格高于当前展示中的 → 打断当前，被中断的插回队列等候重新展示
        /// - 否则按优先级降序插入队列等候（同优先级保持入队顺序，稳定）
        /// </summary>
        public void Enqueue(IIslandAlert alert)
        {
            ArgumentNullException.ThrowIfNull(alert);
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(() => Enqueue(alert)));
                return;
            }

            if (_current == null)
            {
                Activate(alert);
                return;
            }

            // 打断机制：高优先级立即抢占当前展示。被中断的回队列等候，重新激活时 Duration 重置。
            // 用严格大于（>）而非 >=：同优先级不互相打断，避免两个同级 alert 来回抢闪烁。
            if (alert.Priority > _current.Priority)
            {
                InsertByPriority(_current);
                Activate(alert);
            }
            else
            {
                InsertByPriority(alert);
            }
        }

        /// <summary>按优先级降序插入队列；同优先级保持入队顺序（稳定排序）。</summary>
        private void InsertByPriority(IIslandAlert alert)
        {
            int i = 0;
            while (i < _queue.Count && _queue[i].Priority >= alert.Priority)
                i++;
            _queue.Insert(i, alert);
        }

        /// <summary>手动关闭当前提醒（用户点击 dismiss）。出队下一个或清空。</summary>
        public void DismissCurrent()
        {
            if (_current == null) return;
            _dismissTimer.Stop();
            _current = null;

            if (_queue.Count > 0)
            {
                // 取优先级最高的（队列已按优先级降序，首元素即可）
                var next = _queue[0];
                _queue.RemoveAt(0);
                Activate(next);
            }
            else
            {
                OnCurrentChanged();
            }
        }

        private void Activate(IIslandAlert alert)
        {
            _current = alert;
            if (alert.Duration > TimeSpan.Zero)
            {
                _dismissTimer.Interval = alert.Duration;
                _dismissTimer.Stop();
                _dismissTimer.Start();
            }
            else
            {
                _dismissTimer.Stop(); // Zero = 不自动消失，等手动 dismiss
            }

            AddToHistory(alert);
            Stats?.Record(alert.Id);
            // 同步到 Windows 通知中心：用户不在岛前时（全屏/离开）也能回看。
            // 仅在开关开启时发；真正展示才发，避免通知中心被堆。
            if (Notifier != null && DisplaySettings.Instance.EnableSystemNotification)
                Notifier.Send(alert);
            OnCurrentChanged();
        }

        /// <summary>把一条 alert 加进历史记录（最新在前）。超出容量丢弃最旧的。</summary>
        private void AddToHistory(IIslandAlert alert)
        {
            _history.Insert(0, new AlertHistoryEntry(alert, DateTime.Now));
            while (_history.Count > HistoryCapacity)
                _history.RemoveAt(_history.Count - 1);
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>清空历史记录。</summary>
        public void ClearHistory()
        {
            if (_history.Count == 0) return;
            _history.Clear();
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnDismissTick(object? sender, EventArgs e) => DismissCurrent();

        private void OnCurrentChanged() => CurrentChanged?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
            _dismissTimer.Stop();
            _dismissTimer.Tick -= OnDismissTick;
            _queue.Clear();
            _history.Clear();
            _current = null;
        }
    }

    /// <summary>
    /// 历史记录条目：alert 引用 + 激活时间戳。INPC 让 UI 绑定的相对时间能随定时器刷新。
    /// </summary>
    public sealed class AlertHistoryEntry : System.ComponentModel.INotifyPropertyChanged
    {
        public IIslandAlert Alert { get; }
        public DateTime Time { get; }

        public string? Icon => Alert.Icon;
        public string Title => Alert.Title;
        public string? Subtitle => Alert.Subtitle;

        /// <summary>
        /// 转发原 alert 的动作。非 null 时通知页条目显示"重做"按钮——
        /// 下载完成提醒的历史条目可点击重新执行（如再次打开文件所在目录）。
        /// Callback 是闭包，捕获了当时上下文（下载 path 等），文件还在就仍有效。
        /// </summary>
        public AlertAction? Action => Alert.Action;

        // 相对时间文案（"刚刚"/"30秒前"/"3分钟前"/"2小时前"/"昨天"），由 View 层定时刷新。
        private string _relativeTime = "刚刚";
        public string RelativeTime
        {
            get => _relativeTime;
            set
            {
                if (_relativeTime == value) return;
                _relativeTime = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(RelativeTime)));
            }
        }

        public AlertHistoryEntry(IIslandAlert alert, DateTime time)
        {
            Alert = alert;
            Time = time;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
