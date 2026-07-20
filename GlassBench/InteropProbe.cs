using System;
using System.Reflection;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D9;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;

namespace GlassBench;

/// <summary>
/// 运行时探测：CsWin32 生成的 D3D9/D3D11/DXGI interop 类型与关键方法是否齐全。
/// dotnet run -- --probe 触发。类型用 typeof（build 期即可验证），方法用反射（运行期确认签名生成）。
/// </summary>
internal static class InteropProbe
{
    public static void Run()
    {
        Console.WriteLine("=== CsWin32 interop 覆盖探测 ===");
        Console.WriteLine();
        ProbeTypes();
        Console.WriteLine();
        ProbeMethods();
        Console.WriteLine();
        DumpSigs();
        Console.WriteLine();
        DumpD3D11Interop();
    }

    private static void DumpD3D11Interop()
    {
        Console.WriteLine("=== D3D11 interop 形态诊断 ===");
        // D3D11_TEXTURE2D_DESC 字段类型（定位 DXGI_FORMAT 命名空间等）
        Console.WriteLine("D3D11_TEXTURE2D_DESC 成员:");
        foreach (var m in typeof(D3D11_TEXTURE2D_DESC).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Console.WriteLine($"  {m.MemberType,-9} {m.Name}");
        foreach (var p in typeof(D3D11_TEXTURE2D_DESC).GetProperties())
            Console.WriteLine($"  prop {p.Name,-16} : {p.PropertyType.FullName}");
        Console.WriteLine("D3D11_SUBRESOURCE_DATA props:");
        foreach (var p in typeof(D3D11_SUBRESOURCE_DATA).GetProperties())
            Console.WriteLine($"  prop {p.Name,-16} : {p.PropertyType.FullName}");
        Console.WriteLine("D3DLOCKED_RECT props:");
        foreach (var p in typeof(D3DLOCKED_RECT).GetProperties())
            Console.WriteLine($"  prop {p.Name,-16} : {p.PropertyType.FullName}");

        // 直接扫描 DXGI_FORMAT / DXGI_SAMPLE_DESC 的命名空间
        foreach (var want in new[] { "DXGI_FORMAT", "DXGI_SAMPLE_DESC", "D3D_FEATURE_LEVEL" })
        {
            Type? found = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] ts; try { ts = asm.GetTypes(); } catch { continue; }
                foreach (var ty in ts) if (ty.Name == want) { found = ty; break; }
                if (found != null) break;
            }
            Console.WriteLine($"  扫描 {want} : {(found?.FullName ?? "未找到")}");
        }

        // ID3D11Texture2D 是 RCW(interface) 还是 unmanaged struct？
        var t11 = typeof(ID3D11Texture2D);
        Console.WriteLine($"ID3D11Texture2D : FullName={t11.FullName} IsInterface={t11.IsInterface} IsValueType={t11.IsValueType}");

