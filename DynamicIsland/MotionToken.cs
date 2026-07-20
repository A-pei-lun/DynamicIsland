using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicIsland
{
    /// <summary>
    /// 全局动画规范令牌。所有动效时长和缓动函数从这里取，
    /// 不做写死参数，保证全项目统一。
    ///
    /// 时长分档：
    ///   Instant  80ms — 瞬时微反馈（闪入闪出、首段弹跳）
    ///   Fast    120ms — 快速过渡（Hover 进出、按钮反馈）
    ///   Normal  200ms — 默认过渡（展开收起、面板显隐）
    ///   Slow    350ms — 强调过渡（抽纸进出、全屏动画）
    ///   Sweep   540ms — 扫光主行程（Alert 描边扫光）
    ///
    /// 曲线预设（CurvePresets）：
    ///   8 组预设，每组含 In / Out / InOut 三种缓动。
    ///   默认使用 Quadratic。设置窗可选曲线切换（TODO: 加 DisplaySettings 字段）。
    ///
    ///   贝塞尔曲线编辑器：TODO → 见 memory/bezier-curve-editor-idea.md
    /// </summary>
    public static class MotionToken
    {
        // ── 时长 ──
        public static readonly Duration Instant = Dur(80);
        public static readonly Duration Fast    = Dur(120);
        public static readonly Duration Normal  = Dur(200);
        public static readonly Duration Slow    = Dur(350);
        public static readonly Duration Sweep   = Dur(540);

        // ── 默认缓动（Quadratic，单例不反复 new）──
        public static IEasingFunction EaseOut   => _active.Out;
        public static IEasingFunction EaseIn    => _active.In;
        public static IEasingFunction EaseInOut => _active.InOut;

        private static CurvePreset _active;
        static MotionToken()
        {
            _active = Quadratic;
        }

        /// <summary>切换当前生效的曲线预设。设置窗改曲线时调用。</summary>
        public static void SetActiveCurve(CurvePreset preset)
        {
            _active = preset;
        }

        /// <summary>当前生效的曲线名。</summary>
        public static string ActiveCurveName => _active.Name;

        // ── 计时器预设（渐隐收回序列）──
        public static readonly TimeSpan ShrinkDelayToHover     = TimeSpan.FromMilliseconds(1000);
        public static readonly TimeSpan ShrinkDelayToCollapsed = TimeSpan.FromMilliseconds(500);

        // ── 弹跳多段 ──
        public const double BouncePressScale   = 0.97;
        public const double BounceOvershoot    = 1.02;
        public const double BounceRestScale    = 1.0;

        // ════════════════════════════════════════════════════════════════
        // 曲线预设
        // ════════════════════════════════════════════════════════════════

        public static readonly CurvePreset Quadratic = new("二次 / Quadratic",
            new QuadraticEase { EasingMode = EasingMode.EaseIn },
            new QuadraticEase { EasingMode = EasingMode.EaseOut },
            new QuadraticEase { EasingMode = EasingMode.EaseInOut });

        public static readonly CurvePreset Cubic = new("三次 / Cubic",
            new CubicEase { EasingMode = EasingMode.EaseIn },
            new CubicEase { EasingMode = EasingMode.EaseOut },
            new CubicEase { EasingMode = EasingMode.EaseInOut });

        public static readonly CurvePreset Sine = new("正弦 / Sine",
            new SineEase { EasingMode = EasingMode.EaseIn },
            new SineEase { EasingMode = EasingMode.EaseOut },
            new SineEase { EasingMode = EasingMode.EaseInOut });

        public static readonly CurvePreset Back = new("过冲 / Back",
            new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.6 },
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 },
            new BackEase { EasingMode = EasingMode.EaseInOut, Amplitude = 0.6 });

        public static readonly CurvePreset Elastic = new("橡皮筋 / Elastic",
            new ElasticEase { EasingMode = EasingMode.EaseIn, Oscillations = 3, Springiness = 3 },
            new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 3, Springiness = 3 },
            new ElasticEase { EasingMode = EasingMode.EaseInOut, Oscillations = 3, Springiness = 3 });

        public static readonly CurvePreset Bounce = new("弹跳 / Bounce",
            new BounceEase { EasingMode = EasingMode.EaseIn, Bounces = 3, Bounciness = 2 },
            new BounceEase { EasingMode = EasingMode.EaseOut, Bounces = 3, Bounciness = 2 },
            new BounceEase { EasingMode = EasingMode.EaseInOut, Bounces = 3, Bounciness = 2 });

        public static readonly CurvePreset Expo = new("爆发 / Exponential",
            new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 5 },
            new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 },
            new ExponentialEase { EasingMode = EasingMode.EaseInOut, Exponent = 5 });

        public static readonly CurvePreset Circle = new("圆弧 / Circle",
            new CircleEase { EasingMode = EasingMode.EaseIn },
            new CircleEase { EasingMode = EasingMode.EaseOut },
            new CircleEase { EasingMode = EasingMode.EaseInOut });

        /// <summary>所有预设列表，供设置窗下拉框绑定。</summary>
        public static readonly IReadOnlyList<CurvePreset> CurvePresets = new[]
        {
            Quadratic, Cubic, Sine, Back, Elastic, Bounce, Expo, Circle
        };

        private static Duration Dur(double ms) => TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>一条曲线预设：含 In / Out / InOut 三种缓动，给设置窗选。</summary>
    public readonly record struct CurvePreset(
        string Name,
        IEasingFunction In,
        IEasingFunction Out,
        IEasingFunction InOut);
}
