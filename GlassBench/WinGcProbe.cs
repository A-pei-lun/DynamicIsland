using System;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace GlassBench;

internal static class WinGcProbe
{
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, uint length, out IntPtr hstring);
    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IInspectable = new("AF86E2E0-B12D-4C60-9C5A-D7AA65101E90");

    public static void Run()
    {
        Console.WriteLine("=== WinGC 阶段1: CreateForWindow vs CreateForMonitor ===");
        try
        {
            Guid iidGci = Guid.Empty;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                    if (t.IsInterface && t.Name == "IGraphicsCaptureItem")
                    { iidGci = t.GUID; Console.WriteLine($"  找到 {t.FullName} GUID={t.GUID}"); }
            }
            if (iidGci == Guid.Empty) Console.WriteLine("  未找到 IGraphicsCaptureItem 接口类型");

            string clsid = "Windows.Graphics.Capture.GraphicsCaptureItem";
            WindowsCreateString(clsid, (uint)clsid.Length, out IntPtr hstr);
            Guid iidInterop = IID_IGraphicsCaptureItemInterop;
            int hr = RoGetActivationFactory(hstr, ref iidInterop, out IntPtr factoryPtr);
            WindowsDeleteString(hstr);
            Console.WriteLine($"  RoGetActivationFactory hr=0x{hr:X8}");
            if (hr != 0) return;

            try
            {
                Guid iidQ = IID_IGraphicsCaptureItemInterop;
                Marshal.QueryInterface(factoryPtr, ref iidQ, out IntPtr interopPtr);
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetTypedObjectForIUnknown(interopPtr, typeof(IGraphicsCaptureItemInterop));

                IntPtr hwnd = GetConsoleWindow();
                IntPtr hmon = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
                Console.WriteLine($"  console HWND=0x{hwnd.ToInt64():X}, HMONITOR=0x{hmon.ToInt64():X}");

                var iids = new System.Collections.Generic.List<(string, Guid)> { ("IInspectable", IID_IInspectable) };
                if (iidGci != Guid.Empty) iids.Add(("IGraphicsCaptureItem", iidGci));

                foreach (var (name, g0) in iids)
                {
                    Guid g = g0;
                    int hw = interop.CreateForWindow(hwnd, ref g, out IntPtr pw);
                    Console.WriteLine($"  CreateForWindow  iid={name,-20} hr=0x{hw:X8} ptr=0x{pw.ToInt64():X}");
                    int hm = interop.CreateForMonitor(hmon, ref g, out IntPtr pm);
                    Console.WriteLine($"  CreateForMonitor iid={name,-20} hr=0x{hm:X8} ptr=0x{pm.ToInt64():X}");
                    if (pm != IntPtr.Zero || pw != IntPtr.Zero)
                    {
                        var ptr = pm != IntPtr.Zero ? pm : pw;
                        var item = GraphicsCaptureItem.FromAbi(ptr);
                        Console.WriteLine($"  -> 命中! {item.DisplayName} {item.Size.Width}x{item.Size.Height}");
                        return;
                    }
                }
                Console.WriteLine("  -> 都失败");
                Marshal.Release(interopPtr);
            }
            finally { Marshal.Release(factoryPtr); }
        }
        catch (Exception ex) { Console.WriteLine($"  异常: {ex.GetType().Name}: {ex.Message}"); }
    }
}
