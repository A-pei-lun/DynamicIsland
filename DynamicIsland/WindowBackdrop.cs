using System;
using System.Runtime.InteropServices;

namespace DynamicIsland
{
    /// <summary>
    /// 把系统材质（Acrylic/Mica/全透明）应用到窗口。纯 P/Invoke，不引包。
    ///
    /// 两条渲染路径：
    /// - DWM 系统材质（DwmSetWindowAttribute DWMSBT_*）：Mica——borderless 窗实测纯黑，故本类不挂，由调用方 GlassBorder 平涂实色兜底。
    /// - Win10 accent（SetWindowCompositionAttribute）：亚克力=ACRYLICBLURBEHIND(state4)模糊；全透明=TRANSPARENTGRADIENT(state2)锐利穿透。
    ///   GradientColor 的 alpha=底色不透明度；alpha=0 会被 DWM 当不可见跳过→黑，故全透明与亚克力低端都走 MinAccentAlpha 而非 0。
    ///   选 accent 路径而非 DWM Acrylic 的原因：DWM Acrylic 不暴露底色 alpha，没法做滑块；
    ///   accent 路径跨收起/展开态渲染一致（同一条 policy），正好满足「收缩态随展开态一致」。
    ///   (AcrylicSpike 实锤：state2 锐利穿透 / state4 模糊 / Mica 在 borderless 上纯黑。)
    ///
    /// 前提：窗口 AllowsTransparency=False（分层窗口与 DWM backdrop 互斥），
    /// 且 HwndSource.CompositionTarget.BackgroundColor 已置 Transparent（由调用方在 OnSourceInitialized 做）。
    ///
    /// 关键坑（AcrylicSpike 实锤）：
    /// 1. 别用 WindowChrome——它会在 backdrop 上画不透明黑。圆角交给系统 DWMWA_WINDOW_CORNER_PREFERENCE。
    /// 2. borderless(WindowStyle=None) 窗口没有 DWM frame 区域，backdrop 无处渲染（只在角漏一点/全黑）。
    ///    必须 DwmExtendFrameIntoClientArea(cxLeftWidth=-1) 把 frame 扩满客户区，backdrop 才铺满。
    /// 3. AllowsTransparency=False 下系统圆角只有 ~8px，拿不到 20px（要 20px 只能退回透明窗=无真模糊）。
    /// 4. 切换路径前先两条都清掉（DWMSBT_NONE + ACCENT_DISABLED），否则叠加渲染。
    /// </summary>
    internal static class WindowBackdrop
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;        // 22000+ 用 20
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        private const int DWMSBT_NONE = 1;
        private const int DWMSBT_MAINWINDOW = 2;       // Mica
        private const int DWMSBT_TRANSIENTWINDOW = 3;  // DWM Acrylic（未用：不暴露 alpha）

        private const int DWMWCP_ROUND = 2;

        // ── Win10 accent（SetWindowCompositionAttribute）──
        private const int WCA_ACCENT_POLICY = 19;
        private const int ACCENT_DISABLED = 0;
        private const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2;  // 锐利穿透 + 底色（无模糊）——全透明用
        private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;    // 亚克力模糊 + 底色——亚克力用