        // 找 ID3D11Texture2D_unmanaged
        Type? u = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts; try { ts = asm.GetTypes(); } catch { continue; }
            foreach (var ty in ts) if (ty.Name == "ID3D11Texture2D_unmanaged") { u = ty; break; }
            if (u != null) break;
        }
        if (u != null)
        {
            Console.WriteLine($"ID3D11Texture2D_unmanaged : FullName={u.FullName} IsValueType={u.IsValueType}");
            Console.WriteLine("  自定义转换运算符:");
            foreach (var m in u.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                if (m.Name == "op_Implicit" || m.Name == "op_Explicit")
                    Console.WriteLine($"    {m.Name} {m.ReturnType.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
        }
        else Console.WriteLine("ID3D11Texture2D_unmanaged : 未找到");

        // 全部 CreateTexture2D 重载（含扩展方法）
        Console.WriteLine("全部 CreateTexture2D 重载（全程序集，标 ext=静态扩展）:");
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts; try { ts = asm.GetTypes(); } catch { continue; }
            foreach (var t in ts)
            {
                MethodInfo[] ms;
                try { ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (var m in ms)
                    if (m.Name == "CreateTexture2D")
                        Console.WriteLine($"  {t.FullName}.CreateTexture2D({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))}) ext={m.IsStatic} ret={m.ReturnType.Name}");
            }
        }

        // IDXGIResource1 是否 RCW + CreateSharedHandle 重载
        Console.WriteLine($"IDXGIResource1 : IsInterface={typeof(IDXGIResource1).IsInterface} FullName={typeof(IDXGIResource1).FullName}");
        Console.WriteLine($"IDXGIKeyedMutex : IsInterface={typeof(IDXGIKeyedMutex).IsInterface} FullName={typeof(IDXGIKeyedMutex).FullName}");
    }

    private static void DumpSigs()
    {
        Console.WriteLine("关键方法签名:");
        DumpSig(typeof(PInvoke), "Direct3DCreate9Ex");
        DumpSig(typeof(IDirect3D9Ex), "CreateDeviceEx");
        DumpSig(typeof(PInvoke), "D3D11CreateDevice");
        DumpSig(typeof(ID3D11Device), "CreateTexture2D");
        DumpSig(typeof(ID3D11Device), "CreateRenderTargetView");
        DumpSig(typeof(ID3D11Device), "OpenSharedResource");
        DumpSig(typeof(IDirect3DDevice9Ex), "CreateTexture");
        DumpSig(typeof(IDirect3DTexture9), "GetSurfaceLevel");
        DumpSig(typeof(ID3D11DeviceContext), "ClearRenderTargetView");
        DumpSig(typeof(ID3D11Texture2D), "QueryInterface");
        DumpSig(typeof(IDXGIResource1), "CreateSharedHandle");
        DumpSig(typeof(IDXGIKeyedMutex), "AcquireSync");
        DumpSig(typeof(IDXGIKeyedMutex), "ReleaseSync");
        DumpSig(typeof(IDirect3DDevice9Ex), "CreateOffscreenPlainSurface");
        DumpSig(typeof(IDirect3DDevice9Ex), "GetRenderTargetData");

        Console.WriteLine();
        Console.WriteLine("[dump] IDXGIKeyedMutex 全部方法:");
        foreach (var m in typeof(IDXGIKeyedMutex).GetMethods())
            Console.WriteLine($"  {m.Name}");
        Console.WriteLine();
        Console.WriteLine("[dump] IDXGIResource1 全部方法:");
        foreach (var m in typeof(IDXGIResource1).GetMethods())
            Console.WriteLine($"  {m.Name}");
    }

    private static void DumpSig(Type t, string name)
    {
        var ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                   .Where(m => m.Name == name);
        foreach (var m in ms)
        {
            var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Console.WriteLine($"  {t.Name}.{m.Name}({ps}) -> {m.ReturnType.Name}");
        }
    }

    private static void ProbeTypes()
    {
        Console.WriteLine("类型（typeof 命中即生成成功）:");
        var types = new (Type t, string name)[]
        {
            (typeof(IDirect3D9), nameof(IDirect3D9)),
            (typeof(IDirect3D9Ex), nameof(IDirect3D9Ex)),
            (typeof(IDirect3DDevice9), nameof(IDirect3DDevice9)),
            (typeof(IDirect3DDevice9Ex), nameof(IDirect3DDevice9Ex)),
            (typeof(IDirect3DSurface9), nameof(IDirect3DSurface9)),
            (typeof(IDirect3DBaseTexture9), nameof(IDirect3DBaseTexture9)),
            (typeof(ID3D11Device), nameof(ID3D11Device)),
            (typeof(ID3D11DeviceContext), nameof(ID3D11DeviceContext)),
            (typeof(ID3D11Texture2D), nameof(ID3D11Texture2D)),
            (typeof(IDXGIFactory1), nameof(IDXGIFactory1)),
            (typeof(IDXGIResource1), nameof(IDXGIResource1)),
            (typeof(IDXGIOutputDuplication), nameof(IDXGIOutputDuplication)),
        };
        foreach (var (t, name) in types)
            Console.WriteLine($"  {name,-28} OK  ({t.Namespace})");
    }

    private static void ProbeMethods()
    {
        Console.WriteLine("关键方法（反射命中即签名已生成）:");
        Check(typeof(IDirect3DSurface9), "OpenSharedResource");
        Check(typeof(IDirect3DDevice9), "OpenSharedResource");
        Check(typeof(IDirect3DDevice9Ex), "OpenSharedResource");
        Check(typeof(IDirect3D9Ex), "CreateDeviceEx");
        Check(typeof(IDirect3D9Ex), "GetAdapterDisplayModeEx");
        Check(typeof(IDirect3DDevice9Ex), "CreateOffscreenPlainSurface");
        Check(typeof(IDirect3DDevice9Ex), "GetRenderTargetData");
        Check(typeof(ID3D11Device), "OpenSharedResource");
        Check(typeof(ID3D11Device1), "OpenSharedResource1");
        Check(typeof(ID3D11Device), "CreateTexture2D");
        Check(typeof(IDXGIResource1), "CreateSharedHandle");
        Check(typeof(IDXGIOutputDuplication), "AcquireNextFrame");
        Check(typeof(IDXGIOutput1), "DuplicateOutput");
        Check(typeof(PInvoke), "Direct3DCreate9");
        Check(typeof(PInvoke), "Direct3DCreate9Ex");
        Check(typeof(PInvoke), "D3D11CreateDevice");
        Check(typeof(PInvoke), "CreateDXGIFactory1");
        Check(typeof(PInvoke), "CreateDXGIFactory2");

        Console.WriteLine();
        Console.WriteLine("IID 校验（OpenSharedResource 的 riid）：");
        Console.WriteLine($"  IDirect3DSurface9.GUID = {typeof(IDirect3DSurface9).GUID}");
        Console.WriteLine($"  IDirect3DTexture9.GUID = {typeof(IDirect3DTexture9).GUID}");
        Console.WriteLine($"  ID3D11Texture2D.GUID   = {typeof(ID3D11Texture2D).GUID}");

        Console.WriteLine();
        Console.WriteLine("[dump] PInvoke 类中 3D/DXGI/Direct/Factory 相关方法:");
        foreach (var m in typeof(PInvoke).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            if (m.Name.Contains("3D") || m.Name.Contains("Dxgi") || m.Name.Contains("Direct") || m.Name.Contains("Factory"))
                Console.WriteLine($"  PInvoke.{m.Name}");

        Console.WriteLine();
        Console.WriteLine("[dump] IDirect3DDevice9Ex 全部方法:");
        foreach (var m in typeof(IDirect3DDevice9Ex).GetMethods())
            Console.WriteLine($"  {m.Name}");

        Console.WriteLine();
        Console.WriteLine("[dump] IDirect3DDevice9 (基) 全部方法:");
        foreach (var m in typeof(IDirect3DDevice9).GetMethods())
            Console.WriteLine($"  {m.Name}");
    }

    private static void Check(Type t, string method)
    {
        var m = t.GetMethod(method);
        Console.WriteLine($"  {t.Name}.{method,-26} {(m != null ? "OK" : "❌ 缺失")}");
    }
}
