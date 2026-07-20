using System;
using System.Collections.Generic;
using System.Linq;

namespace DynamicIsland.Island
{
    /// <summary>
    /// 管理所有 <see cref="IIslandSource"/>，仲裁出当前应该展示哪一个。
    /// 任何源数据变化或 IsActive 切换，都会触发 <see cref="Updated"/>。
    /// </summary>
    public sealed class IslandHost : IDisposable
    {
        private readonly List<IIslandSource> _sources = new();
        private bool _started;

        /// <summary>当前胜出的源。可能为 null（极少见，理论上 ClockSource 永远兜底）。</summary>
        public IIslandSource? CurrentSource { get; private set; }

        /// <summary>当前源声明的收起态期望宽度。null = 自动测量。</summary>
        public double? CurrentDesiredWidth => CurrentSource?.DesiredCollapsedWidth;

        /// <summary>当前源的情绪色。用于边光/高光染色。</summary>
        public IslandMood CurrentMood => CurrentSource?.Mood ?? IslandMood.Neutral;

        /// <summary>当前源切换、或当前源内部数据更新时触发。UI 据此刷新。</summary>
        public event EventHandler? Updated;

        public void Register(IIslandSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            _sources.Add(source);
            source.Changed += OnSourceChanged;
            if (_started) source.Start();
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            foreach (var s in _sources) s.Start();
            Recompute();
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            foreach (var s in _sources) s.Stop();
        }

        private void OnSourceChanged(object? sender, EventArgs e) => Recompute();

        private void Recompute()
        {
            CurrentSource = _sources
                .Where(s => s.IsActive)
                .OrderByDescending(s => s.Priority)
                .FirstOrDefault();

            // 即使胜出源没变，源的内部数据也可能更新了（时钟跳一秒），都通知 UI 刷新
            Updated?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Stop();
            foreach (var s in _sources)
            {
                s.Changed -= OnSourceChanged;
                s.Dispose();
            }
            _sources.Clear();
        }
    }
}
