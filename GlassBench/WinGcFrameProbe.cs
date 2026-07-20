using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using WinRT;

namespace GlassBench;

internal static class WinGcFrameProbe
{
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    const uint MONITOR_DEFAULTTOPRIMARY = 1;
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr clsid, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll")] static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, uint len, out IntPtr hstr);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstr);
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr w, ref Guid iid, out IntPtr r);
        [PreserveSig] int CreateForMonitor(IntPtr m, ref Guid iid, out IntPtr r);
    }

    static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static unsafe void Run()
    {
        Console.WriteLine("=== WinGC 阶段2: FramePool 帧率 + 阻塞 ===");
        Console.WriteLine("  Direct3D11CaptureFramePool 静态方法:");
        foreach (var m in typeof(Direct3D11CaptureFramePool).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            Console.WriteLine($"    {m.Name}");
        int mainThread = Thread.CurrentThread.ManagedThreadId;
        Console.WriteLine($"  主线程 ID={mainThread}");
        try
        {
            // 1. D3D11 device
            HRESULT hr0 = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, default, default(D3D11_CREATE_DEVICE_FLAG), ReadOnlySpan<D3D_FEATURE_LEVEL>.Empty, 7, out ID3D11Device dev11, out _, out _);
            Console.WriteLine($"  D3D11CreateDevice hr=0x{(int)hr0:X8}");

            // 2. IDXGIDevice -> WinRT IDirect3DDevice
            IntPtr devUnk = Marshal.GetIUnknownForObject(dev11);
            Guid iidDxgi = typeof(IDXGIDevice).GUID;
            Marshal.QueryInterface(devUnk, ref iidDxgi, out IntPtr dxgiPtr);
            Marshal.Release(devUnk);
            int hr1 = CreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out IntPtr inspectablePtr);
            Console.WriteLine($"  CreateDirect3D11DeviceFromDXGIDevice hr=0x{hr1:X8}");
            Marshal.Release(dxgiPtr);
            if (hr1 != 0) { Console.WriteLine("  转 IDirect3DDevice 失败"); return; }
            IDirect3DDevice d3dDev = MarshalInspectable<IDirect3DDevice>.FromAbi(inspectablePtr);
            Console.WriteLine("  IDirect3DDevice OK");

            // 3. GraphicsCaptureItem
            string clsid = "Windows.Graphics.Capture.GraphicsCaptureItem";
            WindowsCreateString(clsid, (uint)clsid.Length, out IntPtr hstr);
            Guid iop = IID_IGraphicsCaptureItemInterop;
            RoGetActivationFactory(hstr, ref iop, out IntPtr factoryPtr);
            WindowsDeleteString(hstr);
            Marshal.QueryInterface(factoryPtr, ref iop, out IntPtr interopPtr);
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetTypedObjectForIUnknown(interopPtr, typeof(IGraphicsCaptureItemInterop));
            IntPtr hmon = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
            Guid iidItem = IID_IGraphicsCaptureItem;
            interop.CreateForMonitor(hmon, ref iidItem, out IntPtr itemPtr);
            GraphicsCaptureItem item = GraphicsCaptureItem.FromAbi(itemPtr);
            Console.WriteLine($"  item Size={item.Size.Width}x{item.Size.Height}");
            Marshal.Release(interopPtr); Marshal.Release(factoryPtr);

            // 4. FramePool + FrameArrived
            var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(d3dDev, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            int count = 0; int cbThread = -1; double maxGet = 0;
            pool.FrameArrived += (s, _) =>
            {
                Interlocked.CompareExchange(ref cbThread, Thread.CurrentThread.ManagedThreadId, -1);
                var t = Stopwatch.StartNew();
                using var f = s.TryGetNextFrame();
                t.Stop();
                double ms = t.Elapsed.TotalMilliseconds;
                double mg = maxGet; if (ms > mg) Interlocked.Exchange(ref maxGet, ms);
                Interlocked.Increment(ref count);
            };
            var session = pool.CreateCaptureSession(item);
            session.StartCapture();
            Console.WriteLine("  capture 启动，采样 5 秒（FreeThreaded ThreadPool）...");
            Thread.Sleep(5000);
            session.Dispose();
            pool.Dispose();

            Console.WriteLine($"  5秒 {count} 帧 = {count / 5.0:F1} fps");
            Console.WriteLine($"  回调线程 ID={cbThread} (主={mainThread}, 同线程={cbThread == mainThread})");
            Console.WriteLine($"  TryGetNextFrame max 耗时={maxGet:F3} ms");
            Console.WriteLine($"  -> {(cbThread != mainThread ? "回调非 UI 线程，不阻塞 UI ✓" : "回调在 UI 线程，会阻塞 UI ✗")}");
        }
        catch (Exception ex) { Console.WriteLine($"  异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
    }
}
