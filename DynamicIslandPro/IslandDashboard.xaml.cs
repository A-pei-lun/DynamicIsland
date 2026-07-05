using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicIslandPro.Island;
using DynamicIslandPro.Sources;

namespace DynamicIslandPro
{
    /// <summary>
    /// 展开态组合仪表盘。上方是可滚轮翻页的面板区，下方是固定系统资源条。
    /// 与收起态的"单源仲裁"解耦：展开后无论谁占着岛，都能翻页查看媒体控制 / 系统详情 / 后续更多页。
    /// </summary>
    public partial class IslandDashboard : UserControl
    {
        private readonly List<IIslandPanel> _panels;
        private readonly SystemResourceSource _system;
        private IIslandPanel? _current;
        private List<IIslandPanel> _available = new();

        public IslandDashboard(IEnumerable<IIslandPanel> panels, SystemResourceSource system)
        {
            InitializeComponent();

            _panels = panels.OrderBy(p => p.Order).ToList();
            _system = system;
            SystemStrip.DataContext = system;

            foreach (var p in _panels)
                p.AvailabilityChanged += OnPanelAvailabilityChanged;

            RebuildAvailable(showFirst: true);

            // 资源数值变化时脉冲整个系统条（约 400ms 平滑过渡）
            _system.PropertyChanged += OnSystemValueChanged;
        }

        // ─── 资源条数值脉冲 ──────────────────────────────────────────
        private void OnSystemValueChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SystemResourceSource.Cpu)
                or nameof(SystemResourceSource.Ram)
                or nameof(SystemResourceSource.Gpu))
            {
                // 值变化时脉冲 ScaleX 1.0→1.04→1.0，400ms
                var pulse = new DoubleAnimation
                {
                    To = 1.04,
                    Duration = TimeSpan.FromMilliseconds(200),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop,
                    AutoReverse = true,
                };
                var scale = new ScaleTransform(1, 1);
                SystemStrip.RenderTransform = scale;
                SystemStrip.RenderTransformOrigin = new Point(0.5, 0.5);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            }
        }

        // ─── 可用页集合 ────────────────────────────────────────────
        private List<IIslandPanel> GetAvailable()
            => _panels.Where(p => p.IsAvailable).OrderBy(p => p.Order).ToList();

        private void RebuildAvailable(bool showFirst)
        {
            _available = GetAvailable();
            if (_available.Count == 0)
            {
                _current = null;
                PageHost.Content = null;
                EmptyHint.Visibility = Visibility.Visible;
                RenderDots();
                return;
            }

            EmptyHint.Visibility = Visibility.Collapsed;
            // 首次进入或当前页已失效，退回首页
            if (showFirst || _current == null || !_available.Contains(_current))
                _current = _available[0];

            PageHost.Content = _current.View;
            RenderDots();
        }

        private void OnPanelAvailabilityChanged(object? sender, EventArgs e)
        {
            // 可用页集合变了：若当前页失效就切到首页，并刷新指示点
            _available = GetAvailable();
            if (_available.Count == 0)
            {
                _current = null;
                PageHost.Content = null;
                EmptyHint.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyHint.Visibility = Visibility.Collapsed;
                if (_current == null || !_available.Contains(_current))
                {
                    _current = _available[0];
                    PageHost.Content = _current.View;
                }
            }
            RenderDots();
        }

        // ─── 滚轮翻页 ──────────────────────────────────────────────
        // 分区域：鼠标悬停在指示点行上 → 滚轮翻页；在列表区（通知/统计的 ScrollViewer）上
        // → 放行让列表正常上下滚动，堆起来的通知可翻看被挤到下面的。
        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            // 不在指示点上，或只有一页：放行给列表 ScrollViewer 自己处理
            if (!DotsHost.IsMouseOver || _available.Count <= 1)
                return;

            int idx = _current == null ? 0 : _available.IndexOf(_current);
            if (idx < 0) idx = 0;

            // 滚轮上(正 Delta)= 上一页；下(负)= 下一页
            idx += e.Delta > 0 ? -1 : 1;
            GoToPage(idx);

            // 在指示点上翻页：阻止列表 ScrollViewer 抢滚轮
            e.Handled = true;
        }

        // ─── 指示点（可点击） ────────────────────────────────────────
        private void RenderDots()
        {
            DotsHost.Children.Clear();
            for (int i = 0; i < _available.Count; i++)
            {
                int capturedIndex = i; // 闭包捕获当前下标
                var isCurrent = ReferenceEquals(_available[i], _current);
                var dot = new Ellipse
                {
                    Width = 5,
                    Height = 5,
                    Fill = new SolidColorBrush(
                        isCurrent ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                                  : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
                };
                // 用透明 Border 包 Ellipse：扩大点击区（光点 5px 太小点不准），
                // Background=Transparent 才能命中 hit-test（null 背景不收鼠标）。
                var hit = new Border
                {
                    Background = Brushes.Transparent,
                    Padding = new Thickness(5, 3, 5, 3),
                    Cursor = Cursors.Hand,
                    Child = dot,
                };
                hit.MouseLeftButtonUp += (_, _) => GoToPage(capturedIndex);
                DotsHost.Children.Add(hit);
            }
            // 只有一页时不显示指示点，免得孤零零一个点
            DotsHost.Visibility = _available.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ─── 切到指定页 ──────────────────────────────────────────────
        // 滚轮和点击指示点共用。下标模运算循环；越界/非法静默（点当前页也无害，会重渲一次）。
        private void GoToPage(int index)
        {
            if (_available.Count == 0) return;
            index = ((index % _available.Count) + _available.Count) % _available.Count;
            _current = _available[index];
            PageHost.Content = _current.View;
            RenderDots();
        }
    }
}
