using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D9;
using Windows.Win32.Graphics.Direct3D11;

namespace DynamicIsland.LiquidGlass
{
    /// <summary>
    /// D3D9/D3D11 共享纹理桥（参考 GlassBench SharedTextureProbe，方向纠正版）。
    ///
    /// 方向（2026-07-14 实测纠正）：D3D9 建共享纹理，D3D11 开。D3D9 无 OpenSharedResource，必须反过来。
    ///   1. D3D9Ex CreateTexture(RENDERTARGET, D3DPOOL_DEFAULT, pSharedHandle OUT) -> tex9 + legacy 共享 handle
    ///   2. D3D11 ID3D11Device.OpenSharedResource(handle, IID_ID3D11Texture2D) -> tex11（与 tex9 共享显存）
    ///   3. D3D11 CreateRenderTargetView(tex11) -> rtv11（模糊输出写到共享纹理）
    ///   4. D3D9 tex9.GetSurfaceLevel(0) -> surf9（D3DImage.SetBackBuffer 用，与 tex11 同一片显存）
    ///
    /// 无 KeyedMutex（D3D9 不产/不开 keyed mutex 纹理）。同步靠 D3D11 Flush + D3DImage.Lock/Unlock（WPF 侧）。
    /// 复用外部 D3D11 设备（与 WinGCCapture/GpuBlur 同设备，命令同队列串行）。
    /// </summary>
    internal sealed class D3D11Interop : IDisposable
    {
        private readonly ID3D11Device _dev11;
        private readonly ID3D11DeviceContext _ctx11;

        private IDirect3D9Ex? _d3d9;
        private IDirect3DDevice9Ex? _dev9;
        private IDirect3DTexture9? _tex9;
        private IDirect3DSurface9? _surf9;

        private ID3D11Texture2D? _tex11;
        private ID3D11RenderTargetView? _rtv11;

        private HANDLE _sharedHandle; // D3D9 CreateTexture 写出的 legacy 共享 handle
        private uint _w, _h;
        private bool _disposed;

        public D3D11Interop(ID3D11Device dev11, ID3D11DeviceContext ctx11)
        {
            _dev11 = dev11;
            _ctx11 = ctx11;
            // D3D9Ex 设备（D3DImage 要求：HARDWARE_VERTEXPROCESSING|FPU_PRESERVE|MULTITHREADED）
            PInvoke.Direct3DCreate9Ex(32, out _d3d9);
        }

        /// <summary>模糊输出目标（共享纹理的 D3D11 RTV）。GpuBlur V-pass 写到这里。</summary>
        public ID3D11RenderTargetView? OutputRtv => _rtv11;

        /// <summary>D3D9 共享纹理 surface（D3DImage.SetBackBuffer 用）。GpuGlassBackend 提取 IntPtr。</summary>
        public IDirect3DSurface9? Surface9 => _surf9;

        public uint Width => _w;
        public uint Height => _h;

        /// <summary>确保共享纹理尺寸（变了则重建）。D3D9 设备懒建（首次 EnsureSize 时）。</summary>
        public unsafe void EnsureSize(uint w, uint h)
        {
            if (w == 0 || h == 0) return;
            if (_dev9 == null) CreateD3D9Device();
            if (w == _w && h == _h && _tex11 != null) return;

            ReleaseShared();

            // 1. D3D9 建共享纹理（RENDERTARGET | DEFAULT，pSharedHandle OUT）
            _sharedHandle = default;
            _dev9!.CreateTexture(w, h, 1, 0x01u, D3DFORMAT.D3DFMT_A8R8G8B8, D3DPOOL.D3DPOOL_DEFAULT,
                out _tex9, ref _sharedHandle);
            if (_sharedHandle.Value == null)
                throw new InvalidOperationException("D3D9 CreateTexture 未写出共享 handle");

            // 2. D3D11 OpenSharedResource 开（关键：方向 D3D9 建 -> D3D11 开）
            Guid iid = typeof(ID3D11Texture2D).GUID;
            void* pTex = null;
            _dev11.OpenSharedResource(_sharedHandle, &iid, &pTex);
            if (pTex == null)
                throw new InvalidOperationException("D3D11 OpenSharedResource 返回空");
            _tex11 = (ID3D11Texture2D)Marshal.GetTypedObjectForIUnknown(new IntPtr(pTex), typeof(ID3D11Texture2D));

            // 3. D3D11 RTV（模糊输出）
            _dev11.CreateRenderTargetView(_tex11, null, out _rtv11);

            // 4. D3D9 surface（D3DImage 用，与 tex11 共享显存）
            _tex9.GetSurfaceLevel(0, out _surf9);

            _w = w; _h = h;
        }

        /// <summary>D3D11 Flush：让 V-pass 写对 D3D9/D3DImage 可见（无 keyed mutex 的同步点）。</summary>
        public void Flush() => _ctx11.Flush();

        private unsafe void CreateD3D9Device()
        {
            var pp = new D3DPRESENT_PARAMETERS
            {
                Windowed = true,
                SwapEffect = D3DSWAPEFFECT.D3DSWAPEFFECT_DISCARD,
                BackBufferWidth = 1, BackBufferHeight = 1,
                BackBufferFormat = D3DFORMAT.D3DFMT_UNKNOWN,
            };
            // 0x40=HARDWARE_VERTEXPROCESSING | 0x02=FPU_PRESERVE | 0x04=MULTITHREADED
            _d3d9!.CreateDeviceEx(0, D3DDEVTYPE.D3DDEVTYPE_HAL, default,
                (uint)(0x40 | 0x02 | 0x04), &pp, null, out _dev9);
        }

        private unsafe void ReleaseShared()
        {
            Rel(_surf9); _surf9 = null;
            Rel(_rtv11); _rtv11 = null;
            Rel(_tex11); _tex11 = null;
            Rel(_tex9); _tex9 = null;
            if (_sharedHandle.Value != null)
            {
                CloseHandle(new IntPtr(_sharedHandle.Value));
                _sharedHandle = default;
            }
            _w = _h = 0;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        private static void Rel(object? o)
        {
            if (o is not null) try { Marshal.ReleaseComObject(o); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseShared();
            Rel(_dev9); _dev9 = null;
            Rel(_d3d9); _d3d9 = null;
        }
    }
}
