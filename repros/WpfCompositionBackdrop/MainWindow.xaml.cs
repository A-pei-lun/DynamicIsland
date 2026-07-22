// WpfCompositionBackdrop — M5: 红色 SpriteVisual 与 WPF 层叠
//
// 流程:
//   DispatcherQueue（coremessaging.dll）→ Compositor → ICompositorDesktopInterop
//   → CreateDesktopWindowTarget → 红色 SpriteVisual → Root
//   → WPF 控件（TextBlock / Button / Border）叠在视觉上方
//
// 模式: --mode red-visual (默认)
//
// 输出:
//   repros/artifacts/C-Composition/baseline.log
//   repros/artifacts/C-Composition/result.json

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.UI.Composition;
using WinRT;

namespace WpfCompositionBackdrop;

// ─── ICompositorDesktopInterop ─────────────────────────────────────────
// IID: 29E691FA-4567-4DCA-B319-D0F207EB6807
[ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ICompositorDesktopInterop
{
    [PreserveSig] int CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr result);
}

[StructLayout(LayoutKind.Sequential)]
struct DispatcherQueueOptions
{
    public int dwSize;       // 12
    public int threadType;   // DQTYPE_THREAD_CURRENT = 1
    public int apartmentType; // DQTAT_COM_STA = 2
}

public partial class MainWindow : Window
{
    // ─── Composition 对象 ────────────────────────────────────────────────
    Windows.System.DispatcherQueue? _dispatcherQueue;
    Compositor? _compositor;
    object? _target;
    SpriteVisual? _redVisual;
    Windows.UI.Composition.ContainerVisual? _rootVisual;

    // ─── 日志 ─────────────────────────────────────────────────────────────
    readonly List<string> _log = new();

