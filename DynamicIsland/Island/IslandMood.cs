using System.Windows.Media;

namespace DynamicIsland.Island
{
    /// <summary>
    /// 胶囊边光情绪色——只染边缘/高光/光晕，不动整体背景。
    /// 三级模型（Ambient/Focus/Immersive）的视觉种子，v1.0 长成完整材质系统。
    ///
    /// 优先级：Ultra(彩虹) > Alert mood > Source mood > Neutral，Warning/Critical 可覆盖系统 accent。
    /// </summary>
    public enum IslandMood
    {
        /// <summary>无特殊情绪，跟随系统强调色。</summary>
        Neutral,
        /// <summary>信息通知——下载完成、USB 插入、蓝牙连接。</summary>
        Info,
        /// <summary>成功确认——充电接入。</summary>
        Success,
        /// <summary>警告——低电量、资源高占用。</summary>
        Warning,
        /// <summary>严重——极低电量、断网。</summary>
        Critical,
        /// <summary>媒体播放中——专辑色调。</summary>
        Media,
        /// <summary>Ultra 彩虹模式（最高优先级）。</summary>
        Ultra,
    }

    public static class IslandMoodColors
    {
        /// <summary>取 mood 对应的纯色画刷。Neutral 返回 null——调用方应用系统默认。</summary>
        public static SolidColorBrush? GetBrush(IslandMood mood) => mood switch
        {
            IslandMood.Neutral  => null,
            IslandMood.Info     => _info,
            IslandMood.Success  => _success,
            IslandMood.Warning  => _warning,
            IslandMood.Critical => _critical,
            IslandMood.Media    => _media,
            IslandMood.Ultra    => null,
            _                   => null,
        };

        public static System.Windows.Media.Color? GetColor(IslandMood mood) => GetBrush(mood)?.Color;

        private static readonly SolidColorBrush _info     = new(Color.FromRgb(0x4C, 0xC2, 0xFF)); // 蓝
        private static readonly SolidColorBrush _success  = new(Color.FromRgb(0x2E, 0xCC, 0x71)); // 绿
        private static readonly SolidColorBrush _warning  = new(Color.FromRgb(0xF1, 0xC4, 0x0F)); // 黄
        private static readonly SolidColorBrush _critical = new(Color.FromRgb(0xE7, 0x4C, 0x3C)); // 红
        private static readonly SolidColorBrush _media    = new(Color.FromRgb(0x9B, 0x59, 0xB6)); // 紫
    }
}
