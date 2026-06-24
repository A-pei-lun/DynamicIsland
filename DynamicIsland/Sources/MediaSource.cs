using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Island;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Sources
{
    /// <summary>
    /// 监听 Windows 系统媒体会话（GSMTC）。
    /// 任何注册了 SMTC 的播放器都能识别：Spotify / 网易云 / QQ 音乐 /
    /// 浏览器视频 / Windows 媒体播放器 等。
    /// 同时实现 <see cref="INotifyPropertyChanged"/>，给自带的展开视图直接绑定。
    /// </summary>
    public sealed class MediaSource : IIslandSource, INotifyPropertyChanged
    {
        // ─── GSMTC ────────────────────────────────────────────────
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;

        // ─── 状态字段 ──────────────────────────────────────────────
        private string? _title;
        private string? _artist;
        private bool _isPlaying;
        private bool _hasSession;
        private ImageSource? _thumbnail;
        private MediaPlaybackAutoRepeatMode _repeatMode;
        private bool _isShuffleActive;

        // 进度（GSMTC 不会自己跳，本地 timer 外插）
        private TimeSpan _reportedPosition;
        private TimeSpan _duration;
        private DateTime _reportedAtUtc;
        private readonly DispatcherTimer _progressTimer;
        private int _tickCount;

        // ─── 视图（懒创建）─────────────────────────────────────────
        private MediaExpandedView? _view;

        // 把所有 GSMTC 的回调切回 UI 线程
        private readonly Dispatcher _dispatcher;

        public MediaSource()
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _progressTimer.Tick += (_, _) =>
            {
                // 每 4 次 tick (≈1s) 主动重读一次 timeline，对付不发
                // TimelinePropertiesChanged 事件的播放器（典型：网易云）
                if (++_tickCount >= 4)
                {
                    _tickCount = 0;
                    RefreshTimeline();
                }
                // 外插：当前位置 = 上次报告的位置 + 经过的实际时间
                OnPropertyChanged(nameof(Position));
                OnPropertyChanged(nameof(PositionText));
                OnPropertyChanged(nameof(Progress));
            };

            PlayPauseCommand = new RelayCommand(async () => await TryPlayPauseAsync());
            NextCommand = new RelayCommand(async () => await SafeAsync(s => s.TrySkipNextAsync()));
            PrevCommand = new RelayCommand(async () => await SafeAsync(s => s.TrySkipPreviousAsync()));
            ToggleRepeatCommand = new RelayCommand(async () => await ToggleRepeatAsync());
            ToggleShuffleCommand = new RelayCommand(async () => await ToggleShuffleAsync());
        }

        // ─── IIslandSource ────────────────────────────────────────
        public string Id => "media";
        public int Priority => 100;

        /// <summary>
        /// 只有"正在播放"才占岛：暂停时让出收起/悬停态给时钟或系统资源，
        /// 灵动岛不被暂停的播放器长期占用。
        /// </summary>
        public bool IsActive => _hasSession && _isPlaying && !string.IsNullOrEmpty(_title);

        /// <summary>
        /// 是否存在可操作的媒体会话（不论播放/暂停）。
        /// 仪表盘的媒体区据此决定显隐——暂停时岛虽然让出了，展开后仍能操作媒体。
        /// </summary>
        public bool HasMedia => _hasSession && !string.IsNullOrEmpty(_title);
        public string CollapsedText
        {
            get
            {
                if (!IsActive) return string.Empty;
                var icon = _isPlaying ? "🎵" : "⏸";
                var artist = string.IsNullOrEmpty(_artist) ? "" : $" - {_artist}";
                return $"{icon} {_title}{artist}";
            }
        }
        // 走自带 view，文本回退用不上
        public string? ExpandedText => null;

        public FrameworkElement? ExpandedView
        {
            get
            {
                _view ??= new MediaExpandedView { DataContext = this };
                return _view;
            }
        }

        public event EventHandler? Changed;

        // ─── 视图绑定属性 ──────────────────────────────────────────
        public string Title => _title ?? "";
        public string Artist => _artist ?? "";
        public ImageSource? Thumbnail => _thumbnail;
        public bool IsPlaying => _isPlaying;
        public string PlayPauseIcon => _isPlaying ? "⏸" : "▶";

        public TimeSpan Position
        {
            get
            {
                if (!_isPlaying) return _reportedPosition;
                var elapsed = DateTime.UtcNow - _reportedAtUtc;
                // 防御：reportedAtUtc 是脏数据时（比如某些播放器没填 LastUpdatedTime
                // 落到默认 DateTimeOffset.MinValue），elapsed 会变成几千年，直接当无效。
                if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromHours(24))
                    return _reportedPosition;
                var pos = _reportedPosition + elapsed;
                if (_duration > TimeSpan.Zero && pos > _duration) pos = _duration;
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                return pos;
            }
        }
        public TimeSpan Duration => _duration;
        /// <summary>播放器是否报了有效的时长（网易云这种不发 timeline 的会一直 false）。
        /// UI 层拿它控制进度条/时间是否显示。</summary>
        public bool HasTimeline => _duration > TimeSpan.Zero;
        public string PositionText => Format(Position);
        public string DurationText => Format(_duration);
        public double Progress
        {
            get
            {
                if (_duration <= TimeSpan.Zero) return 0;
                return Math.Clamp(Position.TotalSeconds / _duration.TotalSeconds, 0, 1);
            }
        }

        public MediaPlaybackAutoRepeatMode RepeatMode => _repeatMode;
        public string RepeatIcon => _repeatMode switch
        {
            MediaPlaybackAutoRepeatMode.Track => "🔂",
            MediaPlaybackAutoRepeatMode.List => "🔁",
            _ => "🔁"
        };
        public double RepeatOpacity => _repeatMode == MediaPlaybackAutoRepeatMode.None ? 0.4 : 1.0;

        public bool IsShuffleActive => _isShuffleActive;
        public double ShuffleOpacity => _isShuffleActive ? 1.0 : 0.4;

        public ICommand PlayPauseCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand PrevCommand { get; }
        public ICommand ToggleRepeatCommand { get; }
        public ICommand ToggleShuffleCommand { get; }

        // ─── 启动/停止 ────────────────────────────────────────────
        public async void Start()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                AttachSession(_manager.GetCurrentSession());
            }
            catch
            {
                // GSMTC 不可用就保持静默，不影响其他源
            }
        }

        public void Stop()
        {
            DetachSession();
            if (_manager != null)
            {
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _manager = null;
            }
            _progressTimer.Stop();
            _hasSession = false;
            _title = null;
            _artist = null;
            _isPlaying = false;
            _thumbnail = null;
        }

        public void Dispose() => Stop();

        // ─── 会话切换 ──────────────────────────────────────────────
        private void OnCurrentSessionChanged(
            GlobalSystemMediaTransportControlsSessionManager sender,
            CurrentSessionChangedEventArgs args)
        {
            _dispatcher.Invoke(() => AttachSession(_manager?.GetCurrentSession()));
        }

        private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
        {
            DetachSession();

            _session = session;
            _hasSession = session != null;

            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                RefreshPlaybackInfo();
                RefreshTimeline();
                _ = RefreshPropertiesAsync();
            }
            else
            {
                _title = null;
                _artist = null;
                _isPlaying = false;
                _thumbnail = null;
                _reportedPosition = TimeSpan.Zero;
                _duration = TimeSpan.Zero;
                _progressTimer.Stop();
            }

            NotifyAll();
        }

        private void DetachSession()
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
                _session = null;
            }
        }

        // ─── 数据刷新 ──────────────────────────────────────────────
        private async Task RefreshPropertiesAsync()
        {
            var session = _session;
            if (session == null) return;
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                string? title = props?.Title;
                string? artist = props?.Artist;
                ImageSource? thumb = await LoadThumbnailAsync(props?.Thumbnail);

                _dispatcher.Invoke(() =>
                {
                    _title = title;
                    _artist = artist;
                    _thumbnail = thumb;
                    NotifyAll();
                });
            }
            catch
            {
                // 某些播放器拒绝读取，忽略
            }
        }

        private void RefreshPlaybackInfo()
        {
            if (_session == null) return;
            try
            {
                var info = _session.GetPlaybackInfo();
                _isPlaying = info?.PlaybackStatus ==
                             GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                _repeatMode = info?.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;
                _isShuffleActive = info?.IsShuffleActive ?? false;

                if (_isPlaying) _progressTimer.Start();
                else _progressTimer.Stop();
            }
            catch
            {
                _isPlaying = false;
                _progressTimer.Stop();
            }
        }

        private void RefreshTimeline()
        {
            if (_session == null) return;
            try
            {
                var t = _session.GetTimelineProperties();
                _reportedPosition = t.Position;
                _duration = t.EndTime - t.StartTime;

                // 优先用 GSMTC 报告的 LastUpdatedTime 作为外插起点，能消掉事件传递滞后。
                // 但网易云这种不更新该字段的播放器，LastUpdatedTime 会落到
                // DateTimeOffset.MinValue（0001年），扣到现在就是几千年的 elapsed，
                // 直接让进度条爆炸。所以只有当 LastUpdatedTime 合理（在过去
                // 5 分钟内）才采纳，否则退回 UtcNow。
                var reportedAt = t.LastUpdatedTime.UtcDateTime;
                var now = DateTime.UtcNow;
                var skew = now - reportedAt;
                _reportedAtUtc =
                    (skew >= TimeSpan.Zero && skew <= TimeSpan.FromMinutes(5))
                        ? reportedAt
                        : now;
            }
            catch
            {
                _reportedPosition = TimeSpan.Zero;
                _duration = TimeSpan.Zero;
            }
        }

        private void OnMediaPropertiesChanged(
            GlobalSystemMediaTransportControlsSession sender,
            MediaPropertiesChangedEventArgs args)
        {
            _ = RefreshPropertiesAsync();
        }

        private void OnPlaybackInfoChanged(
            GlobalSystemMediaTransportControlsSession sender,
            PlaybackInfoChangedEventArgs args)
        {
            _dispatcher.Invoke(() =>
            {
                RefreshPlaybackInfo();
                NotifyAll();
            });
        }

        private void OnTimelinePropertiesChanged(
            GlobalSystemMediaTransportControlsSession sender,
            TimelinePropertiesChangedEventArgs args)
        {
            _dispatcher.Invoke(() =>
            {
                RefreshTimeline();
                NotifyAll();
            });
        }

        // ─── 控制 ──────────────────────────────────────────────────
        private async Task TryPlayPauseAsync()
        {
            var s = _session;
            if (s == null) return;
            try
            {
                if (_isPlaying) await s.TryPauseAsync();
                else await s.TryPlayAsync();
            }
            catch { }
        }

        private async Task ToggleRepeatAsync()
        {
            var s = _session;
            if (s == null) return;
            var next = _repeatMode switch
            {
                MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
                MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
                _ => MediaPlaybackAutoRepeatMode.None
            };
            try { await s.TryChangeAutoRepeatModeAsync(next); } catch { }
        }

        private async Task ToggleShuffleAsync()
        {
            var s = _session;
            if (s == null) return;
            try { await s.TryChangeShuffleActiveAsync(!_isShuffleActive); } catch { }
        }

        private async Task SafeAsync(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> op)
        {
            var s = _session;
            if (s == null) return;
            try { await op(s); } catch { }
        }

        // ─── 封面加载 ──────────────────────────────────────────────
        private static async Task<ImageSource?> LoadThumbnailAsync(IRandomAccessStreamReference? thumb)
        {
            if (thumb == null) return null;
            try
            {
                using var stream = await thumb.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(ms);
                ms.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();   // 跨线程使用必须 Freeze
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        // ─── INotifyPropertyChanged ────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>状态大变（切歌、切会话、播放暂停）后，把所有派生属性都通知一遍。</summary>
        private void NotifyAll()
        {
            OnPropertyChanged(nameof(HasMedia));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Artist));
            OnPropertyChanged(nameof(Thumbnail));
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(PlayPauseIcon));
            OnPropertyChanged(nameof(Position));
            OnPropertyChanged(nameof(Duration));
            OnPropertyChanged(nameof(HasTimeline));
            OnPropertyChanged(nameof(PositionText));
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(RepeatMode));
            OnPropertyChanged(nameof(RepeatIcon));
            OnPropertyChanged(nameof(RepeatOpacity));
            OnPropertyChanged(nameof(IsShuffleActive));
            OnPropertyChanged(nameof(ShuffleOpacity));

            // 通知 host 重新仲裁
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // ─── 工具 ──────────────────────────────────────────────────
        private static string Format(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";
        }

        // 简易 ICommand
        private sealed class RelayCommand : ICommand
        {
            private readonly Action _execute;
            public RelayCommand(Action execute) { _execute = execute; }
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _execute();
            public event EventHandler? CanExecuteChanged
            {
                add { CommandManager.RequerySuggested += value; }
                remove { CommandManager.RequerySuggested -= value; }
            }
        }
    }
}
