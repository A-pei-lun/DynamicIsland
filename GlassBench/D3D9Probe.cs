using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D9;

namespace GlassBench;

/// <summary>
/// 验证手写 D3D9 OpenSharedResource 的 vtable index。
/// 创建 IDirect3DDevice9Ex -> 手写 vtable[119] 调 OpenSharedResource(空 handle)。
/// 调到真方法返回 E_INVALIDARG/ E_POINTER；index 错则 AccessViolation。
/// </summary>
internal static class D3D9Probe
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenSharedResourceDelegate(IntPtr @this, IntPtr hSharedResource, ref Guid riid, out IntPtr ppResource);

    public static unsafe void Run()
    {
        Console.WriteLine("=== D3D9 OpenSharedResource vtable[119] 验证 ===");
        try
        {
            HRESULT hr = PInvoke.Direct3DCreate9Ex(32, out IDirect3D9Ex d3d9);
            Console.WriteLine($"  Direct3DCreate9Ex hr=0x{(int)hr:X8}");
            if (hr.Failed) { Console.WriteLine("  创建 D3D9 失败，终止"); return; }

            D3DPRESENT_PARAMETERS pp = new D3DPRESENT_PARAMETERS
            {
                Windowed = true,
                SwapEffect = D3DSWAPEFFECT.D3DSWAPEFFECT_DISCARD,
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferFormat = D3DFORMAT.D3DFMT_UNKNOWN,
            };
            IDirect3DDevice9Ex dev9;
            // HARDWARE_VERTEXPROCESSING(0x40) | FPU_PRESERVE(0x02) | MULTITHREADED(0x04)
            d3d9.CreateDeviceEx(0, D3DDEVTYPE.D3DDEVTYPE_HAL, default,
                (uint)(0x40 | 0x02 | 0x04),
                &pp, null, out dev9);
            Console.WriteLine($"  CreateDeviceEx OK, dev9={(dev9 != null ? "nonnull" : "null")}");
            if (dev9 is null) { Console.WriteLine("  dev9 为 null，终止"); return; }

            // 手写 vtable[119] = OpenSharedResource?
            IntPtr pUnk = Marshal.GetIUnknownForObject(dev9);
            try
            {
                IntPtr vtbl = Marshal.ReadIntPtr(pUnk);
                IntPtr fn = Marshal.ReadIntPtr(vtbl, 119 * IntPtr.Size);
                Console.WriteLine($"  vtable[119] fn=0x{fn.ToInt64():X}");

                var del = Marshal.GetDelegateForFunctionPointer<OpenSharedResourceDelegate>(fn);
                Guid iid = typeof(IDirect3DSurface9).GUID;
                int hr2 = del(pUnk, IntPtr.Zero, ref iid, out IntPtr surf);
                Console.WriteLine($"  调用(空handle) hr=0x{hr2:X8} surf={(surf == IntPtr.Zero ? "null" : "非空")}");
                if (hr2 == unchecked((int)0x80070057)) Console.WriteLine("  -> E_INVALIDARG: index 119 = OpenSharedResource 确认 OK");
                else if (hr2 == unchecked((int)0x80004003)) Console.WriteLine("  -> E_POINTER: index 119 = OpenSharedResource 确认 OK");
                else if (hr2 == unchecked((int)0x80070006)) Console.WriteLine("  -> E_HANDLE: index 119 = OpenSharedResource 确认 OK");
                else Console.WriteLine($"  -> 其他 hr，需判断是否 OpenSharedResource");
            }
            finally
            {
                Marshal.Release(pUnk);
            }
        }
        catch (AccessViolationException ex)
        {
            Console.WriteLine($"  AccessViolation: {ex.Message}");
            Console.WriteLine("  -> vtable index 错！调到错误函数指针。119 不是 OpenSharedResource。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  异常: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
