using System;
using System.Windows;
using DynamicIsland.Island;

namespace DynamicIsland.Sources
{
    /// <summary>
    /// 天气页：展开态仪表盘中的天气分页。
    /// 在 WeatherSource 有数据时可用。
    /// Order=25，排在媒体(10)、通知(20)之后，统计(30)之前。
    /// </summary>
    public sealed class WeatherPanel : IIslandPanel
    {
        private readonly WeatherSource _source;
        private readonly WeatherView _view;

        public WeatherPanel(WeatherSource source)
        {
            _source = source;
            _view = new WeatherView(source);
            _source.DataUpdated += (_, _) => OnAvailabilityChanged();
        }

        public string Id => "weather";
        public int Order => 25;
        public bool IsAvailable => _source.HasData;
        public FrameworkElement View => _view;

        public event EventHandler? AvailabilityChanged;

        private void OnAvailabilityChanged()
        {
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
