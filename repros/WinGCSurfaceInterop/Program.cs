// WinGCSurfaceInterop — M2: 现代 C#/WinRT interop + 可视化回读
//
// 流程:
//   M1 基线（收到帧）→ frame.Surface → As<TInterop> → IDirect3DDxgiInterfaceAccess
//   → GetInterface(ID3D11Texture2D) → GetDesc → staging → Map → BMP
//
// 输出:
//   repros/artifacts/A-WinGC/baseline.log
//   repros/artifacts/A-WinGC/result.json
//   repros/artifacts/A-WinGC/frame.bmp

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
using WinRT.Interop;

namespace WinGCSurfaceInterop;

internal static class Program
{
    // ─── IDirect3DDxgiInterfaceAccess ─────────────────────────────────────
    // IID: A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig] int GetInterface(ref Guid iid, out IntPtr p);
    }

    static readonly Guid IID_ID3D11Texture2D = typeof(ID3D11Texture2D).GUID;

    // ─── WinGC interop (手写 P/Invoke) ────────────────────────────────────

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
    static readonly string BmpPath = Path.Combine(ArtifactsDir, "frame.bmp");

    // ─── 同步 ────────────────────────────────────────────────────────────

    static readonly ManualResetEventSlim FrameReceived = new(false);
    static Direct3D11CaptureFrame? CapturedFrame;
    static int CallbackThreadId = -1;
    static long FrameArrivedTicks;

    // ─── 命令行参数 ──────────────────────────────────────────────────────

    static bool LegacyComparisonMode = false;

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

    static async Task<int> Main(string[] args)
    {
        LegacyComparisonMode = args.Contains("--legacy-comparison");
        if (LegacyComparisonMode)
            Log("!!! 使用 --legacy-comparison 模式（对照观察，不参与 PASS 判定）");

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
            result.Status = "FAIL";
            result.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            result.ErrorHResult = $"0x{ex.HResult:X8}";
        }

        sw.Stop();
        result.ElapsedMs = sw.ElapsedMilliseconds;

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
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_DEBUG,
            ReadOnlySpan<D3D_FEATURE_LEVEL>.Empty,
            7,
            out ID3D11Device dev11,
            out D3D_FEATURE_LEVEL featureLevel,
            out ID3D11DeviceContext ctx);

        LogHr("D3D11CreateDevice", hr);
        result.D3D11CreateDeviceHr = $"0x{(int)hr:X8}";

        if (hr < 0)
        {
            // 如果 Debug layer 不可用，回退到无 debug 标志重试
            Log("  Debug layer 可能不可用，回退到无 debug 标志...");
            hr = PInvoke.D3D11CreateDevice(
                null,
                D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                default,
                default(D3D11_CREATE_DEVICE_FLAG),
                ReadOnlySpan<D3D_FEATURE_LEVEL>.Empty,
                7,
                out dev11,
                out featureLevel,
                out ctx);
            LogHr("D3D11CreateDevice (no debug)", hr);
            if (hr < 0)
            {
                result.Status = "FAIL";
                result.ErrorMessage = "D3D11CreateDevice failed";
                return;
            }
        }

        Log($"  Feature level: {featureLevel}");

        // ── 获取 adapter 信息 ─────────────────────────────────────────────
        Log("--- Adapter 信息 ---");
        try
        {
            var dxgiDevice = dev11.As<IDXGIDevice>();
            IntPtr dxgiDevicePtr = Marshal.GetIUnknownForObject(dxgiDevice);
            Guid iidAdapter = typeof(IDXGIAdapter).GUID;
            int qir = Marshal.QueryInterface(dxgiDevicePtr, ref iidAdapter, out IntPtr adapterPtr);
            LogHr("QI(IDXGIAdapter)", qir);
            Marshal.Release(dxgiDevicePtr);

            if (qir >= 0)
            {
                // 简化：只记录 adapter 指针，不深入查询 desc（避免 CsWin32 签名问题）
                Log($"  Adapter QI 成功 (ptr=0x{adapterPtr:X8})");
                Marshal.Release(adapterPtr);
            }
        }
        catch (Exception ex)
        {
            Log($"  Adapter 信息获取失败: {ex.Message}");
        }

        // ── 2. IDXGIDevice → WinRT IDirect3DDevice ───────────────────────
        Log("--- 步骤 2: CreateDirect3D11DeviceFromDXGIDevice ---");

        IntPtr devUnk = Marshal.GetIUnknownForObject(dev11);
        Guid iidDxgi = typeof(IDXGIDevice).GUID;
        int qirDxgi = Marshal.QueryInterface(devUnk, ref iidDxgi, out IntPtr dxgiPtr);
        LogHr("QueryInterface(IDXGIDevice)", qirDxgi);
        Marshal.Release(devUnk);

        if (qirDxgi < 0)
        {
            result.Status = "FAIL";
            result.ErrorMessage = "QI for IDXGIDevice failed";
            result.DxgiDeviceQiHr = $"0x{qirDxgi:X8}";
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

        GraphicsCaptureItem? item = null;
        IntPtr itemPtr = IntPtr.Zero;
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
            int createItemHr = interop.CreateForMonitor(hmon, ref iidItem, out itemPtr);
            LogHr("CreateForMonitor", createItemHr);

            Marshal.Release(interopPtr);

            if (createItemHr < 0)
            {
                result.Status = "FAIL";
                result.ErrorMessage = "CreateForMonitor failed";
                return;
            }

            item = GraphicsCaptureItem.FromAbi(itemPtr);
            var itemSize = item.Size;
            Log($"  Capture item size: {itemSize.Width}x{itemSize.Height}");
            result.CaptureItemWidth = itemSize.Width;
            result.CaptureItemHeight = itemSize.Height;
        }
        catch (Exception ex)
        {
            Log($"  GraphicsCaptureItem 异常: {ex.GetType().Name}: {ex.Message}");
            result.Status = "FAIL";
            result.ErrorMessage = $"GraphicsCaptureItem: {ex.Message}";
            return;
        }

        // ── 4. Frame pool + capture session ──────────────────────────────
        Log("--- 步骤 4: Frame pool + capture session ---");

        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            d3dDev, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);

        var session = pool.CreateCaptureSession(item);

        // ── 5. FrameArrived ──────────────────────────────────────────────
        Log("--- 步骤 5: 注册 FrameArrived + 启动 ---");

        int mainThreadId = Environment.CurrentManagedThreadId;
        Log($"  主线程 ID: {mainThreadId}");

        pool.FrameArrived += OnFrameArrived;

        session.StartCapture();
        Log("  Capture 已启动，等待第一帧 (10秒超时)...");

        // ── 6. 等待第一帧（10秒超时）─────────────────────────────────────
        bool gotFrame = FrameReceived.Wait(TimeSpan.FromSeconds(10));

        // 取消 FrameArrived 订阅，防止后续帧干扰
        pool.FrameArrived -= OnFrameArrived;

        if (gotFrame && CapturedFrame != null)
        {
            var frame = CapturedFrame;
            var frameSize = frame.ContentSize;
            var surface = frame.Surface;

            Log($"--- 收到第一帧 ---");
            Log($"  Frame content size: {frameSize.Width}x{frameSize.Height}");
            Log($"  frame.Surface != null: {surface != null}");
            Log($"  回调线程 ID: {CallbackThreadId} (主线程: {mainThreadId})");

            result.FrameReceived = true;
            result.FrameWidth = frameSize.Width;
            result.FrameHeight = frameSize.Height;
            result.SurfaceNotNull = surface != null;
            result.CallbackThreadId = CallbackThreadId;

            if (surface != null)
            {
                // ── 7. Surface interop: 现代 C#/WinRT 路径 ────────────────
                Log("--- 步骤 7: Surface interop (现代 C#/WinRT 路径) ---");

                try
                {
                    // 默认路径：使用 C#/WinRT As<TInterop>()
                    Log("  尝试 C#/WinRT As<TInterop>() 路径...");
                    var access = surface.As<IDirect3DDxgiInterfaceAccess>();
                    Log("  As<IDirect3DDxgiInterfaceAccess>() 成功");

                    result.AsInteropSuccess = true;

                    // 调用 GetInterface(ID3D11Texture2D)
                    Guid iidLocal = IID_ID3D11Texture2D;
                    int giHr = access.GetInterface(ref iidLocal, out IntPtr texPtr);
                    LogHr("  GetInterface(ID3D11Texture2D)", giHr);
                    result.GetInterfaceHr = $"0x{giHr:X8}";

                    if (giHr >= 0 && texPtr != IntPtr.Zero)
                    {
                        // GetInterface 返回的是原始 COM 指针，不是 WinRT 可检查对象
                        // 从指针创建 ID3D11Texture2D COM 包装器
                        // 方法: 先在 dev11 上创建 staging texture，再通过 CopyResource 从指针纹理复制
                        Log($"  texPtr=0x{texPtr:X8}，尝试获取纹理...");

                        // 由于无法直接包装 ID3D11Texture2D，采用间接方式：
                        // 先通过 GetInterface 获取纹理指针后，用 dev11 的 OpenSharedResource 访问
                        // 但 CaptureFrame 纹理不是共享资源，无法用 OpenSharedResource
                        // 改用直接 COM 指针转换：通过 Marshal.GetObjectForIUnknown 创建 RCW，
                        // 再用 CsWin32 的 ComWrappers 转换
                        var tex = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(texPtr);
                        Log("  ID3D11Texture2D 获取成功");

                        // ── 8. TextureDesc ────────────────────────────────
                        Log("--- 步骤 8: ID3D11Texture2D::GetDesc ---");
                        unsafe
                        {
                            D3D11_TEXTURE2D_DESC desc;
                            tex.GetDesc(&desc);

                            Log($"  Width: {desc.Width}, Height: {desc.Height}");
                            Log($"  Format: {desc.Format}");
                            Log($"  MipLevels: {desc.MipLevels}, ArraySize: {desc.ArraySize}");
                            Log($"  SampleDesc.Count: {desc.SampleDesc.Count}, Quality: {desc.SampleDesc.Quality}");
                            Log($"  Usage: {desc.Usage}");
                            Log($"  BindFlags: {desc.BindFlags}");
                            Log($"  CPUAccessFlags: {desc.CPUAccessFlags}");
                            Log($"  MiscFlags: {desc.MiscFlags}");

                            result.TextureWidth = (int)desc.Width;
                            result.TextureHeight = (int)desc.Height;
                            result.TextureFormat = desc.Format.ToString();
                            result.TextureMipLevels = (int)desc.MipLevels;
                            result.TextureArraySize = (int)desc.ArraySize;

                            // ── 9. Staging + CopyResource + Map ────────────
                            Log("--- 步骤 9: Staging texture + CopyResource + Map ---");

                            D3D11_TEXTURE2D_DESC stagingDesc = desc;
                            stagingDesc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
                            stagingDesc.BindFlags = 0;
                            stagingDesc.CPUAccessFlags = (D3D11_CPU_ACCESS_FLAG)0x20000; // D3D11_CPU_ACCESS_FLAG_READ
                            stagingDesc.MiscFlags = 0;
                            stagingDesc.MipLevels = 1;
                            stagingDesc.ArraySize = 1;

                            // 创建 staging texture (CsWin32: void CreateTexture2D(desc, null, out tex))
                            ID3D11Texture2D stagingTex = default!;
                            try
                            {
                                dev11.CreateTexture2D(stagingDesc, null, out stagingTex);
                                Log("  CreateTexture2D (staging) 成功");

                                // CopyResource
                                ctx.CopyResource(stagingTex, tex);
                                Log("  CopyResource 完成");

                                // Map (CsWin32 方法返回 void，失败时抛 COMException)
                                D3D11_MAPPED_SUBRESOURCE mapped;
                                uint subresource = 0;
                                ctx.Map(stagingTex, subresource, D3D11_MAP.D3D11_MAP_READ, 0, out mapped);
                                Log("  Map 成功");

                                result.MapSuccess = true;

                                // ── 10. 写 BMP ──────────────────
                                Log("--- 步骤 10: 写入 BMP ---");
                                unsafe
                                {
                                    WriteBmp(
                                        (IntPtr)mapped.pData,
                                        (int)desc.Width,
                                        (int)desc.Height,
                                        (int)mapped.RowPitch);
                                }

                                ctx.Unmap(stagingTex, subresource);
                                Log("  Unmap 完成");

                                // 检查 BMP
                                FileInfo fi = new(BmpPath);
                                result.BmpExists = fi.Exists;
                                result.BmpSize = fi.Exists ? fi.Length : 0;
                                Log($"  BMP 文件存在: {fi.Exists}");
                                Log($"  BMP 文件大小: {fi.Length} bytes");
                                Log($"  BMP 预期最小: {54 + (int)desc.Width * (int)desc.Height * 4} bytes");
                            }
                            catch (Exception exStaging)
                            {
                                Log($"  Staging 相关异常: {exStaging.GetType().Name}: {exStaging.Message}");
                                Log($"  HResult=0x{exStaging.HResult:X8}");
                                result.ErrorMessage = $"Staging: {exStaging.GetType().Name}: {exStaging.Message}";
                                result.ErrorHResult = $"0x{exStaging.HResult:X8}";
                                result.MapSuccess = false;
                            }
                            finally
                            {
                                if (stagingTex != null && stagingTex is IDisposable dispStaging)
                                    dispStaging.Dispose();
                            }
                        }

                        // 释放 COM 引用
                        if (tex is IDisposable dispTex) dispTex.Dispose();
                        Marshal.Release(texPtr);
                    }
                    else
                    {
                        result.GetInterfaceHr = $"0x{giHr:X8}";
                        result.ErrorMessage = $"GetInterface(ID3D11Texture2D) failed: hr=0x{giHr:X8}";
                    }
                }
                catch (Exception ex)
                {
                    Log($"  Surface interop 异常: {ex.GetType().Name}: {ex.Message}");
                    Log($"  HResult=0x{ex.HResult:X8}");
                    result.AsInteropSuccess = false;
                    result.ErrorMessage = $"As<TInterop> failed: {ex.GetType().Name}: {ex.Message}";
                    result.ErrorHResult = $"0x{ex.HResult:X8}";
                }

                // ── 11. (可选) 旧式 Marshal 对照 ──────────────────────────
                if (LegacyComparisonMode)
                {
                    Log("--- 步骤 11 (对照): 旧式 Marshal.GetIUnknownForObject ---");
                    try
                    {
                        IntPtr surfUnk = Marshal.GetIUnknownForObject(surface);
                        Log($"  Marshal.GetIUnknownForObject -> 0x{surfUnk:X8}");
                        // 这只做对照，不用于 PASS 判定
                        Marshal.Release(surfUnk);
                    }
                    catch (Exception ex)
                    {
                        Log($"  Legacy Marshal 对照异常: {ex.Message}");
                    }
                }
            }

            frame.Dispose();
            CapturedFrame = null;
        }
        else
        {
            Log($"  超时或未收到帧: gotFrame={gotFrame}, CapturedFrame={CapturedFrame != null}");
            result.FrameReceived = false;
        }

        // ── 清理 ──────────────────────────────────────────────────────────
        Log("--- 清理 ---");

        session.Dispose();
        Log("  Session disposed");
        pool.Dispose();
        Log("  Pool disposed");

        if (itemPtr != IntPtr.Zero)
            Marshal.Release(itemPtr);

        if (ctx is IDisposable dispCtx) dispCtx.Dispose();
        if (dev11 is IDisposable dispDev) dispDev.Dispose();

        Log("  清理完成");

        // ── 判定 ──────────────────────────────────────────────────────────
        if (LegacyComparisonMode)
        {
            Log("  => 判定: --legacy-comparison 模式，不参与 PASS 判定");
            result.Status = "INCONCLUSIVE";
            return;
        }

        if (!result.FrameReceived)
        {
            result.Status = "INCONCLUSIVE";
            result.ErrorMessage = "No frame received within 10s timeout";
        }
        else if (!result.SurfaceNotNull)
        {
            result.Status = "FAIL";
            result.ErrorMessage = "frame.Surface is null";
        }
        else if (!result.AsInteropSuccess)
        {
            result.Status = "FAIL";
            result.ErrorMessage = result.ErrorMessage ?? "As<TInterop> failed";
        }
        else if (!result.MapSuccess)
        {
            result.Status = "FAIL";
            result.ErrorMessage = result.ErrorMessage ?? "Map failed";
        }
        else if (!result.BmpExists)
        {
            result.Status = "FAIL";
            result.ErrorMessage = "BMP file not created";
        }
        else
        {
            bool allPass = result.AsInteropSuccess && result.GetInterfaceHr != null
                && result.TextureWidth > 0 && result.TextureHeight > 0
                && result.MapSuccess && result.BmpExists && result.BmpSize > 54;

            if (allPass)
            {
                Log("  => 判定: PASS (所有自动条件通过)");
                result.Status = "PASS";
            }
            else
            {
                Log("  => 判定: FAIL (部分自动条件未通过)");
                result.Status = "FAIL";
            }
        }
    }

    static void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
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

    // ─── BMP 写入 ─────────────────────────────────────────────────────────

    static unsafe void WriteBmp(IntPtr data, int width, int height, int rowPitch)
    {
        // BMP 文件结构: 14-byte header + 40-byte DIB header + pixel data
        // 32-bit BGRA, no compression
        int bpp = 4;
        int stride = width * bpp;
        // BMP 行对齐到 4 字节
        int bmpStride = (stride + 3) & ~3;
        int pixelDataSize = bmpStride * height;
        int fileSize = 14 + 40 + pixelDataSize;

        using var fs = new FileStream(BmpPath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // BITMAPFILEHEADER (14 bytes)
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(fileSize);          // bfSize
        bw.Write((ushort)0);         // bfReserved1
        bw.Write((ushort)0);         // bfReserved2
        bw.Write(14 + 40);           // bfOffBits

        // BITMAPINFOHEADER (40 bytes)
        bw.Write(40);                // biSize
        bw.Write(width);
        bw.Write(height);            // 正数 = 左下角原点
        bw.Write((ushort)1);         // biPlanes
        bw.Write((ushort)(bpp * 8)); // biBitCount
        bw.Write(0);                 // biCompression (BI_RGB)
        bw.Write(pixelDataSize);     // biSizeImage
        bw.Write(0);                 // biXPelsPerMeter
        bw.Write(0);                 // biYPelsPerMeter
        bw.Write(0);                 // biClrUsed
        bw.Write(0);                 // biClrImportant

        // Pixel data: bottom-up, BGR format
        // 从源数据复制，处理行对齐
        byte[] row = new byte[bmpStride];
        for (int y = height - 1; y >= 0; y--)
        {
            IntPtr srcRow = data + (y * rowPitch);
            Marshal.Copy(srcRow, row, 0, stride);
            // 剩余字节（对齐填充）保持 0
            bw.Write(row, 0, bmpStride);
        }

        Log($"  BMP 写入完成: {width}x{height}, {fileSize} bytes");
    }

    // ─── 结果输出 ────────────────────────────────────────────────────────

    static void WriteResults(ResultData result)
    {
        File.WriteAllText(LogPath, string.Join(Environment.NewLine, LogLines));
        Console.WriteLine($"\n日志已写入: {LogPath}");

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(result, options);
        File.WriteAllText(JsonPath, json);
        Console.WriteLine($"结果已写入: {JsonPath}");
    }

    // ─── 结果数据结构 ─────────────────────────────────────────────────────

    class ResultData
    {
        public string Stage { get; set; } = "M2";
        public string Status { get; set; } = "NOT RUN";
        public string? ErrorMessage { get; set; }
        public string? ErrorHResult { get; set; }
        public long ElapsedMs { get; set; }

        // D3D11
        public string? D3D11CreateDeviceHr { get; set; }
        public string? DxgiDeviceQiHr { get; set; }
        public string? CreateDirect3DDeviceHr { get; set; }

        // Adapter
        public string? AdapterName { get; set; }
        public string? AdapterLuid { get; set; }

        // Capture
        public int CaptureItemWidth { get; set; }
        public int CaptureItemHeight { get; set; }
        public bool FrameReceived { get; set; }
        public bool SurfaceNotNull { get; set; }
        public int FrameWidth { get; set; }
        public int FrameHeight { get; set; }
        public int CallbackThreadId { get; set; }

        // Surface interop
        public bool AsInteropSuccess { get; set; }
        public string? GetInterfaceHr { get; set; }

        // Texture
        public int TextureWidth { get; set; }
        public int TextureHeight { get; set; }
        public string? TextureFormat { get; set; }
        public int TextureMipLevels { get; set; }
        public int TextureArraySize { get; set; }

        // Readback
        public bool MapSuccess { get; set; }

        // BMP
        public bool BmpExists { get; set; }
        public long BmpSize { get; set; }
    }
}