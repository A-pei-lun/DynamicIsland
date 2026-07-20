using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DynamicIsland.Sources
{
    /// <summary>
    /// 用 DXGI 读取主显卡的专用显存总量（字节）。
    ///
    /// 为什么不用 WMI Win32_VideoController.AdapterRAM：那是 uint，超过 4GB 的显存会
    /// 数值回绕（8GB 卡读成 ~4GB），导致占用率分母偏小、显示偏高。DXGI 的
    /// DXGI_ADAPTER_DESC.DedicatedVideoMemory 是 SIZE_T(64 位)，无回绕，与任务管理器一致。
    ///
    /// 实现走纯 P/Invoke + 手工 vtable 偏移调用（与 GpuMonitor 同风格，避免 ComImport
    /// 接口映射在 STA 下拿不到接口的坑）。GetDesc 输出结构按真实布局镜像。
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class VramInfo
    {
        private const string DXGI = "dxgi.dll";

        // CreateDXGIFactory2(flags, IID, ppFactory) — Win8+，最稳；flags=0。返回 HRESULT。
        [DllImport(DXGI)]
        private static extern int CreateDXGIFactory2(uint Flags, ref Guid riid, out IntPtr ppFactory);

        // 旧系统回退
        [DllImport(DXGI)]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

        private static readonly Guid IID_IDXGIFactory1 =
            new("770aae78-f26f-4dba-a829-253c83d1b387");

        private static bool _queried;
        private static long _totalBytes = -1;

        public static long GetTotalBytes()
        {
            if (_queried) return _totalBytes;
            _queried = true;
            try { _totalBytes = QueryViaDxgi(); }
            catch { _totalBytes = -1; }
            return _totalBytes;
        }

        // DXGI_ADAPTER_DESC 布局（x64）：Description[128] wchar = 256 字节，
        // VendorId/DeviceId/SubSysId/Revision 各 uint = 16 字节 → DedicatedVideoMemory 偏移 272。
        // 该偏移在 x86 下也是 272（前面都是定长字段）。总结构大小 528 字节。
        private const int DescSize = 528;
        private const int DedicatedVideoMemoryOffset = 272;

        private static long QueryViaDxgi()
        {
            IntPtr factory = IntPtr.Zero;
            try
            {
                Guid iid = IID_IDXGIFactory1;
                int hr;
                try { hr = CreateDXGIFactory2(0, ref iid, out factory); }
                catch { hr = -1; }
                if (hr < 0 || factory == IntPtr.Zero)
                    hr = CreateDXGIFactory1(ref iid, out factory);
                if (hr < 0 || factory == IntPtr.Zero) return -1;

                // IDXGIFactory1 vtable（IUnknown 占 [0][1][2]）：
                // [3]SetPrivateData [4]SetPrivateDataInterface [5]GetPrivateData [6]GetParent
                // [7]EnumAdapters [8]MakeWindowAssociation [9]GetWindowAssociation
                // [10]CreateSwapChain [11]CreateSoftwareAdapter [12]EnumAdapters1
                IntPtr vtbl = Marshal.ReadIntPtr(factory);
                IntPtr enumAdapters1 = Marshal.ReadIntPtr(vtbl, 12 * IntPtr.Size);

                long best = -1;
                for (uint i = 0; i < 8; i++)
                {
                    int hrEnum = NativeMethods.EnumAdapters1(enumAdapters1, factory, i, out IntPtr adapter);
                    if (hrEnum < 0 || adapter == IntPtr.Zero) break;

                    try
                    {
                        // IDXGIAdapter vtable（IUnknown [0..2] + IDXGIObject [3..6] + Adapter 自身）：
                        // [3]SetPrivateData [4]SetPrivateDataInterface [5]GetPrivateData [6]GetParent
                        // [7]EnumOutputs [8]GetDesc
                        IntPtr adapterVtbl = Marshal.ReadIntPtr(adapter);
                        IntPtr getDesc = Marshal.ReadIntPtr(adapterVtbl, 8 * IntPtr.Size);

                        IntPtr desc = Marshal.AllocHGlobal(DescSize);
                        try
                        {
                            if (NativeMethods.GetDesc(getDesc, adapter, desc) >= 0)
                            {
                                long vram = Marshal.ReadInt64(desc, DedicatedVideoMemoryOffset);
                                if (vram > best) best = vram;
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(desc);
                        }
                    }
                    finally
                    {
                        // IUnknown::Release = vtable[2]
                        IntPtr release = Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 2 * IntPtr.Size);
                        _ = NativeMethods.Release(release, adapter);
                    }
                }
                return best;
            }
            finally
            {
                if (factory != IntPtr.Zero)
                {
                    IntPtr release = Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 2 * IntPtr.Size);
                    _ = NativeMethods.Release(release, factory);
                }
            }
        }

        /// <summary>通过委托调用非托管 vtable 函数指针，避免 unsafe。</summary>
        private static class NativeMethods
        {
            // IDXGIFactory1::EnumAdapters1(this, UINT Adapter, IDXGIAdapter** ppAdapter)
            public delegate int EnumAdapters1Delegate(IntPtr factory, uint adapter, out IntPtr ppAdapter);
            // IDXGIAdapter::GetDesc(this, DXGI_ADAPTER_DESC* pDesc)
            public delegate int GetDescDelegate(IntPtr adapter, IntPtr pDesc);
            // IUnknown::Release(this)
            public delegate int ReleaseDelegate(IntPtr obj);

            public static int EnumAdapters1(IntPtr fn, IntPtr factory, uint adapter, out IntPtr ppAdapter)
                => Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(fn)(factory, adapter, out ppAdapter);

            public static int GetDesc(IntPtr fn, IntPtr adapter, IntPtr pDesc)
                => Marshal.GetDelegateForFunctionPointer<GetDescDelegate>(fn)(adapter, pDesc);

            public static int Release(IntPtr fn, IntPtr obj)
                => Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(fn)(obj);
        }
    }
}
