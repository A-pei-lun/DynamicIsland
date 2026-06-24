using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DynamicIsland.Island;

namespace DynamicIsland.Alerts
{
    public partial class AlertView : UserControl
    {
        /// <summary>心跳缩放动画的目标 Grid。MainWindow 驱动 ScaleTransform。</summary>
        public FrameworkElement HeartbeatTarget => HeartbeatGrid;
        /// <summary>高光扫过的 GradientStop（中间白条），MainWindow 动画其 Offset 实现扫过效果。</summary>
        public GradientStop SweepStop { get; }

        public AlertView()
        {
            InitializeComponent();

            // 高光扫过渐变：3 stop，中间白条。动画中间 stop 的 Offset 实现扫过。
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                SpreadMethod = GradientSpreadMethod.Pad,
            };
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            brush.GradientStops.Add(SweepStop = new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0.5));
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            SweepLight.Background = brush;
        }

        /// <summary>
        /// 动作按钮点击后触发（回调已执行完毕）。MainWindow 订阅以关闭当前提醒。
        /// 与点击胶囊其它处直接 dismiss 不同：点动作按钮是"执行动作 + 关闭"。
        /// </summary>
        public event EventHandler? ActionInvoked;

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            // DataContext 是当前 alert（MainWindow 在切提醒时设置的）。
            if (DataContext is IIslandAlert alert && alert.Action != null)
            {
                try { alert.Action.Callback(); }
                catch { /* 动作失败不卡住提醒：照常关闭 */ }
            }
            // 通知宿主关闭当前提醒（无论回调成功与否）。
            ActionInvoked?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 非 null → Visible；null → Collapsed。字符串值额外判空串（空串也视作无内容隐藏）。
    /// 既用于字符串属性（图标/副标题），也用于对象属性（Action 按钮按需显隐）。
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
