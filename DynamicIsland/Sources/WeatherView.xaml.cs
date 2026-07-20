using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicIsland.Sources
{
    public partial class WeatherView : UserControl
    {
        private readonly WeatherSource _source;

        public WeatherView(WeatherSource source)
        {
            InitializeComponent();
            _source = source;
            _source.DataUpdated += (_, _) => Dispatcher.Invoke(Refresh);
            Dispatcher.Invoke(Refresh);
        }

        private void Refresh()
        {
            LocationLabel.Text = $"📍 {_source.Location}";

            var d = _source.Data;
            if (d == null)
            {
                WeatherEmoji.Text = "☁️";
                Temperature.Text = "--°";
                Description.Text = "加载中…";
                FeelsLike.Text = "--°";
                Humidity.Text = "--%";
                WindSpeed.Text = "--级";
                return;
            }

            WeatherEmoji.Text = d.Emoji;
            Temperature.Text = $"{d.Temperature:0}°";
            Description.Text = d.Description;
            FeelsLike.Text = $"{d.FeelsLike:0}°";
            Humidity.Text = $"{d.Humidity}%";
            WindSpeed.Text = WindDesc(d.WindSpeed);

            // 3 天预报
            ForecastRow.Children.Clear();
            foreach (var f in d.Forecast)
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(3, 0, 3, 0),
                    MinWidth = 60,
                };
                var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                sp.Children.Add(new TextBlock
                {
                    Text = f.Label,
                    FontSize = 9,
                    Opacity = 0.5,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                sp.Children.Add(new TextBlock
                {
                    Text = f.Emoji,
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 1),
                });
                sp.Children.Add(new TextBlock
                {
                    Text = $"{f.High:0}° / {f.Low:0}°",
                    FontSize = 10,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                card.Child = sp;
                ForecastRow.Children.Add(card);
            }
        }

        private static string WindDesc(double mps) => mps switch
        {
            < 0.3 => "无风",
            < 3.4 => "1-2 级",
            < 8.0 => "3-4 级",
            < 13.9 => "5-6 级",
            _ => "7+ 级",
        };

        private async void RefreshBtn_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            RefreshBtn.Text = "  ⏳ 刷新中…";
            await _source.RefreshAsync();
            RefreshBtn.Text = "  🔄 刷新";
            Refresh();
        }
    }
}