        // accent GradientColor 的 alpha 下限。alpha=0 在 26200 上被 DWM 当作"整层 accent 不可见"跳过，
        // 露出 DwmExtendFrameIntoClientArea(-1) 扩展的纯黑 frame；必须非零 alpha 才会合成身后桌面。
        private const byte MinAccentAlpha = 0x14; // ~8%：足够触发合成，又尽量透

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int cbValue);

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS { public int cxLeftWidth; public int cxRightWidth; public int cyTopHeight; public int cyBottomHeight; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

        [StructLayout(LayoutKind.Sequential)]
        private struct ACCENT_POLICY
        {
            public int AccentState;
            public int AccentFlags;
            public uint GradientColor;   // 内存布局 0xAABBGGRR（alpha<<24 | blue<<16 | green<<8 | red）
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;          // 指向 ACCENT_POLICY
            public IntPtr SizeOfData;    // SIZE_T——指针宽（x64=8B）。写成 int 会让结构体尺寸错、调用 silent 失败
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        /// <summary>
        /// 应用系统材质。幂等——切换模式/主题/模糊度时重调即可。
        /// mode=Acrylic/Mica/Transparent；isDark 控制深浅色调（accent 底色与 DWM 深色模式）。
        /// Acrylic=blurEnabled 决定 state：开=state4 亚克力模糊，关=state2 锐利零模糊；tintIntensity 控底色浓淡(0=最透→100=最强)。
        /// Transparent=state2 锐利穿透(MinAccentAlpha)；Mica=本类不挂 backdrop(调用方 GlassBorder 平涂实色兜底)。alpha 下限 MinAccentAlpha 防 black frame。
        /// </summary>
        public static void Apply(IntPtr hwnd, BackdropMode mode, bool isDark, bool blurEnabled = true, double tintIntensity = 50.0)
        {
            if (hwnd == IntPtr.Zero) return;

            // 把 DWM frame 扩到整个客户区——否则 borderless 窗 backdrop 无处渲染
            MARGINS m = default;
            m.cxLeftWidth = -1;
            DwmExtendFrameIntoClientArea(hwnd, ref m);

            // 系统圆角（~8px）
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            // 深色色调（配合白字；浅色主题给 0）
            int dark = isDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            // 先两条路径都清掉，防叠加
            int none = DWMSBT_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
            SetAccent(hwnd, ACCENT_DISABLED, 0);

            switch (mode)
            {
                case BackdropMode.Mica:
                    // DWM Mica(DWMSBT_MAINWINDOW) 在 borderless(WindowStyle=None) 窗口上实测渲染纯黑
                    // （spike 验证 ±frame 都黑）。故本类不挂 DWM backdrop，由调用方 GlassBorder 平涂实色兜底。
                    break;

                case BackdropMode.Transparent:
                    // 全透明 = accent state2（TRANSPARENTGRADIENT）：锐利看穿 + 极小底色，无模糊。
                    // spike 验证 state2 透明生效；state4(亚克力)会模糊，不满足"全透明"。
                    // alpha=0 会被 DWM 当不可见跳过→黑，故用 MinAccentAlpha。
                    SetAccent(hwnd, ACCENT_ENABLE_TRANSPARENTGRADIENT,
                        ((uint)MinAccentAlpha << 24) | (isDark ? 0x1A1A1Au : 0xE8E8E8u));
                    break;

                default: // Acrylic
                    // 模糊开关决定 state：开=state4 亚克力模糊，关=state2 锐利零模糊。
                    // (AcrylicSpike 实锤：state4 模糊量 DWM 固定不可调，GradientColor alpha 只控底色浓淡；
                    //  想要"比亚克力更低的模糊"只能关模糊落 state2。state3 BLURBEHIND 同 alpha 更黑，已排除。)
                    // 底色 alpha 由 tintIntensity 0→100 线性 [MinAccentAlpha,255]，与模糊开关独立。
                    double t = Math.Clamp(tintIntensity, 0.0, 100.0) / 100.0;
                    byte a = (byte)Math.Round(MinAccentAlpha + (255 - MinAccentAlpha) * t);
                    // 灰系底色：深色用近黑 0x1A1A1A，浅色用近白 0xE8E8E8（R=G=B，无需 BGR 互换）
                    uint rgb = isDark ? 0x1A1A1Au : 0xE8E8E8u;
                    int accentState = blurEnabled
                        ? ACCENT_ENABLE_ACRYLICBLURBEHIND
                        : ACCENT_ENABLE_TRANSPARENTGRADIENT;
                    SetAccent(hwnd, accentState, ((uint)a << 24) | rgb);
                    break;
            }
        }

        private static void SetAccent(IntPtr hwnd, int state, uint gradient)
        {
            var policy = new ACCENT_POLICY
            {
                AccentState = state,
                AccentFlags = 2,
                GradientColor = gradient,
                AnimationId = 0
            };
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>()),
                SizeOfData = (IntPtr)Marshal.SizeOf<ACCENT_POLICY>()
            };
            try
            {
                Marshal.StructureToPtr(policy, data.Data, false);
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(data.Data);
            }
        }
    }
}
