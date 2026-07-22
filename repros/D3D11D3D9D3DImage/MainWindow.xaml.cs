// D3D11D3D9D3DImage — M3/M4: D3D9Ex → D3DImage 全链路
//
// 模式:
//   --mode d3d9-local        (M3) 纯 D3D9Ex ColorFill，无 D3D11
//   --mode d3d11-query        (M4) D3D9Ex 共享 → D3D11 Clear + Query event
//   --mode flush-only-observation (M4 对照) 无 query，不参与 PASS
//
// 输出:
//   repros/artifacts/B-D3D9D3DImage/baseline.log
//   repros/artifacts/B-D3D9D3DImage/result.json

using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Direct3D9;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace D3D11D3D9D3DImage;

public partial class MainWindow : Window
{
    // ─── 模式 ─────────────────────────────────────────────────────────────
    enum RunMode { D3D9Local, D3D11Query, FlushOnly }
    readonly RunMode _mode;

    static RunMode ParseMode()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--mode")
            {
                return args[i + 1] switch
                {
                    "d3d9-local" => RunMode.D3D9Local,
                    "d3d11-query" => RunMode.D3D11Query,
                    "flush-only-observation" => RunMode.FlushOnly,
                    _ => RunMode.D3D9Local,
                };
            }
        }
        return RunMode.D3D9Local; // 默认
    }

    // ─── D3D9 对象 ────────────────────────────────────────────────────────
    IDirect3D9Ex? _d3d9;
    IDirect3DDevice9Ex? _dev9;
    IDirect3DTexture9? _tex9;
    IDirect3DSurface9? _surf9;
    IDirect3DSurface9? _staging9;
    HANDLE _sharedHandle; // M4 共享 handle

    // ─── D3D11 对象 (M4) ──────────────────────────────────────────────────
    ID3D11Device? _dev11;
    ID3D11DeviceContext? _ctx11;
    ID3D11Texture2D? _tex11;
    ID3D11RenderTargetView? _rtv11;
    ID3D11Query? _query11;

    // ─── WPF D3DImage ────────────────────────────────────────────────────
    D3DImage? _d3dImage;
    bool _d3dImageReady;

    // ─── 颜色序列（12 色，与 M3 一致）────────────────────────────────────
    static readonly (string Name, uint D3DColor, float R, float G, float B)[] ColorSequence =
    {
        ("红色 RED",     0xFFFF0000, 1.0f, 0.0f, 0.0f),
        ("绿色 GREEN",   0xFF00FF00, 0.0f, 1.0f, 0.0f),
        ("蓝色 BLUE",    0xFF0000FF, 0.0f, 0.0f, 1.0f),
        ("青色 CYAN",    0xFF00FFFF, 0.0f, 1.0f, 1.0f),
        ("品红 MAGENTA", 0xFFFF00FF, 1.0f, 0.0f, 1.0f),
        ("黄色 YELLOW",  0xFFFFFF00, 1.0f, 1.0f, 0.0f),
        ("白色 WHITE",   0xFFFFFFFF, 1.0f, 1.0f, 1.0f),
        ("灰色 GRAY",    0xFF808080, 0.5f, 0.5f, 0.5f),
        ("橙色 ORANGE",  0xFFFF8000, 1.0f, 0.5f, 0.0f),
        ("紫色 PURPLE",  0xFF800080, 0.5f, 0.0f, 0.5f),
        ("青色 TEAL",    0xFF008080, 0.0f, 0.5f, 0.5f),
        ("黄绿 LIME",    0xFF80FF00, 0.5f, 1.0f, 0.0f),
    };
    const int TexW = 256;
    const int TexH = 256;
    const int TotalColors = 12;

    int _currentIndex = -1;
    int _completedCount = 0;
    bool _allCompleted;
    bool _earlyClose;

    // ─── 计时器 ───────────────────────────────────────────────────────────
    System.Windows.Threading.DispatcherTimer? _timer;

    // ─── 日志 ─────────────────────────────────────────────────────────────
    readonly List<string> _log = new();

    static string ArtifactsDir => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "B-D3D9D3DImage"));
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
        _mode = ParseMode();
        InitializeComponent();
        Focusable = true;
        Loaded += (_, _) => Focus();
    }

    // ══════════════════════════════════════════════════════════════════════
    // 初始化
    // ══════════════════════════════════════════════════════════════════════
    void OnLoaded(object sender, RoutedEventArgs e)
    {
        var modeName = _mode switch
        {
            RunMode.D3D9Local => "d3d9-local",
            RunMode.D3D11Query => "d3d11-query",
            RunMode.FlushOnly => "flush-only-observation",
            _ => "?",
        };
        ModeBlock.Text = $"M{(_mode == RunMode.D3D9Local ? "3" : "4")}: {modeName}";
        TitleBlock.Text = _mode == RunMode.D3D9Local
            ? "D3D9Ex 本地 surface → D3DImage"
            : "D3D9Ex 共享 + D3D11 Clear + Query → D3DImage";

        if (_mode == RunMode.FlushOnly)
        {
            ExpectedColorBlock.Text = "⚠ Flush-only is not a correctness guarantee";
        }

        Directory.CreateDirectory(ArtifactsDir);
        Status("初始化...");

        try
        {
            // ── 1. D3D9Ex 设备（所有模式通用）────────────────────────
            InitD3D9();

            // ── 2. 根据模式初始化 ────────────────────────────────────
            if (_mode == RunMode.D3D9Local)
                InitM3();
            else
                InitM4();

            // ── 3. D3DImage ──────────────────────────────────────────
            Status("[3/4] 设置 D3DImage...");
            _d3dImage = new D3DImage();
            _d3dImage.IsFrontBufferAvailableChanged += OnFrontBufferChanged;
            SetBackBufferSafe();
            D3DImageHost.Source = _d3dImage;
            int tier = (RenderCapability.Tier >> 16);
            Log($"  WPF rendering tier: {tier}");
            Status("[3/4] D3DImage 设置 ✓");

            // ── 4. 启动定时器 ───────────────────────────────────────
            Status("[4/4] 启动颜色序列...");
            _timer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Render,
                OnTimerTick, Dispatcher);
            _currentIndex = 0;
            ShowCurrentColor();
            Status($"运行中 ({_currentIndex + 1}/{TotalColors})");
        }
        catch (Exception ex)
        {
            Log($"初始化异常: {ex.GetType().Name}: {ex.Message}");
            Log($"  HResult=0x{ex.HResult:X8}");
            Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    unsafe void InitD3D9()
    {
        Status("[1/4] 创建 D3D9Ex 设备...");
        HRESULT hr = PInvoke.Direct3DCreate9Ex(32, out _d3d9);
        Log($"Direct3DCreate9Ex hr=0x{(int)hr:X8}");
        if (hr.Failed) { Fail("Direct3DCreate9Ex 失败"); return; }

        LogAdapterInfo(0);

        var pp = new D3DPRESENT_PARAMETERS
        {
            Windowed = true,
            SwapEffect = D3DSWAPEFFECT.D3DSWAPEFFECT_DISCARD,
            BackBufferWidth = 1, BackBufferHeight = 1,
            BackBufferFormat = D3DFORMAT.D3DFMT_UNKNOWN,
        };
        _d3d9.CreateDeviceEx(0, D3DDEVTYPE.D3DDEVTYPE_HAL, default,
            (uint)(0x40 | 0x02 | 0x04), &pp, null, out _dev9);
        Log("  CreateDeviceEx OK");
        Status("[1/4] D3D9Ex 设备 ✓");
    }

    // ─── M3: 纯 D3D9Ex ──────────────────────────────────────────────────
    unsafe void InitM3()
    {
        Status("[2/4] 创建 D3D9 纹理 (M3, 本地)...");
        _dev9!.CreateTexture(TexW, TexH, 1, 0x01u, // RENDERTARGET
            D3DFORMAT.D3DFMT_A8R8G8B8, D3DPOOL.D3DPOOL_DEFAULT,
            out _tex9, null);
        Log("  CreateTexture OK");
        _tex9.GetSurfaceLevel(0, out _surf9);
        Log("  GetSurfaceLevel(0) OK");
        LogSurfaceDesc(_surf9);
        Status("[2/4] D3D9 纹理 ✓");
    }

    // ─── M4: D3D9Ex 共享 + D3D11 ────────────────────────────────────────
    unsafe void InitM4()
    {
        // ── 2. D3D9 共享纹理 ─────────────────────────────────────────
        Status("[2/4] 创建 D3D9 共享纹理 (M4)...");
        HANDLE h9 = default;
        _dev9!.CreateTexture(TexW, TexH, 1, 0x01u, // RENDERTARGET
            D3DFORMAT.D3DFMT_A8R8G8B8, D3DPOOL.D3DPOOL_DEFAULT,
            out _tex9, &h9);
        _sharedHandle = h9;
        Log($"  CreateTexture(shared) OK, handle=0x{new IntPtr(_sharedHandle.Value).ToInt64():X}");
        if (_sharedHandle.Value == null) { Fail("共享 handle 为空"); return; }

        _tex9.GetSurfaceLevel(0, out _surf9);
        Log("  GetSurfaceLevel(0) OK");
        LogSurfaceDesc(_surf9);
        Status("[2/4] D3D9 共享纹理 ✓");

        // ── 3. D3D11 设备 + OpenSharedResource ──────────────────────
        Status("[3/4] D3D11 打开共享纹理...");
        HRESULT hr = PInvoke.D3D11CreateDevice(
            null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, default,
            default(D3D11_CREATE_DEVICE_FLAG),
            ReadOnlySpan<D3D_FEATURE_LEVEL>.Empty, 7,
            out _dev11, out _, out _ctx11);
        Log($"  D3D11CreateDevice hr=0x{(int)hr:X8}");
        if (hr.Failed) { Fail("D3D11CreateDevice 失败"); return; }

        // LUID 匹配
        if (!CheckLuidMatch())
        {
            Fail("D3D9 与 D3D11 LUID 不匹配，BLOCKED");
            return;
        }

        Guid iid = typeof(ID3D11Texture2D).GUID;
        void* pTex = null;
        _dev11.OpenSharedResource(_sharedHandle, &iid, &pTex);
        if (pTex == null) { Fail("OpenSharedResource 返回空"); return; }
        _tex11 = (ID3D11Texture2D)Marshal.GetTypedObjectForIUnknown(
            new IntPtr(pTex), typeof(ID3D11Texture2D));
        Log("  OpenSharedResource -> D3D11 纹理 ✓");

        LogTextureDesc(_tex11);

        // ── RTV + Query ─────────────────────────────────────────────
        var rtvDesc = new D3D11_RENDER_TARGET_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            ViewDimension = D3D11_RTV_DIMENSION.D3D11_RTV_DIMENSION_TEXTURE2D,
            Anonymous = { Texture2D = new D3D11_TEX2D_RTV { MipSlice = 0 } },
        };
        ID3D11RenderTargetView_unmanaged* pRtv = null;
        _dev11.CreateRenderTargetView(_tex11, &rtvDesc, &pRtv);
        _rtv11 = (ID3D11RenderTargetView)Marshal.GetTypedObjectForIUnknown(
            new IntPtr(pRtv), typeof(ID3D11RenderTargetView));
        Log("  CreateRenderTargetView OK");

        if (_mode != RunMode.FlushOnly)
        {
            var qDesc = new D3D11_QUERY_DESC
            {
                Query = D3D11_QUERY.D3D11_QUERY_EVENT,
                MiscFlags = 0,
            };
            ID3D11Query_unmanaged* pQuery = null;
            _dev11.CreateQuery(&qDesc, &pQuery);
            _query11 = (ID3D11Query)Marshal.GetTypedObjectForIUnknown(
                new IntPtr(pQuery), typeof(ID3D11Query));
            Log("  CreateQuery(D3D11_QUERY_EVENT) OK");
        }

        // ── D3D9 回读 staging ──────────────────────────────────────
        _dev9.CreateOffscreenPlainSurface((uint)TexW, (uint)TexH,
            D3DFORMAT.D3DFMT_A8R8G8B8, D3DPOOL.D3DPOOL_SYSTEMMEM,
            out _staging9, null);
        Log("  CreateOffscreenPlainSurface (staging) OK");

        Status("[3/4] D3D11 共享纹理 + RTV + Query ✓");
    }

    // ══════════════════════════════════════════════════════════════════════
    // LUID 匹配
    // ══════════════════════════════════════════════════════════════════════
    unsafe bool CheckLuidMatch()
    {
        Log("--- LUID 匹配检查 ---");

        // D3D9 LUID. GetAdapterLUID 返回 void，失败抛异常
        LUID luid9 = default;
        try
        {
            _d3d9!.GetAdapterLUID(0, &luid9);
            Log($"  D3D9 GetAdapterLUID(0) OK, LUID=0x{luid9.LowPart:X8}:0x{luid9.HighPart:X8}");
        }
        catch (Exception ex)
        {
            Log($"  GetAdapterLUID 异常: {ex.Message}");
            return false;
        }

        // D3D11 LUID (通过 DXGI)
        IntPtr devUnk = Marshal.GetIUnknownForObject(_dev11!);
        Guid iidDxgiDev = typeof(IDXGIDevice).GUID;
        int qi = Marshal.QueryInterface(devUnk, ref iidDxgiDev, out IntPtr dxgiDevPtr);
        Marshal.Release(devUnk);
        if (qi < 0) { Log("  QI IDXGIDevice 失败"); return false; }

        var dxgiDev = (IDXGIDevice)Marshal.GetTypedObjectForIUnknown(dxgiDevPtr, typeof(IDXGIDevice));
        try
        {
            // GetAdapter → IDXGIAdapter → QI IDXGIAdapter1 → GetDesc1
            IDXGIAdapter adapter;
            dxgiDev.GetAdapter(out adapter);
            Log("  GetAdapter OK");

            // QI for IDXGIAdapter1 for GetDesc1
            Guid iidAd1 = typeof(IDXGIAdapter1).GUID;
            IntPtr adUnk = Marshal.GetIUnknownForObject(adapter);
            int qiAd1 = Marshal.QueryInterface(adUnk, ref iidAd1, out IntPtr ad1Ptr);
            Marshal.Release(adUnk);
            if (qiAd1 < 0) { Log("  QI IDXGIAdapter1 失败"); return false; }

            var adapter1 = (IDXGIAdapter1)Marshal.GetTypedObjectForIUnknown(
                ad1Ptr, typeof(IDXGIAdapter1));

            DXGI_ADAPTER_DESC1 desc1 = adapter1.GetDesc1();
            Log($"  D3D11 AdapterLUID=0x{desc1.AdapterLuid.LowPart:X8}:0x{desc1.AdapterLuid.HighPart:X8}");

            bool match = luid9.LowPart == desc1.AdapterLuid.LowPart
                      && luid9.HighPart == desc1.AdapterLuid.HighPart;
            Log($"  LUID 匹配: {(match ? "✓" : "✗ 不匹配 — BLOCKED")}");
            return match;
        }
        finally
        {
            Marshal.Release(dxgiDevPtr);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // 颜色切换
    // ══════════════════════════════════════════════════════════════════════
    void OnTimerTick(object? sender, EventArgs e)
    {
        if (_allCompleted || _d3dImage == null || _surf9 == null)
            return;

        _currentIndex++;

        if (_currentIndex >= TotalColors)
        {
            _allCompleted = true;
            _timer?.Stop();
            ExpectedColorBlock.Text = "✓ 所有 12 次颜色已完成 — 按 Esc 关闭";
            CountBlock.Text = $"第 {TotalColors}/{TotalColors} 次 ✓";
            Status("完成 ✓");
            Log("12 次颜色切换全部完成");
            return;
        }

        ShowCurrentColor();
    }

    unsafe void ShowCurrentColor()
    {
        var (name, d3dColor, r, g, b) = ColorSequence[_currentIndex];
        Log($"--- 第 {_currentIndex + 1}/{TotalColors} 次: {name} ---");

        ExpectedColorBlock.Text = $"期望颜色：{name}";
        CountBlock.Text = $"第 {_currentIndex + 1}/{TotalColors} 次";

        try
        {
            _d3dImage!.Lock();

            if (_mode == RunMode.D3D9Local)
            {
                // M3: D3D9 ColorFill
                var rect = new RECT { left = 0, top = 0, right = TexW, bottom = TexH };
                _dev9!.ColorFill(_surf9!, &rect, d3dColor);
                Log($"  ColorFill OK (0x{d3dColor:X8})");
            }
            else
            {
                // M4: D3D11 ClearRenderTargetView
                float[] rgba = { r, g, b, 1.0f };
                _ctx11!.ClearRenderTargetView(_rtv11!, rgba);
                Log($"  ClearRenderTargetView OK ({r:F2},{g:F2},{b:F2})");

                // Query 等待 GPU 完成
                if (_query11 != null)
                {
                    _ctx11!.End(_query11);
                    // GetData 阻塞模式（flag=0）等待 GPU 完成
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        _ctx11.GetData(_query11, null, 0, 0);
                        sw.Stop();
                        Log($"  Query GetData OK, {sw.ElapsedMilliseconds}ms");
                    }
                    catch (Exception exGet)
                    {
                        sw.Stop();
                        Log($"  Query GetData 异常: {exGet.Message}, {sw.ElapsedMilliseconds}ms");
                    }
                }

                // Flush
                _ctx11!.Flush();
                Log("  Flush OK");

                // D3D9 staging 回读校验
                if (_staging9 != null)
                {
                    ReadbackPixel((byte)(b * 255), (byte)(g * 255), (byte)(r * 255), 255);
                }
            }

            // SetBackBuffer (首次或 front buffer 恢复时已在外部处理)
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9,
                Marshal.GetIUnknownForObject(_surf9!));
            _d3dImage.AddDirtyRect(new Int32Rect(0, 0, TexW, TexH));

            _completedCount++;
            Log($"  D3DImage updated ({_completedCount}/{TotalColors})");
        }
        catch (Exception ex)
        {
            Log($"  ShowColor 异常: {ex.GetType().Name}: {ex.Message}");
            Log($"  HResult=0x{ex.HResult:X8}");
        }
        finally
        {
            try { _d3dImage?.Unlock(); } catch { }
        }
    }

    // ─── D3D9 staging 回读 ──────────────────────────────────────────────
    unsafe void ReadbackPixel(byte expB, byte expG, byte expR, byte expA)
    {
        try
        {
            _dev9!.GetRenderTargetData(_surf9!, _staging9!);
            D3DLOCKED_RECT lr;
            _staging9!.LockRect(&lr, null, 0);

            byte* p = (byte*)lr.pBits;
            // 取样中心像素
            int cx = TexW / 2, cy = TexH / 2;
            byte b = p[cy * lr.Pitch + cx * 4 + 0];
            byte g = p[cy * lr.Pitch + cx * 4 + 1];
            byte r = p[cy * lr.Pitch + cx * 4 + 2];
            byte a = p[cy * lr.Pitch + cx * 4 + 3];

            bool ok = (b == expB && g == expG && r == expR && a == expA);
            Log($"  Readback center({cx},{cy}): B={b:X2} G={g:X2} R={r:X2} A={a:X2} " +
                $"(expect B={expB:X2} G={expG:X2} R={expR:X2} A={expA:X2}) {(ok ? "✓" : "✗")}");

            _staging9.UnlockRect();
        }
        catch (Exception ex)
        {
            Log($"  Readback 异常: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Front buffer 恢复
    // ══════════════════════════════════════════════════════════════════════
    void OnFrontBufferChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        Log($"IsFrontBufferAvailableChanged: old={e.OldValue}, new={e.NewValue}");
        if (_d3dImage != null && _d3dImage.IsFrontBufferAvailable)
            Dispatcher.BeginInvoke(() => SetBackBufferSafe());
    }

    unsafe void SetBackBufferSafe()
    {
        if (_d3dImage == null || _surf9 == null) return;
        try
        {
            _d3dImage.Lock();
            IntPtr surfPtr = Marshal.GetIUnknownForObject(_surf9);
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surfPtr);
            _d3dImage.AddDirtyRect(new Int32Rect(0, 0, TexW, TexH));
            _d3dImage.Unlock();
            Marshal.Release(surfPtr);
            _d3dImageReady = true;
            Log("  SetBackBuffer (初始/恢复) OK");
        }
        catch (Exception ex)
        {
            Log($"  SetBackBuffer 失败: {ex.Message}");
        }
    }

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
        _timer?.Stop();

        if (!_allCompleted && _currentIndex >= 0)
        {
            _earlyClose = true;
            Log($"提前关闭 (已完成 {_completedCount}/{TotalColors})");
        }

        WriteResults();
        Cleanup();
    }

    unsafe void Cleanup()
    {
        if (_d3dImage != null)
        {
            _d3dImage.IsFrontBufferAvailableChanged -= OnFrontBufferChanged;
            if (_d3dImage.IsFrontBufferAvailable)
            {
                try { _d3dImage.Lock(); _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero); _d3dImage.Unlock(); } catch { }
            }
        }

        // M4: D3D11 逆序
        if (_query11 is IDisposable dq) dq.Dispose();
        if (_rtv11 is IDisposable drtv) drtv.Dispose();
        if (_tex11 is IDisposable dt11) dt11.Dispose();
        if (_ctx11 is IDisposable dc11) dc11.Dispose();
        if (_dev11 is IDisposable dd11) dd11.Dispose();

        // D3D9 逆序
        if (_staging9 is IDisposable dst) dst.Dispose();
        if (_surf9 is IDisposable ds9) ds9.Dispose();
        if (_tex9 is IDisposable dt9) dt9.Dispose();
        if (_dev9 is IDisposable dd9) dd9.Dispose();
        if (_d3d9 is IDisposable dd39) dd39.Dispose();

        if (_sharedHandle.Value != null)
            CloseHandle(new IntPtr(_sharedHandle.Value));

        Log("  资源清理完成");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    // ══════════════════════════════════════════════════════════════════════
    // 辅助
    // ══════════════════════════════════════════════════════════════════════
    unsafe void LogAdapterInfo(uint ordinal)
    {
        try
        {
            D3DADAPTER_IDENTIFIER9 id;
            _d3d9!.GetAdapterIdentifier(ordinal, 0, &id);
            var name = System.Text.Encoding.ASCII.GetString(
                new ReadOnlySpan<byte>(
                    (byte*)Unsafe.AsPointer(ref Unsafe.AsRef(in id.Driver)), 512)).TrimEnd('\0');
            Log($"  Adapter[{ordinal}]: {name}");
            Log($"  DriverVersion: 0x{id.DriverVersion:X}");
            Log($"  DeviceId: 0x{id.DeviceId:X}, SubSysId: 0x{id.SubSysId:X}, Rev: 0x{id.Revision:X}");
        }
        catch (Exception ex) { Log($"  Adapter info 失败: {ex.Message}"); }
    }

    unsafe void LogSurfaceDesc(IDirect3DSurface9? surf)
    {
        if (surf == null) return;
        D3DSURFACE_DESC desc;
        surf.GetDesc(&desc);
        Log($"  Surface: {desc.Width}x{desc.Height}, fmt={desc.Format}, pool={desc.Pool}");
    }

    unsafe void LogTextureDesc(ID3D11Texture2D? tex)
    {
        if (tex == null) return;
        D3D11_TEXTURE2D_DESC desc;
        tex.GetDesc(&desc);
        Log($"  D3D11 Texture: {desc.Width}x{desc.Height}, fmt={desc.Format}, " +
            $"bind=0x{(uint)desc.BindFlags:X}, misc=0x{(uint)desc.MiscFlags:X}");
    }

    void Fail(string reason)
    {
        Log($"失败: {reason}");
        StatusBlock.Text = $"失败: {reason}";
        StatusBlock.Foreground = Brushes.Red;
    }

    unsafe void WriteResults()
    {
        var logText = string.Join(Environment.NewLine, _log);
        File.WriteAllText(LogPath, logText);
        Console.WriteLine($"日志已写入: {LogPath}");

        string verdict;
        string? errorMsg = null;
        if (_mode == RunMode.FlushOnly)
        {
            verdict = "INCONCLUSIVE";
            errorMsg = "flush-only-observation 模式不参与 PASS 判定";
        }
        else if (_earlyClose)
        {
            verdict = "INCONCLUSIVE";
            errorMsg = $"提前关闭，仅完成 {_completedCount}/{TotalColors} 次";
        }
        else if (_allCompleted)
        {
            verdict = "PASS";
        }
        else if (_currentIndex < 0)
        {
            verdict = "FAIL";
            errorMsg = "初始化失败";
        }
        else
        {
            verdict = "FAIL";
            errorMsg = $"未完成全部 12 次 ({_completedCount}/{TotalColors})";
        }

        var result = new
        {
            Stage = _mode == RunMode.D3D9Local ? "M3" : "M4",
            Mode = _mode.ToString(),
            Status = verdict,
            ErrorMessage = errorMsg,
            ColorsCompleted = _completedCount,
            TotalColors,
            AllCompleted = _allCompleted,
            EarlyClose = _earlyClose,
            D3D9DeviceCreated = _dev9 != null,
            TextureCreated = _tex9 != null,
            SurfaceCreated = _surf9 != null,
            D3DImageSet = _d3dImageReady,
            // M4 only
            SharedHandleValid = _sharedHandle.Value != null,
            D3D11DeviceCreated = _dev11 != null,
            OpenSharedResourceOk = _tex11 != null,
            RtvCreated = _rtv11 != null,
            QueryCreated = _query11 != null,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(result,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(JsonPath, json);
        Console.WriteLine($"结果已写入: {JsonPath}");
    }
}