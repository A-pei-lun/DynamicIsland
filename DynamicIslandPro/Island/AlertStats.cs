using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;

namespace DynamicIslandPro.Island
{
    /// <summary>
    /// 提醒统计：跨进程重启累计各类型提醒次数。独立于 settings.json 存放——
    /// settings 是低频写（debounce 500ms），统计是高频写（每条提醒都记），混在一起会让 settings 文件被频繁重写。
    /// 单独文件 stats.json 还能单独清零、单独备份。
    ///
    /// 类型分组按 alert Id 的第一个 '.' 前缀（battery.*/network.*/usb.*/bt.*/clipboard.*/download.*/test.*）。
    /// 前缀缺失（无 '.'）整串算一类。
    ///
    /// 持久化策略与 DisplaySettings 一致：原子写（tmp + Move）、损坏兜底用默认值、I/O 失败不影响进程。
    /// 落盘走 800ms debounce：多条提醒连发（如 USB 插入连弹两条）不连刷盘。
    /// </summary>
    public sealed class AlertStats : IDisposable
    {
        private static readonly string StatsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DynamicIsland");
        private static readonly string StatsPath = Path.Combine(StatsDir, "stats.json");

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _saveTimer;
        private readonly Dictionary<string, int> _counts = new();
        private DateTime _firstSeen = DateTime.Today;
        private bool _loaded;
        private bool _disposed;

        /// <summary>统计变化时触发（新增计数时），让统计页刷新展示。</summary>
        public event EventHandler? Changed;

        public AlertStats()
        {
            _dispatcher = System.Windows.Application.Current?.Dispatcher
                          ?? Dispatcher.CurrentDispatcher;
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        }

        /// <summary>累计提醒总数。</summary>
        public int Total => _counts.Values.Sum();

        /// <summary>统计首次记录日期（用于算"已统计 N 天"）。</summary>
        public DateTime FirstSeen => _firstSeen;

        /// <summary>已统计天数（含今天，至少 1）。</summary>
        public int Days => Math.Max(1, DateTime.Today.Subtract(_firstSeen).Days + 1);

        /// <summary>
        /// 各类型计数快照（只读）。Key=类型前缀，Value=累计次数，按次数降序。
        /// 给统计页绑定展示用。
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, int>> ByType
            => _counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToList();

        /// <summary>启动期调一次。文件不存在/损坏一律静默用默认值。</summary>
        public void Load()
        {
            try
            {
                if (File.Exists(StatsPath))
                {
                    var json = File.ReadAllText(StatsPath);
                    var snap = JsonSerializer.Deserialize<Snapshot>(json, JsonOpts);
                    if (snap != null)
                    {
                        _counts.Clear();
                        foreach (var kv in snap.Counts ?? new())
                            _counts[kv.Key] = kv.Value;
                        // 首次日期缺失用今天，避免算出负天数
                        _firstSeen = snap.FirstSeen == default ? DateTime.Today : snap.FirstSeen.Date;
                    }
                }
            }
            catch
            {
                // 损坏不阻塞，保留空统计
            }
            finally
            {
                _loaded = true;
            }
        }

        /// <summary>
        /// 记录一条提醒。按 Id 前缀分组累加。可在任意线程调用（外部源投递时），
        /// 内部 marshal 回 UI 线程再做计数+落盘，避免字典并发写。
        /// </summary>
        public void Record(string alertId)
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(() => Record(alertId)));
                return;
            }

            string type = GetTypePrefix(alertId);
            _counts[type] = _counts.TryGetValue(type, out int c) ? c + 1 : 1;

            ScheduleSave();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>清零所有统计（统计页"清空"按钮用）。</summary>
        public void Clear()
        {
            _counts.Clear();
            _firstSeen = DateTime.Today;
            SaveNow();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static string GetTypePrefix(string id)
        {
            int dot = id.IndexOf('.');
            return dot > 0 ? id.Substring(0, dot) : id;
        }

        private void ScheduleSave()
        {
            if (!_loaded) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>立即同步写盘。debounce 命中 / 进程退出兜尾 / Clear 各用一次。</summary>
        public void SaveNow()
        {
            try
            {
                Directory.CreateDirectory(StatsDir);
                var snap = new Snapshot
                {
                    Counts = _counts,
                    FirstSeen = _firstSeen,
                };
                var json = JsonSerializer.Serialize(snap, JsonOpts);
                // 原子写：tmp + 替换，防崩在写一半留损坏 JSON
                var tmp = StatsPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, StatsPath, overwrite: true);
            }
            catch
            {
                // I/O 失败不影响进程
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _saveTimer.Stop();
            _saveTimer.Tick -= (_, _) => { };
            // 进程正常退出时 App.OnExit 已调 SaveNow；这里再保底一次防未落盘计数丢失
            SaveNow();
        }

        /// <summary>序列化快照（POCO）。字段缺失取默认值，加字段时旧 stats.json 自动兼容。</summary>
        private sealed class Snapshot
        {
            public Dictionary<string, int> Counts { get; set; } = new();
            public DateTime FirstSeen { get; set; } = default;
        }
    }
}
