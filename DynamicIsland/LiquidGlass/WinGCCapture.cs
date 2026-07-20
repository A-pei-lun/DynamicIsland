using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using WinRT;

namespace DynamicIsland.LiquidGlass
{
    /// <summary>
    /// Windows.Graphics.Capture 抓屏（默认后端）。
    ///
    /// 管线（参考 GlassBench WinGcFrameProbe，已验证跑通）：
    ///   外部 D3D11 device -> IDXGIDevice -> CreateDirect3D11DeviceFromDXGIDevice(d3d11.dll) -> WinRT IDirect3DDevice
    ///   -> IGraphicsCaptureItemInterop.CreateForMonitor(hmon) -> GraphicsCaptureItem
    ///   -> Direct3D11CaptureFramePool.CreateFreeThreaded(B8G8R8A8, 2 槽, item.Size)
    ///   -> FrameArrived(ThreadPool) -> frame.Surface.As&lt;IDirect3DDxgiInterfaceAccess&gt;().GetInterface(ID3D11Texture2D)
    ///
    /// 关键：复用外部 D3D11 设备（与模糊/共享纹理同设备）。WinGC 抓屏写与模糊读走同一 D3D11 命令队列，
    ///       天然串行，无需跨设备 keyed mutex 同步，帧纹理也可在同设备直接读取（WinGC 帧纹理默认非共享，
    ///       跨设备读不到）。
    ///
    /// 帧率：WinGC API 限 60fps（GlassBench 实测 56fps≈60Hz，240Hz 屏也只给 60fps）。方案一接受。
    /// </summary>
    internal sealed class WinGCCapture : IDesktopCapture
    {
        // ── P/Invoke: WinRT 桥（d3d11.dll 把 IDXGIDevice 包成 WinRT IDirect3DDevice）──
        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        // ── COM interop: 从 WinRT IDirect3DSurface 取底层 ID3D11Texture2D ──
        [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            [PreserveSig] int GetInterface(ref Guid iid, out IntPtr ppv);
        }

        // ── COM interop: GraphicsCaptureItem.CreateForMonitor（SDK.NET 投影无此方法，绕过）──
        [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
            [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
        }

        [DllImport("combase.dll")]
        private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
        [DllImport("combase.dll")]
        private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, uint length, out IntPtr hstring);
        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);

        private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        private readonly ID3D11Device _dev11;
        private IDirect3DDevice? _d3dDev;
        private GraphicsCaptureItem? _item;
        private Direct3D11CaptureFramePool? _pool;
        private GraphicsCaptureSession? _session;

        public bool IsRunning { get; private set; }
        public int WinGcFrameCount { get; private set; }   // 诊断：WinGC 回调到达次数
        public int WinGcExtractFailCount { get; private set; } // 诊断：纹理提取失败次数
        public int WinGcCastFail { get; private set; }     // 诊断：surface.As<> 失败
        public int WinGcHrFail { get; private set; }       // 诊断：GetInterface hr!=0
        public int WinGcLastHr { get; private set; }       // 诊断：最后一次失败的 HRESULT
        public int WinGcNativeQiHr { get; private set; }   // 分支 1A：原生 ABI 指针 QI 的 HRESULT
        public string? WinGcSurfaceType { get; private set; } // 分支 1A：surface 的实际 CLR 类型
        public string Name => $"WinGC 抓屏 (cb={WinGcFrameCount}, fail={WinGcExtractFailCount}, cast={WinGcCastFail}, hr={WinGcHrFail}, lastHr={WinGcLastHr}, nativeQiHr={WinGcNativeQiHr}, surfType={WinGcSurfaceType ?? "?"})";

        public event Action<ID3D11Texture2D, uint, uint>? FrameArrived;

        public WinGCCapture(ID3D11Device dev11) => _dev11 = dev11;

