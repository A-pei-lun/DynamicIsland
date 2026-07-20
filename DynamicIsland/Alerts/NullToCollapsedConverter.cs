using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DynamicIsland.Alerts
{
    /// <summary>
    /// 非 null → Visible；null → Collapsed。字符串值额外判空串。
    /// </summary>
    public sealed class NullToCollapsedConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value == null) return Visibility.Collapsed;
            if (value is string s && string.IsNullOrEmpty(s)) return Visibility.Collapsed;
            return Visibility.Visible;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
