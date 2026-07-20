using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace AcrylicSpike;

/// <summary>
/// DWM 材质 spike：为"亚克力模糊度下限"找阈值 + 验 state3 是否更轻。
/// 1=state4@0x14(主程序现状基底) / 2=state3@0x14(BLURBEHIND 更轻?) / 3=state4@0x08 / 4=state4@0x04 / Esc 退出。
/// 拖到彩色桌面上观察：每个是"黑"还是"能合成看穿"，以及 state3 vs state4 哪个模糊更轻。
/// </summary>
public partial class MainWindow : Window
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWCP_ROUND = 2;
    const int DWMSBT_NONE = 1;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int cbValue);

    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS { public int cxLeftWidth; public int cxRightWidth; public int cyTopHeight; public int cyBottomHeight; }

    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    const int WCA_ACCENT_POLICY = 19;
    const int ACCENT_DISABLED = 0;
    const int ACCENT_ENABLE_BLURBEHIND = 3;          // 老式柔和模糊（可能比 state4 轻）
    const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;    // 亚克力模糊（主程序现用）

    [StructLayout(LayoutKind.Sequential)]
    struct ACCENT_POLICY { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public IntPtr SizeOfData; }

    [DllImport("user32.dll")]
    static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    enum Mode { S4_0x14, S3_0x14, S4_0x08, S4_0x04 }
    Mode _mode = Mode.S4_0x14;

    public MainWindow() { InitializeComponent(); }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource src && src.CompositionTarget != null)
            src.CompositionTarget.BackgroundColor = Colors.Transparent;
        Apply(_mode);
    }

    void Apply(Mode mode)
    {
        _mode = mode;
        var hwnd = new WindowInteropHelper(this).Handle;

        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        int none = DWMSBT_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        SetAccent(hwnd, ACCENT_DISABLED, 0);

        var m = new MARGINS { cxLeftWidth = -1, cxRightWidth = 0, cyTopHeight = 0, cyBottomHeight = 0 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);

        string label;
        switch (mode)
        {
            case Mode.S4_0x14:
                SetAccent(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND, (0x14u << 24) | 0x1A1A1Au);
                label = "1·state4 亚克力 @0x14\n主程序模糊度=0 现状\n（基底对照：能合成、有模糊）";
                break;
            case Mode.S3_0x14:
                SetAccent(hwnd, ACCENT_ENABLE_BLURBEHIND, (0x14u << 24) | 0x1A1A1Au);
                label = "2·state3 BLURBEHIND @0x14\n模糊比 state4 轻？\n能合成还是黑/无效果？";
                break;
            case Mode.S4_0x08:
                SetAccent(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND, (0x08u << 24) | 0x1A1A1Au);
                label = "3·state4 亚克力 @0x08\n更透？还是黑？";
                break;
            case Mode.S4_0x04:
                SetAccent(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND, (0x04u << 24) | 0x1A1A1Au);
                label = "4·state4 亚克力 @0x04\n更透？还是黑？";
                break;
            default:
                label = "?";
                break;
        }
        ModeLabel.Text = label;
    }

    int SetAccent(IntPtr hwnd, int state, uint gradient)
    {
        var policy = new ACCENT_POLICY { AccentState = state, AccentFlags = 2, GradientColor = gradient, AnimationId = 0 };
        var data = new WindowCompositionAttributeData
        {
            Attribute = WCA_ACCENT_POLICY,
            Data = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>()),
            SizeOfData = (IntPtr)Marshal.SizeOf<ACCENT_POLICY>()
        };
        try { Marshal.StructureToPtr(policy, data.Data, false); return SetWindowCompositionAttribute(hwnd, ref data); }
        finally { Marshal.FreeHGlobal(data.Data); }
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.D1: Apply(Mode.S4_0x14); break;
            case Key.D2: Apply(Mode.S3_0x14); break;
            case Key.D3: Apply(Mode.S4_0x08); break;
            case Key.D4: Apply(Mode.S4_0x04); break;
            case Key.Escape: Close(); break;
        }
    }

    void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