        public void Start(IntPtr hmonitor)
        {
            if (IsRunning) return;
            _d3dDev = CreateWinRTDevice(_dev11);
            _item = CreateCaptureItem(hmonitor);

            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _d3dDev, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
            _pool.FrameArrived += OnFrameArrived;
            // TODO: 监器分辨率变更（非切换）的 resize 处理--监 frame.ContentSize 与 _poolSize 不符时
            //       Recreate 池。monitor 切换由 MainWindow 重启 glass 覆盖，此处暂略。

            _session = _pool.CreateCaptureSession(_item);
            // 禁用 WinGC 捕获指示（屏幕周围金色边框）与光标捕获。Win11 22000+ 支持；旧版本 setter 抛异常则忽略。
            try { _session.IsBorderRequired = false; } catch { }
            try { _session.IsCursorCaptureEnabled = false; } catch { }
            _session.StartCapture();
            IsRunning = true;
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object _)
        {
            WinGcFrameCount++;
            if (!IsRunning) return;
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;

            var surface = frame.Surface;
            try
            {
                // 正确路径（计划 V2 阶段 1 → 分支 1A）：
                //   frame.Surface
                //     → ((IWinRTObject)surface).NativeObject
                //     → AsInterface<IDirect3DDxgiInterfaceAccess>()
                //     → GetInterface(IID_ID3D11Texture2D)
                //     → ID3D11Texture2D
                //
                // 不再使用 Marshal.GetIUnknownForObject 直接 QueryInterface ID3D11Texture2D。
                var nativeObj = ((IWinRTObject)surface).NativeObject;
                var access = nativeObj.AsInterface<IDirect3DDxgiInterfaceAccess>();
                // 如果 access 为 null 会抛异常，由外层 catch 捕获

                // 显式标准 IID_ID3D11Texture2D = 6F15AAF2-D208-4E89-9AB4-489535D34F9C
                Guid iidTex = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
                int hr = access.GetInterface(ref iidTex, out IntPtr texPtr);
                if (hr == 0 && texPtr != IntPtr.Zero)
                {
                    var tex2 = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(texPtr);
                    Marshal.Release(texPtr); // 平衡 GetInterface 的引用
                    FrameArrived?.Invoke(tex2, (uint)frame.ContentSize.Width, (uint)frame.ContentSize.Height);
                    Marshal.ReleaseComObject(tex2); // 释放 GetObjectForIUnknown 的引用
                }
                else
                {
                    WinGcHrFail++;
                    WinGcExtractFailCount++;
                    WinGcLastHr = hr;
                }
            }
            catch (Exception ex)
            {
                WinGcCastFail++;
                WinGcExtractFailCount++;
                WinGcLastHr = ex.HResult;

                // 分支 1A 步骤 1：记录 surface 实际类型
                if (WinGcSurfaceType == null)
                    WinGcSurfaceType = surface.GetType().FullName;

                // 分支 1A 步骤 2-4：用原生 ABI 指针直接 QueryInterface IDirect3DDxgiInterfaceAccess
                try
                {
                    IntPtr thisPtr = ((IWinRTObject)surface).NativeObject.ThisPtr;
                    Guid iidAccess = typeof(IDirect3DDxgiInterfaceAccess).GUID;
                    WinGcNativeQiHr = Marshal.QueryInterface(thisPtr, ref iidAccess, out IntPtr accessPtr);
                    if (WinGcNativeQiHr == 0 && accessPtr != IntPtr.Zero)
                    {
                        // 原生 QI 成功！说明是 C#/WinRT 包装问题，直接用原生指针调用 GetInterface
                        var nativeAccess = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
                        Guid iidTex = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
                        int hr2 = nativeAccess.GetInterface(ref iidTex, out IntPtr texPtr2);
                        if (hr2 == 0 && texPtr2 != IntPtr.Zero)
                        {
                            var tex2 = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(texPtr2);
                            Marshal.Release(texPtr2);
                            // 重置计数——这次成功了
                            WinGcCastFail--;
                            WinGcExtractFailCount--;
                            WinGcHrFail = 0;
                            WinGcLastHr = 0;
                            FrameArrived?.Invoke(tex2, (uint)frame.ContentSize.Width, (uint)frame.ContentSize.Height);
                            Marshal.ReleaseComObject(tex2);
                        }
                        else
                        {
                            WinGcHrFail++;
                            WinGcLastHr = hr2;
                        }
                        Marshal.Release(accessPtr);
                    }
                    // 如果 native QI 也失败，nativeQiHr 已经记录了失败的 HRESULT
                }
                catch { /* 原生 QI 路径也失败，静默 */ }
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { _session?.Dispose(); } catch { }
            try { _pool?.Dispose(); } catch { }
            _session = null;
            _pool = null;
            _item = null;
            _d3dDev = null;
        }

        public void Dispose() => Stop();

        // ── 构造辅助 ──

        private static IDirect3DDevice CreateWinRTDevice(ID3D11Device dev11)
        {
            // ⚠️ 诊断（2026-07-20）：CreateDirect3D11DeviceFromDXGIDevice 创建的设备产生的 surface
            // 不支持 IDirect3DDxgiInterfaceAccess。使用原始方法。
            IntPtr unk = Marshal.GetIUnknownForObject(dev11);
            IntPtr dxgiPtr = IntPtr.Zero;
            try
            {
                Guid iidDxgi = typeof(IDXGIDevice).GUID;
                Marshal.QueryInterface(unk, in iidDxgi, out dxgiPtr);
                int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out IntPtr inspectablePtr);
                if (hr != 0)
                    throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice 失败 hr=0x{hr:X8}");
                return MarshalInspectable<IDirect3DDevice>.FromAbi(inspectablePtr);
            }
            finally
            {
                if (dxgiPtr != IntPtr.Zero) Marshal.Release(dxgiPtr);
                Marshal.Release(unk);
            }
        }

        private static GraphicsCaptureItem CreateCaptureItem(IntPtr hmonitor)
        {
            const string clsid = "Windows.Graphics.Capture.GraphicsCaptureItem";
            WindowsCreateString(clsid, (uint)clsid.Length, out IntPtr hstr);
            Guid iop = IID_IGraphicsCaptureItemInterop;
            int hr = RoGetActivationFactory(hstr, ref iop, out IntPtr factoryPtr);
            WindowsDeleteString(hstr);
            if (hr != 0)
                throw new InvalidOperationException($"RoGetActivationFactory 失败 hr=0x{hr:X8}");

            IntPtr interopPtr = IntPtr.Zero;
            try
            {
                Marshal.QueryInterface(factoryPtr, in iop, out interopPtr);
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetTypedObjectForIUnknown(
                    interopPtr, typeof(IGraphicsCaptureItemInterop));

                Guid iidItem = IID_IGraphicsCaptureItem;
                int hr2 = interop.CreateForMonitor(hmonitor, ref iidItem, out IntPtr itemPtr);
                if (hr2 != 0 || itemPtr == IntPtr.Zero)
                    throw new InvalidOperationException($"CreateForMonitor 失败 hr=0x{hr2:X8}");
                return GraphicsCaptureItem.FromAbi(itemPtr);
            }
            finally
            {
                if (interopPtr != IntPtr.Zero) Marshal.Release(interopPtr);
                Marshal.Release(factoryPtr);
            }
        }
    }
}
