// WinGCSurfaceInterop — M1: 只建立"收到第一帧"的基线
// 本阶段不执行任何 surface QI，只验证 WinGC 管道能否收到帧。
//
// 输出:
//   repros/artifacts/A-WinGC/baseline.log
//   repros/artifacts/A-WinGC/result.json

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using WinRT;

namespace WinGCSurfaceInterop;

internal static class Program
{
    // ─── WinGC interop (手写 P/Invoke，不用 CsWin32) ──────────────────────

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(IntPtr clsid, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll")]
    static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, uint len, out IntPtr hstr);

    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(IntPtr hstr);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    // ─── COM interop ─────────────────────────────────────────────────────

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr w, ref Guid iid, out IntPtr r);
        [PreserveSig] int CreateForMonitor(IntPtr m, ref Guid iid, out IntPtr r);
    }

    static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    // ─── 输出路径 ─────────────────────────────────────────────────────────

    static readonly string ArtifactsDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "A-WinGC"));

    static readonly string LogPath = Path.Combine(ArtifactsDir, "baseline.log");
    static readonly string JsonPath = Path.Combine(ArtifactsDir, "result.json");

    // ─── 同步 ────────────────────────────────────────────────────────────

    static readonly ManualResetEventSlim FrameReceived = new(false);
    static Direct3D11CaptureFrame? CapturedFrame;
    static int CallbackThreadId = -1;
    static long FrameArrivedTicks;

    // ─── 日志 ────────────────────────────────────────────────────────────

    static readonly List<string> LogLines = new();

    static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        LogLines.Add(line);
        Console.WriteLine(line);
    }

    static void LogHr(string label, int hr)
    {
        Log($"{label} hr=0x{hr:X8} ({(hr >= 0 ? "S_OK" : "FAILED")})");
    }

    static void LogHr(string label, HRESULT hr)
    {
        LogHr(label, (int)hr);
    }

    // ─── 主流程 ──────────────────────────────────────────────────────────

    static async Task<int> Main()
    {
        // 确保输出目录存在
        Directory.CreateDirectory(ArtifactsDir);

        var result = new ResultData();
        var sw = Stopwatch.StartNew();

        try
        {
            await RunCapture(result);
        }
        catch (Exception ex)
        {
            Log($"未处理的异常: {ex.GetType().Name}: {ex.Message}");
            Log($"  HResult=0x{ex.HResult:X8}");
            Log($"  {ex.StackTrace}");
            result.Status = "FAIL";
            result.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            result.ErrorHResult = $"0x{ex.HResult:X8}";
        }

        sw.Stop();
        result.ElapsedMs = sw.ElapsedMilliseconds;

        // 写入结果
        WriteResults(result);

        Log($"完成。耗时 {result.ElapsedMs} ms，状态: {result.Status}");
        return result.Status == "PASS" ? 0 : 1;
    }

    static async Task RunCapture(ResultData result)
    {
        // ── 1. D3D11 device ──────────────────────────────────────────────
        Log("--- 步骤 1: D3D11CreateDevice ---");
        HRESULT hr = PInvoke.D3D11CreateDevice(
            null,
            D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            default,
            default(D3D11_CREATE_DEVICE_FLAG),
            ReadOnlySpan<D3D_FEATURE_LEVEL>.Empty,
            7,
            out ID3D11Device dev11,
            out D3D_FEATURE_LEVEL featureLevel,
            out ID3D11DeviceContext ctx);

        LogHr("D3D11CreateDevice", hr);
        result.D3D11CreateDeviceHr = $"0x{(int)hr:X8}";

        if (hr < 0)
        {
            result.Status = "FAIL";
            result.ErrorMessage = "D3D11CreateDevice failed";
            return;
        }

        Log($"  Feature level: {featureLevel}");

        // ── 2. IDXGIDevice → WinRT IDirect3DDevice ───────────────────────
        Log("--- 步骤 2: CreateDirect3D11DeviceFromDXGIDevice ---");

        IntPtr devUnk = Marshal.GetIUnknownForObject(dev11);
        Guid iidDxgi = typeof(IDXGIDevice).GUID;
        int qir = Marshal.QueryInterface(devUnk, ref iidDxgi, out IntPtr dxgiPtr);
        LogHr("QueryInterface(IDXGIDevice)", qir);
        Marshal.Release(devUnk);

        if (qir < 0)
        {
            result.Status = "FAIL";
            result.ErrorMessage = "QI for IDXGIDevice failed";
            result.DxgiDeviceQiHr = $"0x{qir:X8}";
            Marshal.Release(dxgiPtr);
            return;
        }

        int createHr = CreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out IntPtr inspectablePtr);
        LogHr("CreateDirect3D11DeviceFromDXGIDevice", createHr);
        result.CreateDirect3DDeviceHr = $"0x{createHr:X8}";
        Marshal.Release(dxgiPtr);

        if (createHr < 0)
        {
            result.Status = "FAIL";
            result.ErrorMessage = "CreateDirect3D11DeviceFromDXGIDevice failed";
            return;
        }

        IDirect3DDevice d3dDev = MarshalInspectable<IDirect3DDevice>.FromAbi(inspectablePtr);
        Log("  IDirect3DDevice 创建成功");

        // ── 3. GraphicsCaptureItem for primary monitor ───────────────────
        Log("--- 步骤 3: GraphicsCaptureItem (primary monitor) ---");

        try
        {
            string clsid = "Windows.Graphics.Capture.GraphicsCaptureItem";
            int wsHr = WindowsCreateString(clsid, (uint)clsid.Length, out IntPtr hstr);
            LogHr("WindowsCreateString", wsHr);

            if (wsHr < 0)
            {
                result.Status = "FAIL";
                result.ErrorMessage = "WindowsCreateString failed";
                return;
            }

            Guid iop = IID_IGraphicsCaptureItemInterop;
            int roHr = RoGetActivationFactory(hstr, ref iop, out IntPtr factoryPtr);
            LogHr("RoGetActivationFactory", roHr);
            WindowsDeleteString(hstr);

            if (roHr < 0)
            {
                result.Status = "FAIL";
                result.ErrorMessage = "RoGetActivationFactory failed";
                return;
            }

            int qiInteropHr = Marshal.QueryInterface(factoryPtr, ref iop, out IntPtr interopPtr);
            LogHr("QI IGraphicsCaptureItemInterop", qiInteropHr);
            Marshal.Release(factoryPtr);

            if (qiInteropHr < 0)
            {
                result.Status = "FAIL";
                result.ErrorMessage = "QI for IGraphicsCaptureItemInterop failed";
                return;
            }

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetTypedObjectForIUnknown(
                interopPtr, typeof(IGraphicsCaptureItemInterop));

            IntPtr hmon = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
            Log($"  Monitor handle: 0x{hmon:X8}");

            Guid iidItem = IID_IGraphicsCaptureItem;
            int createItemHr = interop.CreateForMonitor(hmon, ref iidItem, out IntPtr itemPtr);
            LogHr("CreateForMonitor", createItemHr);

            Marshal.Release(interopPtr);

            if (createItemHr < 0)
            {
                result.Status = "FAIL";
                result.ErrorMessage = "CreateForMonitor failed";
                return;
            }

            GraphicsCaptureItem item = GraphicsCaptureItem.FromAbi(itemPtr);
            var itemSize = item.Size;
            Log($"  Capture item size: {itemSize.Width}x{itemSize.Height}");
            result.CaptureItemWidth = itemSize.Width;
            result.CaptureItemHeight = itemSize.Height;

            // ── 4. Frame pool + capture session ──────────────────────────
            Log("--- 步骤 4: Frame pool + capture session ---");

            var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                d3dDev, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, itemSize);

            var session = pool.CreateCaptureSession(item);

            // ── 5. FrameArrived ──────────────────────────────────────────
            Log("--- 步骤 5: 注册 FrameArrived + 启动 ---");

            int mainThreadId = Environment.CurrentManagedThreadId;
            Log($"  主线程 ID: {mainThreadId}");

            pool.FrameArrived += OnFrameArrived;

            session.StartCapture();
            Log("  Capture 已启动，等待第一帧 (10秒超时)...");

            // ── 6. 等待第一帧（10秒超时）─────────────────────────────────
            bool gotFrame = FrameReceived.Wait(TimeSpan.FromSeconds(10));

            if (gotFrame && CapturedFrame != null)
            {
                var frame = CapturedFrame;
                var frameSize = frame.ContentSize;
                var surface = frame.Surface;

                Log($"--- 收到第一帧 ---");
                Log($"  Frame content size: {frameSize.Width}x{frameSize.Height}");
                Log($"  frame.Surface != null: {surface != null}");
                Log($"  Frame arrived ticks: {FrameArrivedTicks}");
                Log($"  回调线程 ID: {CallbackThreadId} (主线程: {mainThreadId})");

                result.FrameReceived = true;
                result.FrameWidth = frameSize.Width;
                result.FrameHeight = frameSize.Height;
                result.SurfaceNotNull = surface != null;
                result.CallbackThreadId = CallbackThreadId;
                result.MainThreadId = mainThreadId;
                result.FrameArrivedTicks = FrameArrivedTicks;

                frame.Dispose();
                CapturedFrame = null;
            }
            else
            {
                Log($"  超时或未收到帧: gotFrame={gotFrame}, CapturedFrame={CapturedFrame != null}");
                result.FrameReceived = false;
            }

            // ── 7. 清理（逆序释放）───────────────────────────────────────
            Log("--- 清理 ---");

            session.Dispose();
            Log("  Session disposed");
            pool.FrameArrived -= OnFrameArrived;
            pool.Dispose();
            Log("  Pool disposed");

            // WinRT objects — 释放引用
            Marshal.Release(itemPtr);
            // inspectablePtr 已被 FromAbi 接管引用，不需要额外释放
            // 在 CsWin32 中，COM 包装器通过 ComWrappers 管理，GC 会处理
            // 但为了确定性释放，尝试通过 IDisposable 释放
            if (ctx is IDisposable dispCtx) dispCtx.Dispose();
            if (dev11 is IDisposable dispDev) dispDev.Dispose();

            Log("  清理完成");

            // ── 8. 判定 ──────────────────────────────────────────────────
            if (result.FrameReceived && result.SurfaceNotNull)
            {
                Log("  => 判定: PASS (frame != null && frame.Surface != null)");
                result.Status = "PASS";
            }
            else if (result.FrameReceived && !result.SurfaceNotNull)
            {
                Log("  => 判定: FAIL (frame != null but frame.Surface == null)");
                result.Status = "FAIL";
                result.ErrorMessage = "frame.Surface is null";
            }
            else
            {
                Log("  => 判定: INCONCLUSIVE (未收到帧)");
                result.Status = "INCONCLUSIVE";
                result.ErrorMessage = "No frame received within 10s timeout";
            }
        }
        catch (Exception ex)
        {
            Log($"  WinGC 异常: {ex.GetType().Name}: {ex.Message}");
            Log($"  HResult=0x{ex.HResult:X8}");
            result.Status = "FAIL";
            result.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            result.ErrorHResult = $"0x{ex.HResult:X8}";
        }
    }

    static void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // 只取第一帧
        if (CapturedFrame != null || FrameReceived.IsSet)
            return;

        try
        {
            CallbackThreadId = Environment.CurrentManagedThreadId;
            FrameArrivedTicks = Stopwatch.GetTimestamp();
            var frame = sender.TryGetNextFrame();
            if (frame != null)
            {
                CapturedFrame = frame;
                FrameReceived.Set();
            }
        }
        catch (Exception ex)
        {
            Log($"  FrameArrived 回调异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ─── 结果输出 ────────────────────────────────────────────────────────

    static void WriteResults(ResultData result)
    {
        // 写入日志
        File.WriteAllText(LogPath, string.Join(Environment.NewLine, LogLines));
        Console.WriteLine($"\n日志已写入: {LogPath}");

        // 写入 JSON
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(result, options);
        File.WriteAllText(JsonPath, json);
        Console.WriteLine($"结果已写入: {JsonPath}");
    }

    // ─── 结果数据结构 ─────────────────────────────────────────────────────

    class ResultData
    {
        public string Stage { get; set; } = "M1";
        public string Status { get; set; } = "NOT RUN";
        public string? ErrorMessage { get; set; }
        public string? ErrorHResult { get; set; }
        public long ElapsedMs { get; set; }

        // D3D11
        public string? D3D11CreateDeviceHr { get; set; }
        public string? DxgiDeviceQiHr { get; set; }
        public string? CreateDirect3DDeviceHr { get; set; }

        // Capture
        public int CaptureItemWidth { get; set; }
        public int CaptureItemHeight { get; set; }
        public bool FrameReceived { get; set; }
        public bool SurfaceNotNull { get; set; }
        public int FrameWidth { get; set; }
        public int FrameHeight { get; set; }
        public int CallbackThreadId { get; set; }
        public int MainThreadId { get; set; }
        public long FrameArrivedTicks { get; set; }
    }
}