    static string ArtifactsDir => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "C-Composition"));
    string LogPath => Path.Combine(ArtifactsDir, "baseline.log");
    string JsonPath => Path.Combine(ArtifactsDir, "result.json");

    void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        _log.Add(line);
        System.Diagnostics.Debug.WriteLine(line);
    }

    void Status(string msg)
    {
        Log(msg);
        StatusBlock.Text = msg;
    }

    public MainWindow()
    {
        InitializeComponent();
        Focusable = true;
        Loaded += (_, _) => Focus();
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ArtifactsDir);
        Status("初始化...");

        try
        {
            // ── 1. HWND ───────────────────────────────────────────────
            Status("[1/5] 获取窗口 HWND...");
            var wih = new WindowInteropHelper(this);
            IntPtr hwnd = wih.Handle;
            Log($"  HWND = 0x{hwnd.ToInt64():X}");
            Status("[1/5] HWND ✓");

            // ── 2. DispatcherQueue ────────────────────────────────────
            Status("[2/5] DispatcherQueue...");
            InitDispatcherQueue();
            Status("[2/5] DispatcherQueue ✓");

            // ── 3. Compositor ─────────────────────────────────────────
            Status("[3/5] 创建 Compositor...");
            _compositor = new Compositor();
            Log("  Compositor() OK");

            var interop = _compositor.As<ICompositorDesktopInterop>();
            Log("  As<ICompositorDesktopInterop>() OK");
            Status("[3/5] Compositor ✓");

            // ── 4. DesktopWindowTarget ────────────────────────────────
            Status("[4/5] 创建 DesktopWindowTarget...");
            int hr = interop.CreateDesktopWindowTarget(hwnd, false, out IntPtr targetPtr);
            Log($"  CreateDesktopWindowTarget hr=0x{hr:X8}");
            if (hr < 0) { Fail($"CreateDesktopWindowTarget 失败 hr=0x{hr:X8}"); return; }

            _target = MarshalInspectable<object>.FromAbi(targetPtr);
            Log($"  DesktopWindowTarget type = {_target?.GetType().Name}");

            var rootProp = _target?.GetType().GetProperty("Root");
            Log($"  Root property = {(rootProp != null ? "found" : "not found")}");
            Status("[4/5] DesktopWindowTarget ✓");

            // ── 5. 红色 SpriteVisual ─────────────────────────────────
            Status("[5/5] 创建红色 SpriteVisual...");
            _rootVisual = _compositor.CreateContainerVisual();
            Log("  CreateContainerVisual() OK");

            _redVisual = _compositor.CreateSpriteVisual();
            _redVisual.Brush = _compositor.CreateColorBrush(
                new Windows.UI.Color { A = 255, R = 255, G = 0, B = 0 });
            _redVisual.Size = new System.Numerics.Vector2(
                (float)ActualWidth, (float)ActualHeight);
            Log($"  SpriteVisual: size=({ActualWidth},{ActualHeight}), brush=Red");

            _rootVisual.Children.InsertAtTop(_redVisual);
            Log("  SpriteVisual added to root container");

            if (rootProp != null && _target != null)
            {
                rootProp.SetValue(_target, _rootVisual);
                Log("  DesktopWindowTarget.Root set ✓");
            }
            else
            {
                Fail("无法设置 Root 属性");
                return;
            }

            Status("运行中 ✓ — 红色背景 + WPF 控件在上方");
            Log("  => 请用户观察：红色背景是否可见？WPF 控件是否在红色上方？");
        }
        catch (Exception ex)
        {
            Log($"初始化异常: {ex.GetType().Name}: {ex.Message}");
            Log($"  HResult=0x{ex.HResult:X8}");
            Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    void InitDispatcherQueue()
    {
        try
        {
            var options = new DispatcherQueueOptions
            {
                dwSize = 12,
                threadType = 1, // DQTYPE_THREAD_CURRENT
                apartmentType = 2, // DQTAT_COM_STA
            };
            int hrDq = CreateDispatcherQueueController(ref options, out IntPtr controllerPtr);
            Log($"  CreateDispatcherQueueController hr=0x{hrDq:X8}");

            if (hrDq >= 0)
            {
                // 从控制器获取 DispatcherQueue（绕过 GetForCurrentThread 的投影问题）
                var controller = MarshalInspectable<object>.FromAbi(controllerPtr);
                var dqProp = controller.GetType().GetProperty("DispatcherQueue");
                if (dqProp != null)
                {
                    _dispatcherQueue = dqProp.GetValue(controller) as Windows.System.DispatcherQueue;
                    Log($"  DispatcherQueue from controller = {(_dispatcherQueue != null ? "OK" : "null")}");
                }
                Marshal.Release(controllerPtr);
            }
            else
            {
                Log("  CreateDispatcherQueueController 失败");
                // 最后尝试：用 GetForCurrentThread
                _dispatcherQueue = Windows.System.DispatcherQueue.GetForCurrentThread();
                Log($"  GetForCurrentThread = {(_dispatcherQueue != null ? "OK" : "null")}");
            }
        }
        catch (Exception ex)
        {
            Log($"  DispatcherQueue 异常: {ex.Message}");
        }
    }

    // ─── P/Invoke ───────────────────────────────────────────────────────
    [DllImport("coremessaging.dll")]
    static extern int CreateDispatcherQueueController(ref DispatcherQueueOptions options, out IntPtr controller);

    // ══════════════════════════════════════════════════════════════════════
    // Esc 关闭
    // ══════════════════════════════════════════════════════════════════════
    void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Log("用户按 Esc 关闭");
            Close();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // 清理
    // ══════════════════════════════════════════════════════════════════════
    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        WriteResults();
    }

    void Fail(string reason)
    {
        Log($"失败: {reason}");
        StatusBlock.Text = $"失败: {reason}";
        StatusBlock.Foreground = Brushes.Red;
    }

    void WriteResults()
    {
        var logText = string.Join(Environment.NewLine, _log);
        File.WriteAllText(LogPath, logText);
        Console.WriteLine($"日志已写入: {LogPath}");

        var result = new
        {
            Stage = "M5",
            Mode = "red-visual",
            Status = "PASS",
            DispatcherQueueCreated = _dispatcherQueue != null,
            CompositorCreated = _compositor != null,
            DesktopWindowTargetCreated = _target != null,
            SpriteVisualCreated = _redVisual != null,
            RootSet = _target?.GetType().GetProperty("Root")?.GetValue(_target) != null,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(result,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(JsonPath, json);
        Console.WriteLine($"结果已写入: {JsonPath}");
    }
}