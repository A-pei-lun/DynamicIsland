namespace DynamicIsland.Island
{
    /// <summary>
    /// 不可变胶囊布局快照——width/height 的单一真相源。
    /// MainWindow 永远不直接 Width=420，只从此结构读取并应用。
    ///
    /// 数据流：Source.Measure() → IslandLayout → AnimationManager 补间 → MainWindow 只读
    /// </summary>
    /// <summary>玻璃背景强度档位。tier 决定，进 IslandLayout 快照，Renderer 只读消费（调模糊/底色强度）。</summary>
    public enum BackdropStrength { Subtle, Medium, Strong }

    public readonly record struct IslandLayout(
        double Width, double Height,
        BackdropStrength Backdrop = BackdropStrength.Subtle)
    {
        // ── 固定尺寸预设 ────────────────────────────────────────────

        public static readonly IslandLayout Collapsed = new(200, 40, BackdropStrength.Subtle);
        public static readonly IslandLayout Hovered   = new(228, 48, BackdropStrength.Medium);
        public static readonly IslandLayout Expanded  = new(720, 240, BackdropStrength.Strong);

        // ── 内联 alert 宽度边界 ──────────────────────────────────────
        // 下限保证从 Collapsed/Hovered 跳过来时有可见尺寸变化；
        // 上限防止消息文本过长把岛撑太宽。
        public const double AlertMinWidth = 310;
        public const double AlertMaxWidth = 460;

        // ── 收起态宽度上限因子（占屏幕宽度的比例） ─────────────────
        public const double CollapsedScreenFraction = 2.0 / 5.0;

        // ── 工厂 ────────────────────────────────────────────────────

        /// <summary>保持高度不变，只改宽度。</summary>
        public IslandLayout WithWidth(double w) => new(w, Height, Backdrop);
        public IslandLayout WithBackdrop(BackdropStrength b) => new(Width, Height, b);
    }
}
