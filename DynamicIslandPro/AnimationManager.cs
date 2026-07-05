using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DynamicIslandPro
{
    /// <summary>
    /// 统一动画管理器。所有 DoubleAnimation / 延迟回调都走这里，
    /// 外面不自己 new Storyboard / DoubleAnimation / DispatcherTimer。
    /// </summary>
    public static class AnimationManager
    {
        /// <summary>对目标依赖属性做一段补间动画。动画结束后属性停在 to 值（FillBehavior.Stop + SetCurrentValue）。</summary>
        /// <param name="target">动画目标</param>
        /// <param name="prop">依赖属性</param>
        /// <param name="to">目标值</param>
        /// <param name="duration">时长（用 MotionToken.* 或自定 Duration）</param>
        /// <param name="ease">缓动（用 MotionToken.Ease* 或自定）</param>
        /// <param name="onDone">动画完成回调（可选）</param>
        public static void Animate(DependencyObject target, DependencyProperty prop, double to,
            Duration duration, IEasingFunction ease, Action? onDone = null)
        {
            var anim = new DoubleAnimation
            {
                To = to,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            anim.Completed += (_, _) =>
            {
                if (target is IAnimatable a)
                    a.BeginAnimation(prop, null);
                target.SetCurrentValue(prop, to);
                onDone?.Invoke();
            };
            if (target is IAnimatable a2)
                a2.BeginAnimation(prop, anim);
        }

        /// <summary>对 FrameworkElement 做一段补间动画（重载，自动处理 BeginAnimation null）。</summary>
        public static void Animate(FrameworkElement target, DependencyProperty prop, double to,
            Duration duration, IEasingFunction ease, Action? onDone = null)
            => Animate((DependencyObject)target, prop, to, duration, ease, onDone);

        /// <summary>延迟执行回调，内部用 DispatcherTimer 单次触发。</summary>
        public static void DelayedInvoke(int ms, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
        }
    }
}
