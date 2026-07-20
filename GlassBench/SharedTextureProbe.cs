using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Direct3D9;
using Windows.Win32.Graphics.Dxgi.Common;

namespace GlassBench;

/// <summary>
/// D3D11 <-> D3D9Ex 共享纹理闭环（P0 spike 收尾，方向已纠正）。
///
/// 关键纠正（2026-07-14 实测）：
///   - D3D9（IDirect3DDevice9Ex）**没有** OpenSharedResource（CsWin32/reflection 确认 base 与 Ex 均无；
///     原 memory 称 vtable[119]=OpenSharedResource 是误读——vtable[119] 实为 SetConvolutionMonoKernel，
///     空/legacy/NT handle 都只返回 D3DERR_INVALIDCALL）。
///   - D3D11（ID3D11Device）**有** OpenSharedResource（legacy HANDLE）+ OpenSharedResource1（NTHANDLE）。
///   - CreateSharedHandle 要求 NTHANDLE；GetSharedHandle 给 legacy。但 D3D9 既无 OpenSharedResource，
///     "D3D11 建 -> D3D9 开" 这条路根本走不通。
///
/// 正确方向（WPF D3DImage + D3D11 经典架构）：**D3D9 建共享纹理，D3D11 开**：
///   1. D3D9Ex CreateTexture(RENDERTARGET, DEFAULT, pSharedHandle OUT) -> tex9 + legacy 共享 handle
///   2. D3D11 ID3D11Device.OpenSharedResource(handle, IID_ID3D11Texture2D) -> tex11（与 tex9 共享显存）
///   3. D3D11 UpdateSubresource 写洋红（D3D11 GPU 写）+ Flush
///   4. D3D9 tex9.GetSurfaceLevel(0) -> surf9
///   5. D3D9 GetRenderTargetData -> SYSTEMMEM staging -> LockRect 校验像素 == 洋红（D3D9 读到 D3D11 的写）
///
/// 注：此 legacy 共享路径**无 KeyedMutex**（D3D9 CreateTexture 不产 KEYEDMUTEX 纹理，D3D9 侧也无 keyed mutex API）。
///    真实管线靠 D3D11 Flush + D3DImage.Lock/Unlock 同步。这是 WPF D3DImage+D3D11 的既有做法。
/// </summary>
internal static class SharedTextureProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    public static unsafe void Run()
    {
        Console.WriteLine("=== D3D9 建 -> D3D11 开 共享纹理闭环（方向纠正版）===");
        const int W = 256, H = 256;
        // 洋红 B8G8R8A8 / D3D9 A8R8G8B8：内存 B,G,R,A = FF,00,FF,FF
        const byte B = 0xFF, G = 0x00, R = 0xFF, A = 0xFF;

        IDirect3D9Ex? d3d9 = null;
        IDirect3DDevice9Ex? dev9 = null;
        IDirect3DTexture9? tex9 = null;
        IDirect3DSurface9? sharedSurf = null;
        IDirect3DSurface9? staging = null;
        ID3D11Device? dev11 = null;
        ID3D11DeviceContext? ctx11 = null;
        ID3D11Texture2D? tex11 = null;
        HANDLE h9 = default;

        try
        {
            // ── 1. D3D9Ex device ──
            PInvoke.Direct3DCreate9Ex(32, out d3d9);
            var pp = new D3DPRESENT_PARAMETERS
            {
                Windowed = true,
                SwapEffect = D3DSWAPEFFECT.D3DSWAPEFFECT_DISCARD,
                BackBufferWidth = 1, BackBufferHeight = 1,
                BackBufferFormat = D3DFORMAT.D3DFMT_UNKNOWN,
            };
            d3d9.CreateDeviceEx(0, D3DDEVTYPE.D3DDEVTYPE_HAL, default, (uint)(0x40 | 0x02 | 0x04), &pp, null, out dev9);
            Console.WriteLine("  [1] D3D9Ex device OK");

            // ── 2. D3D9 建共享纹理（RENDERTARGET, DEFAULT, pSharedHandle OUT）──
            // D3DUSAGE_RENDERTARGET=0x01；pSharedHandle 传指向 NULL 的指针，CreateTexture 写出 legacy 共享 handle
            h9 = default;
            dev9.CreateTexture((uint)W, (uint)H, 1, 0x01u, D3DFORMAT.D3DFMT_A8R8G8B8, D3DPOOL.D3DPOOL_DEFAULT, out tex9, &h9);
            Console.WriteLine($"  [2] D3D9 共享纹理 OK, handle=0x{new IntPtr(h9.Value).ToInt64():X} ({(h9.Value != null ? "非空" : "空")})");
            if (h9.Value == null) { Console.WriteLine("  ✗ 未取得共享 handle（CreateTexture 未建共享纹理），终止"); return; }

            // ── 3. D3D11 device + context ──
            PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, default,
                default(D3D11_CREATE_DEVICE_FLAG), ReadOnlySpan<D3D_FEATURE_LEVEL>.Empty, 7,
                out dev11, out _, out ctx11);
            Console.WriteLine("  [3] D3D11 device + context OK");

            // ── 4. D3D11 OpenSharedResource 开 D3D9 的共享纹理（关键）──
            Guid iid = typeof(ID3D11Texture2D).GUID;
            void* pTex = null;
            dev11.OpenSharedResource(h9, &iid, &pTex);
            if (pTex == null) { Console.WriteLine("  ✗ D3D11 OpenSharedResource 返回空"); return; }
            tex11 = (ID3D11Texture2D)Marshal.GetTypedObjectForIUnknown(new IntPtr(pTex), typeof(ID3D11Texture2D));
            Console.WriteLine("  [4] D3D11 OpenSharedResource OK -> tex11（与 tex9 共享显存）✓");

            // ── 5. D3D11 写洋红（UpdateSubresource，CPU->GPU 纹理）+ Flush ──
            byte[] pixels = new byte[W * H * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = B; pixels[i + 1] = G; pixels[i + 2] = R; pixels[i + 3] = A; }
            fixed (byte* pPix = pixels)
            {
                ctx11.UpdateSubresource(tex11, 0u, null, pPix, (uint)(W * 4), (uint)(W * H * 4));
            }
            ctx11.Flush();
            // 无 KeyedMutex，靠 Flush + 等待让 D3D11 写对 D3D9 可见
            System.Threading.Thread.Sleep(120);
            Console.WriteLine("  [5] D3D11 UpdateSubresource(洋红) + Flush OK");

            // ── 6. D3D9 取 surface + 回读校验 ──
            tex9.GetSurfaceLevel(0, out sharedSurf);
            Console.WriteLine("  [6] D3D9 GetSurfaceLevel(0) OK");
            dev9.CreateOffscreenPlainSurface((uint)W, (uint)H, D3DFORMAT.D3DFMT_A8R8G8B8, D3DPOOL.D3DPOOL_SYSTEMMEM, out staging, null);
            bool gotData = false;
            try { dev9.GetRenderTargetData(sharedSurf, staging); gotData = true; }
            catch (Exception ex) { Console.WriteLine($"     GetRenderTargetData 失败: {ex.Message}"); }

            bool colorOk = false; int matched = 0, checkedN = 0; int pitch = 0;
            if (gotData)
            {
                D3DLOCKED_RECT lr;
                staging.LockRect(&lr, null, 0);
                pitch = lr.Pitch;
                byte* p = (byte*)lr.pBits;
                byte b0 = p[0], b1 = p[1], b2 = p[2], b3 = p[3];
                Console.WriteLine($"     回读首像素 = {b0:X2} {b1:X2} {b2:X2} {b3:X2}（期望 FF 00 FF FF）");
                for (int i = 0; i < H; i++)
                    for (int j = 0; j < W; j++)
                    {
                        byte* px = p + i * lr.Pitch + j * 4;
                        checkedN++;
                        if (px[0] == B && px[1] == G && px[2] == R && px[3] == A) matched++;
                    }
                staging.UnlockRect();
                colorOk = (matched == checkedN);
            }

            Console.WriteLine();
            Console.WriteLine("  ── 结论 ──");
            Console.WriteLine($"  D3D9 建共享纹理 + 取 handle : {(h9.Value != null ? "✓" : "✗")}");
            Console.WriteLine($"  D3D11 OpenSharedResource     : {(tex11 != null ? "✓ 成功（方向纠正验证通过）" : "✗")}");
            Console.WriteLine($"  D3D11 写 -> D3D9 读 内容校验  : {(colorOk ? $"✓ {matched}/{checkedN} 像素=洋红 (Pitch={pitch})" : (gotData ? "✗ 回读成功但颜色不符" : "✗ 回读失败"))}");
            Console.WriteLine($"  => D3D9建->D3D11开 共享闭环 {(tex11 != null && colorOk ? "✓ 通" : (tex11 != null ? "△ OpenSharedResource 通，内容校验未过" : "✗ 未通"))}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  异常: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            // spike 一次性运行，COM 对象随进程退出回收
            if (h9.Value != null) CloseHandle(new IntPtr(h9.Value));
        }
    }
}
