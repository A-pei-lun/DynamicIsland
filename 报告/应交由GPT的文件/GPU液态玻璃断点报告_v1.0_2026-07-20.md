# GPU 液态玻璃修复 · 断点报告

> **日期**：2026-07-20
> **执行阶段**：Fable 5 执行计划 · 阶段 1 → 分支 B（呈现链路排查）
> **当前状态**：**已超出计划覆盖范围，请求规划者决策下一步方向**

---

## 一、摘要

GPU 液态玻璃后端（`GpuGlassBackend`）在 Win11 26100 / .NET 10 上遇到了 **WinRT COM interop 根兼容性问题**：`Direct3D11CaptureFramePool` 产生的 `IDirect3DSurface` 不支持标准的 `IDirect3DDxgiInterfaceAccess` 接口，导致 **GPU 管线从未收到任何一帧**，后续所有处理（D3D11 模糊、D3D9 共享纹理、D3DImage 呈现）全部无法执行。

---

## 二、执行过程与发现

### 阶段 0 — 基线确认 ✅

| 项 | 值 |
|---|---|
| 设置窗显示 | `GPU 硬件加速 (r=1.6, 296x59, mapFail=0)` |
| 结论 | GPU 后端启动成功，无静默回退 |

### 阶段 1 — 洋红管线可见性测试 ❌

- 把 V-pass 着色器改为无条件输出 `float4(1,0,1,1)` 纯洋红
- fxc 编译 → `dotnet build` → 双重验证（`/dumpbin` + PowerShell 提取 DLL 内嵌资源）确认新 CSO 已嵌入
- **用户观察**：胶囊完全没变，无洋红

### 分支 B · 步骤 1 — DWM 底座嫌疑

- 禁用 `WindowBackdrop` 中 LiquidGlass 的 DWM backdrop（`ACCENT_DISABLED`）
- **用户观察**：胶囊变全透明，GPU 玻璃层未显示任何内容
- **结论**：DWM 底座提供了可见效果，但 GPU 渲染层完全不可见

### 分支 B · 步骤 2-4 — 帧计数器诊断

在 `GpuGlassBackend` 和 `WinGCCapture` 中逐层添加计数器：

| 计数器 | 值 | 含义 |
|---|---|---|
| `wgc` | 759+ 增长 | ✅ WinGC 回调正常触发 |
| `wgcFail` | 759+ 增长 | ❌ 每帧纹理提取均失败 |
| `cast` | 759+ 增长 | ❌ 表面接口转换失败 |
| `hr` | 0 | `GetInterface` 从未执行 |
| `lastHr` | **-2147467262** (0x80004002) | `E_NOINTERFACE` |
| `frame` | 0 | ❌ `GpuGlassBackend.OnFrameArrived` 从未被调 |
| `present` | 0 | ❌ `PresentOnUI` 从未被调 |

---

## 三、根因分析

### 问题链路

```
WinGC 回调触发 ✅
  → TryGetNextFrame() 成功 ✅
    → frame.Surface 非空 ✅
      → QueryInterface(IDirect3DDxgiInterfaceAccess) ❌ E_NOINTERFACE
        → 纹理提取失败 → 事件不触发 → GpuGlassBackend 不处理 → 无帧呈现
```

### 关键代码路径

`WinGCCapture.OnFrameArrived` 中，从 `frame.Surface`（`IDirect3DSurface`）提取底层 `ID3D11Texture2D` 的标准做法是通过 `IDirect3DDxgiInterfaceAccess` 接口：

```csharp
// 标准写法（windows.graphics.directx.direct3d11.interop.h 定义的标准接口）
[ComImport, Guid("A9B3D012-3DF2-4473-8875-25240F0F3E16")]
interface IDirect3DDxgiInterfaceAccess {
    int GetInterface(ref Guid iid, out IntPtr ppv);
}
```

但 `surface.As<IDirect3DDxgiInterfaceAccess>()` 和 `Marshal.QueryInterface` 均返回 `E_NOINTERFACE`。

### 已尝试的修复

