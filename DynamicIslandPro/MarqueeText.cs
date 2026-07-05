using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIslandPro
{
    /// <summary>
    /// 跑马灯文本：文字超出可视宽时自动横向滚动，否则静止。
    /// 用于媒体标题/艺人过长时不再被省略号截断。
    ///
    /// 结构：UserControl 内一个 ClipToBounds 的 Grid，Grid 内一个 TextBlock，
    /// TextBlock 用 TranslateTransform 平移。控件高度由 TextBlock 自然撑开，
    /// 宽度由父容器约束（必须有限宽度才能测出是否溢出）。
    /// </summary>
    public sealed class MarqueeText : UserControl
    {
        private readonly Grid _clip;
        private readonly TextBlock _text;
        private readonly TranslateTransform _transform;
        private DoubleAnimation? _anim;

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(MarqueeText),
            new FrameworkPropertyMetadata(string.Empty, OnTextChanged));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        /// <summary>
        /// 跑马灯触发宽度阈值（DIP）。文本宽 > 此值才滚动；≤ 0 退化为读 _clip.ActualWidth。
        /// 由调用方钉在"内容上限"上：宽度自适应时父容器会把岛撑到能装下文本就不滚，
        /// 装不下（达到上限）才滚——这条判定不依赖 layout 时序，避免动画过程误判。
        /// </summary>
        public static readonly DependencyProperty MaxViewWidthProperty = DependencyProperty.Register(
            nameof(MaxViewWidth), typeof(double), typeof(MarqueeText),
            new FrameworkPropertyMetadata(0.0, OnMaxViewWidthChanged));

        public double MaxViewWidth
        {
            get => (double)GetValue(MaxViewWidthProperty);
            set => SetValue(MaxViewWidthProperty, value);
        }

        private static void OnMaxViewWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var m = (MarqueeText)d;
            m._lastViewWidth = -1; // 失效缓存强制重算
            m.UpdateMarquee();
        }

        public MarqueeText()
        {
            _text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            // 字体相关属性复用基类 DP：把内部 TextBlock 绑到本控件，XAML 设基类 DP 自动同步
            _text.SetBinding(TextBlock.FontSizeProperty, new System.Windows.Data.Binding
            { Source = this, Path = new PropertyPath(FontSizeProperty) });
            _text.SetBinding(TextBlock.FontWeightProperty, new System.Windows.Data.Binding
            { Source = this, Path = new PropertyPath(FontWeightProperty) });
            _text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding
            { Source = this, Path = new PropertyPath(ForegroundProperty) });
            _text.SetBinding(TextBlock.FontFamilyProperty, new System.Windows.Data.Binding
            { Source = this, Path = new PropertyPath(FontFamilyProperty) });

            _transform = new TranslateTransform();
            _text.RenderTransform = _transform;

            _clip = new Grid
            {
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _clip.Children.Add(_text);
            Content = _clip;

            // StackPanel 给子元素正无穷宽度测量，会撑开 _clip 使 ActualWidth==textWidth，
            // 跑马灯永远判定不溢出。强制按父约束裁剪：重写测量，不让内容超过约束宽度。
            // 通过限制 _text 的最大宽度跟随 _clip 实际约束来实现。

            // 字体变化会通过上面的绑定同步到 TextBlock，这里监听字号变化重算跑马灯
            // (字号变了，文字宽度也变)
            var desc = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                FontSizeProperty, typeof(MarqueeText));
            desc?.AddValueChanged(this, (_, _) => UpdateMarquee());

            _clip.SizeChanged += (_, _) => UpdateMarquee();
            IsVisibleChanged += (_, _) => UpdateMarquee();
            // LayoutUpdated 兜底：IsVisibleChanged/SizeChanged 触发时 ActualWidth 可能还是 0
            // 或布局未稳定（尤其首次进入展开态），等布局走完再测一次。
            LayoutUpdated += (_, _) => UpdateMarquee();
        }

        /// <summary>
        /// 用 FormattedText 独立测量文本真实宽度，不受 TextBlock 的 MaxWidth/约束影响。
        /// </summary>
        private double MeasureTextWidth()
        {
            if (string.IsNullOrEmpty(_text.Text)) return 0;
            var ft = new FormattedText(
                _text.Text,
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
                FontSize,
                Foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return ft.Width;
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var m = (MarqueeText)d;
            m._text.Text = (string)e.NewValue ?? string.Empty;
            // 文本变了：先停掉旧动画，强制重算（绕过 UpdateMarquee 的"动画在跑"早退）
            if (m._anim != null)
            {
                m._transform.BeginAnimation(TranslateTransform.XProperty, null);
                m._anim = null;
            }
            m._lastText = ""; // 失效缓存，强制重测
            m.UpdateMarquee();
        }

        private double _lastViewWidth = -1;
        private string _lastText = "";

        private void UpdateMarquee()
        {
            // 动画已在跑就不打扰（避免 LayoutUpdated 每帧打断滚动）
            if (_anim != null) return;

            if (!IsVisible || string.IsNullOrEmpty(_text.Text))
            {
                _transform.X = 0;
                return;
            }

            // 优先用显式钉的 MaxViewWidth（与父容器动画解耦，判定稳定）；
            // 未设时退化为读 _clip.ActualWidth（兼容老用法）。
            double viewWidth = MaxViewWidth > 0 ? MaxViewWidth : _clip.ActualWidth;
            if (viewWidth <= 0)
            {
                _transform.X = 0;
                return;
            }

            // 文本或可视宽没变就别重复测量（LayoutUpdated 高频触发）
            if (viewWidth == _lastViewWidth && _text.Text == _lastText)
                return;
            _lastViewWidth = viewWidth;
            _lastText = _text.Text;

            _transform.X = 0;

            // FormattedText 独立测真实文本宽度（不受 TextBlock 约束/MaxWidth 影响）
            double textWidth = MeasureTextWidth();

            if (textWidth <= viewWidth + 0.5)
                return;   // 没溢出，静止

            double distance = textWidth - viewWidth;
            _anim = new DoubleAnimation
            {
                From = 0,
                To = -distance,
                Duration = TimeSpan.FromSeconds(Math.Max(2.5, distance / 45.0)), // ~45px/s
                BeginTime = TimeSpan.FromSeconds(0.8),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            _transform.BeginAnimation(TranslateTransform.XProperty, _anim);
        }
    }
}
