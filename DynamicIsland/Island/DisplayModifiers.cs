namespace DynamicIsland.Island
{
    /// <summary>
    /// 正交修饰层，叠加在三级 tier（Ambient/Focus/Immersive）之上，不与 tier 耦合。
    /// tier 决定「展示什么/多大」，modifier 决定「是否压制/省电」。
    /// 优先级：Suppressed &gt; Sleep &gt; tier。Suppressed 激活时直接隐藏，不看 tier/Sleep。
    /// </summary>
    [Flags]
    public enum DisplayModifiers
    {
        None = 0,
        /// <summary>完全隐藏（全屏/DND）。胶囊 Visibility=Hidden，不留指示点。优先级最高。</summary>
        Suppressed = 1,
        /// <summary>省电态（长时 idle）。降玻璃抓取帧率 + 微暗 backdrop，不变尺寸。任何交互立即退出。</summary>
        Sleep = 2,
        // Dragging 预留：当前无拖拽交互，等真要做拖拽再加。
    }
}