| 尝试 | 结果 |
|---|---|
| `surface.As<IDirect3DDxgiInterfaceAccess>()` | ❌ E_NOINTERFACE |
| `Marshal.QueryInterface(surfUnk, iidAccess, ...)` | ❌ E_NOINTERFACE |
| 直接 QI `ID3D11Texture2D` | ❌ E_NOINTERFACE |
| 添加 `D3D11_CREATE_DEVICE_BGRA_SUPPORT` 标志 | ❌ 无效 |
| 尝试 `Direct3D11Device.CreateFromDXGIDevice` | ❌ CsWin32 不投影此 WinRT 类 |

### WinRT 设备创建方式

当前使用 `d3d11.dll` 的 `CreateDirect3D11DeviceFromDXGIDevice` 函数创建 WinRT `IDirect3DDevice`：

```csharp
[DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

static IDirect3DDevice CreateWinRTDevice(ID3D11Device dev11) {
    IntPtr unk = Marshal.GetIUnknownForObject(dev11);
    Marshal.QueryInterface(unk, in iidDxgi, out dxgiPtr);
    CreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out IntPtr inspectablePtr);
    return MarshalInspectable<IDirect3DDevice>.FromAbi(inspectablePtr);
}
```

此函数返回的 `IInspectable*` 通过 `MarshalInspectable<IDirect3DDevice>.FromAbi()` 包装为 `IDirect3DDevice`，并成功创建了 `Direct3D11CaptureFramePool` 和 `GraphicsCaptureSession`。但该设备产生的 `IDirect3DSurface` **不支持 `IDirect3DDxgiInterfaceAccess`**。

---

## 四、环境信息

| 项 | 值 |
|---|---|
| 操作系统 | Windows 11 (build 26100) |
| .NET SDK | 10.0.302 |
| TFM | `net10.0-windows10.0.26100.0` |
| 平台 | x64 |
| CsWin32 | 0.3.298 |
| 项目 | DynamicIsland (WPF, WinExe) |

---

## 五、需要规划者决策的方向

### 方向 A：回退 Hlsl 后端，放弃 GPU 路径

- **代价**：失去 GPU 加速优势（UI 线程零阻塞、60fps 事件驱动）
- **优点**：零额外开发成本，Hlsl 后端已验证可用
- **风险**：无

### 方向 B：改用 Desktop Duplication API (DXGI) 替代 WinGC

- **原理**：`IDXGIOutputDuplication` 直接返回 `ID3D11Texture2D`，无需 WinRT 桥接
- **优点**：原生 D3D11 接口，无 COM interop 兼容性问题；可获取更高帧率（非 60fps 限制）
- **代价**：需要重写抓屏层（`IDesktopCapture` 接口另一个实现），DXGI 采集需要 D3D11 设备有 `D3D11_CREATE_DEVICE_DEBUG` 或特定标志
- **风险**：中等开发量；DXGI 路径在某些显卡驱动上可能有稳定性问题

### 方向 C：SoftwareBitmap CPU 中转

- **原理**：`SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface)` → CPU 回读 → `ID3D11DeviceContext.UpdateSubresource` 写入 GPU 纹理
- **优点**：无需改动 WinGC 框架，`IDirect3DSurface` 到 `SoftwareBitmap` 不需要 `IDirect3DDxgiInterfaceAccess`
- **代价**：每帧 CPU 回读 + GPU 上传，性能损失大，失去 GPU 加速意义
- **风险**：低

### 方向 D：其他方案

- 尝试使用 `Windows.Graphics.DirectX.Direct3D11.Interop` 命名空间中的其他 API
- 更换 WinRT 投影方式（如直接引用 `Microsoft.Windows.SDK.NET` 而不是 CsWin32）
- 使用 `IDXGIDevice` 的 `QueryInterface` 获取 `IDirect3DDevice` 的另一种方式

---

## 六、附：关键源代码

### 6.1 WinGCCapture.cs（完整，200 行）

**路径**：`DynamicIsland/LiquidGlass/WinGCCapture.cs`

