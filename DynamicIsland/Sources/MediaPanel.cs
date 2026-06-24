using System;
using System.ComponentModel;
using System.Windows;
using DynamicIsland.Island;

namespace DynamicIsland.Sources
{
    /// <summary>
    /// 媒体控制页：复用 <see cref="MediaExpandedView"/>，DataContext 绑到 <see cref="MediaSource"/>。
    /// 播放/暂停只要有会话就可用（<see cref="MediaSource.HasMedia"/>），暂停时仍能在此页继续播放。
    /// </summary>
    public sealed class MediaPanel : IIslandPanel
    {
        private readonly MediaSource _media;
        private readonly MediaExpandedView _view;

        public MediaPanel(MediaSource media)
        {
            _media = media;
            _view = new MediaExpandedView { DataContext = media };
            // HasMedia 是 INPC 属性，变化时通知 dashboard 重算可用页
            _media.PropertyChanged += OnMediaPropertyChanged;
        }

        public string Id => "media";
        public int Order => 10;
        public bool IsAvailable => _media.HasMedia;
        public FrameworkElement View => _view;

        public event EventHandler? AvailabilityChanged;

        private void OnMediaPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MediaSource.HasMedia))
                AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
