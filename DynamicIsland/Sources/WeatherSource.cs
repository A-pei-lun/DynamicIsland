using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DynamicIsland.Island;

namespace DynamicIsland.Sources
{
    /// <summary>
    /// 天气数据源：Open-Meteo（免费、无需 Key）+ ip-api 自动定位。
    /// 每 30 分钟刷新一次，失败静默保留上次数据。
    /// </summary>
    public sealed class WeatherSource : IIslandSource
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

        private Timer? _timer;
        private WeatherData? _data;
        private string _location = "定位中…";
        private double _lat, _lon;
        private bool _hasLocation;

        public string Id => "weather";
        public int Priority => 10; // 低于音乐/提醒，作为背景信息

        public bool IsActive => _data != null;
        public string CollapsedText => _data != null
            ? $"{_data.Emoji} {_data.Temperature:0}° {_data.Description}"
            : "☁️ 天气加载中…";

        public string? ExpandedText => null;
        public FrameworkElement? ExpandedView => null; // weather is a panel page, not a source view

        public event EventHandler? Changed;

        // 暴露给 WeatherView 绑定
        public WeatherData? Data => _data;
        public string Location => _location;
        public bool HasData => _data != null;

        public event EventHandler? DataUpdated;

        public async void Start()
        {
            await LocateAndFetchAsync();
            _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        public async Task RefreshAsync()
        {
            if (!_hasLocation) await LocateAsync();
            if (!_hasLocation) return;
            await FetchWeatherAsync();
        }

        public void Dispose() => Stop();

        private async Task LocateAndFetchAsync()
        {
            await LocateAsync();
            if (_hasLocation) await FetchWeatherAsync();
        }

        private async Task LocateAsync()
        {
            try
            {
                var json = await _http.GetStringAsync("http://ip-api.com/json/?fields=city,lat,lon");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("lat", out var lat) && root.TryGetProperty("lon", out var lon))
                {
                    _lat = lat.GetDouble();
                    _lon = lon.GetDouble();
                    _location = root.TryGetProperty("city", out var city) ? city.GetString() ?? "未知" : "未知";
                    _hasLocation = true;
                }
            }
            catch { /* IP 定位失败，等用户手动设 */ }
        }

        private async Task FetchWeatherAsync()
        {
            try
            {
                var url = $"https://api.open-meteo.com/v1/forecast"
                    + $"?latitude={_lat}&longitude={_lon}"
                    + "&current=temperature_2m,weather_code,relative_humidity_2m,wind_speed_10m,apparent_temperature"
                    + "&daily=temperature_2m_max,temperature_2m_min,weather_code"
                    + "&forecast_days=3&timezone=auto";

                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var cur = root.GetProperty("current");
                var daily = root.GetProperty("daily");

                int code = cur.GetProperty("weather_code").GetInt32();
                _data = new WeatherData
                {
                    Temperature = cur.GetProperty("temperature_2m").GetDouble(),
                    FeelsLike = cur.GetProperty("apparent_temperature").GetDouble(),
                    Humidity = cur.GetProperty("relative_humidity_2m").GetInt32(),
                    WindSpeed = cur.GetProperty("wind_speed_10m").GetDouble(),
                    WeatherCode = code,
                    Emoji = CodeToEmoji(code),
                    Description = CodeToDescription(code),
                    Forecast = new[]
                    {
                        ParseDay(daily, 0),
                        ParseDay(daily, 1),
                        ParseDay(daily, 2),
                    },
                };

                Changed?.Invoke(this, EventArgs.Empty);
                DataUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch { /* 静默：保留上次数据 */ }
        }

        private static DayForecast ParseDay(JsonElement daily, int i)
        {
            var codes = daily.GetProperty("weather_code");
            var maxT = daily.GetProperty("temperature_2m_max");
            var minT = daily.GetProperty("temperature_2m_min");
            int c = codes[i].GetInt32();
            return new DayForecast
            {
                Label = i == 0 ? "今天" : i == 1 ? "明天" : "后天",
                High = maxT[i].GetDouble(),
                Low = minT[i].GetDouble(),
                Emoji = CodeToEmoji(c),
                Description = CodeToDescription(c),
            };
        }

        // ── WMO Weather Code mapping ────────────────────────────────
        private static string CodeToEmoji(int code) => code switch
        {
            0 => "☀️",
            1 or 2 or 3 => "🌤️",
            45 or 48 => "🌫️",
            51 or 53 or 55 or 56 or 57 => "🌧️",
            61 or 63 or 65 or 66 or 67 => "🌧️",
            71 or 73 or 75 or 77 => "❄️",
            80 or 81 or 82 => "🌦️",
            85 or 86 => "🌨️",
            95 or 96 or 99 => "⛈️",
            _ => "☁️",
        };

        private static string CodeToDescription(int code) => code switch
        {
            0 => "晴",
            1 => "少云",
            2 => "多云",
            3 => "阴",
            45 => "雾",
            48 => "冻雾",
            51 => "小雨",
            53 => "中雨",
            55 => "大雨",
            61 => "小阵雨",
            63 => "中阵雨",
            65 => "大阵雨",
            71 => "小雪",
            73 => "中雪",
            75 => "大雪",
            80 => "阵雨",
            81 => "中阵雨",
            82 => "大阵雨",
            85 => "阵雪",
            86 => "大阵雪",
            95 => "雷暴",
            96 or 99 => "雷暴+冰雹",
            _ => "多云",
        };
    }

    public sealed class WeatherData
    {
        public double Temperature { get; init; }
        public double FeelsLike { get; init; }
        public int Humidity { get; init; }
        public double WindSpeed { get; init; }
        public int WeatherCode { get; init; }
        public string Emoji { get; init; } = "☁️";
        public string Description { get; init; } = "多云";
        public DayForecast[] Forecast { get; init; } = Array.Empty<DayForecast>();
    }

    public sealed class DayForecast
    {
        public string Label { get; init; } = "";
        public double High { get; init; }
        public double Low { get; init; }
        public string Emoji { get; init; } = "☁️";
        public string Description { get; init; } = "";
    }
}