```csharp
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
    internal sealed class WinGCCapture : IDesktopCapture
    {
        // ── P/Invoke: WinRT 桥（d3d11.dll 把 IDXGIDevice 包成 WinRT IDirect3DDevice）──
        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        // ── COM interop: 从 WinRT IDirect3DSurface 取底层 ID3D11Texture2D ──
        [ComImport, Guid("A9B3D012-3DF2-4473-8875-25240F0F3E16"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            [PreserveSig] int GetInterface(ref Guid iid, out IntPtr ppv);
        }

        // ── COM interop: GraphicsCaptureItem.CreateForMonitor ──
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
        public int WinGcFrameCount { get; private set; }
        public int WinGcExtractFailCount { get; private set; }
        public int WinGcCastFail { get; private set; }
        public int WinGcHrFail { get; private set; }
        public int WinGcLastHr { get; private set; }
        public string Name => $"WinGC 抓屏 (cb={WinGcFrameCount}, fail={WinGcExtractFailCount})";

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

            _session = _pool.CreateCaptureSession(_item);
            try { _session.IsBorderRequired = false; } catch { }
            try { _session.IsCursorCaptureEnabled = false; } catch { }
            _session.StartCapture();
            IsRunning = true;
        }

        // ⚠️ 核心问题在此方法
        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object _)
        {
            WinGcFrameCount++;
            if (!IsRunning) return;
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;

            var surface = frame.Surface;
            try
            {
                // 标准做法：IDirect3DSurface -> IDirect3DDxgiInterfaceAccess -> ID3D11Texture2D
                // 但 QueryInterface 返回 E_NOINTERFACE
                IntPtr surfUnk = Marshal.GetIUnknownForObject(surface);
                Guid iidTex = typeof(ID3D11Texture2D).GUID;
                int hrTex = Marshal.QueryInterface(surfUnk, ref iidTex, out IntPtr texPtr);
                Marshal.Release(surfUnk);
                if (hrTex == 0 && texPtr != IntPtr.Zero)
                {
                    var tex2 = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(texPtr);
                    FrameArrived?.Invoke(tex2, (uint)frame.ContentSize.Width, (uint)frame.ContentSize.Height);
                    try { Marshal.ReleaseComObject(tex2); } catch { }
                }
                else
                {
                    WinGcCastFail++;
                    WinGcExtractFailCount++;
                    WinGcLastHr = hrTex;
                }
            }
            catch { WinGcExtractFailCount++; }
        }

        private static IDirect3DDevice CreateWinRTDevice(ID3D11Device dev11)
        {
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

        private static GraphicsCaptureItem CreateCaptureItem(IntPtr hmonitor) { /* ... 实现略 */ }

        // Stop/Dispose 略
    }
}
```

### 6.2 GpuGlassBackend.cs（关键部分）

**路径**：`DynamicIsland/LiquidGlass/GpuGlassBackend.cs`

```csharp
// D3D11 设备创建（已添加 BGRA_SUPPORT 标志，但无效）
PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, default,
    D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
    ReadOnlySpan<D3D_FEATURE_LEVEL>.Empty, 7,
    out _dev11, out _, out _ctx11);

// 启动 WinGC 捕获
_capture = new WinGCCapture(_dev11);
_capture.FrameArrived += OnFrameArrived;
// ...
_capture.Start(hmon);

// OnFrameArrived 从未被调（因为 WinGCCapture 内部纹理提取失败）
private void OnFrameArrived(ID3D11Texture2D frameTex, uint monW, uint monH)
{
    _frameCount++;  // 从未执行
    // ... 后续处理
}
```

### 6.3 WindowBackdrop.cs（LiquidGlass 模式）

**路径**：`DynamicIsland/WindowBackdrop.cs`

LiquidGlass 模式使用 `ACCENT_ENABLE_TRANSPARENTGRADIENT`（state2），锐利看穿无模糊。DWM 底座禁用后胶囊变全透明，确认 GPU 玻璃层完全不显示。

---

## 七、附：执行日志

完整执行日志见项目根目录 `PLAN_LOG.md`，包含每一轮的"改动→现象→判读→跳转"记录。

---

*本报告由执行模型生成，供规划者（Fable 5）决策下一步方向。*