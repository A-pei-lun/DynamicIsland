# DynamicIsland GPU 阶段 1 终止 · 回规划者申请

> **日期**：2026-07-20
> **依据计划**：`报告/DynamicIsland_GPU_EXECUTION_PLAN_V2.md`
> **命中停止条件**：第 12 节第 1 条

---

## 1. 执行摘要

| 项 | 值 |
|---|---|
| 当前阶段 | **阶段 1 → 分支 1A**（修通 WinRT IDirect3DSurface 解包） |
| 停止原因 | `NativeObject.ThisPtr` 原生 QI `IDirect3DDxgiInterfaceAccess` 仍为 E_NOINTERFACE |
| 结论 | `frame.Surface` 的 COM 对象**确实不支持** `IDirect3DDxgiInterfaceAccess`，违反互操作契约 |
| 根因推测 | `CreateDirect3D11DeviceFromDXGIDevice`（d3d11.dll P/Invoke）创建的 WinRT 设备与 `Direct3D11CaptureFramePool` 不兼容 |

---

## 2. 实机计数（强制 GPU 模式，10 秒稳定后）

```
当前生效：GPU 硬件加速 (r=0.0, 296x59, mapFail=0, present=0, frame=0,
  wgc=1106, wgcFail=1106, cast=1106, hr=0, lastHr=-2147467262,
  nativeQiHr=-2147467262, surfType=WinRT.IInspectable)
```

| 计数器 | 值 | 含义 |
|---|---|---|
| `wgc` | 1106 | WinGC FramePool 回调正常触发 ✅ |
| `wgcFail` | 1106 | 纹理提取 100% 失败 ❌ |
| `cast` | 1106 | `AsInterface<IDirect3DDxgiInterfaceAccess>` 100% 失败 |
| `hr` | 0 | `GetInterface` 没走到（因为上一步就失败了） |
| `lastHr` | -2147467262 (0x80004002) | **E_NOINTERFACE** |
| `nativeQiHr` | -2147467262 | **原生 ABI 指针 QI 也返回 E_NOINTERFACE** |
| `surfType` | `WinRT.IInspectable` | surface 被投影为裸 IInspectable，不是 `IDirect3DSurface` |

---

## 3. 两路验证均已失败

### 路径 A：C#/WinRT 投影层

```csharp
var nativeObj = ((IWinRTObject)surface).NativeObject;
var access = nativeObj.AsInterface<IDirect3DDxgiInterfaceAccess>();
// → 抛出异常，E_NOINTERFACE
```

### 路径 B：原生 ABI 指针直接 QueryInterface

```csharp
IntPtr thisPtr = ((IWinRTObject)surface).NativeObject.ThisPtr;
Guid iidAccess = typeof(IDirect3DDxgiInterfaceAccess).GUID;  // A9B3D012-3DF2-4473-8875-25240F0F3E16
int hr = Marshal.QueryInterface(thisPtr, ref iidAccess, out IntPtr accessPtr);
// → hr = 0x80004002 (E_NOINTERFACE)
```

---

## 4. 修改的文件

### 4.1 `DynamicIsland/LiquidGlass/WinGCCapture.cs`

**变更内容**：`OnFrameArrived` 解包路径。

**旧代码**（错误路径）：
```csharp
IntPtr surfUnk = Marshal.GetIUnknownForObject(surface);
Guid iidTex = typeof(ID3D11Texture2D).GUID;
int hrTex = Marshal.QueryInterface(surfUnk, ref iidTex, out IntPtr texPtr);
```

**新代码**（计划 V2 正确路径 → 分支 1A fallback）：

```csharp
// 主路径：IWinRTObject → AsInterface → GetInterface
var nativeObj = ((IWinRTObject)surface).NativeObject;
var access = nativeObj.AsInterface<IDirect3DDxgiInterfaceAccess>();
Guid iidTex = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
int hr = access.GetInterface(ref iidTex, out IntPtr texPtr);

// catch 中分支 1A fallback：原生 ABI 指针直接 QI
IntPtr thisPtr = ((IWinRTObject)surface).NativeObject.ThisPtr;
Guid iidAccess = typeof(IDirect3DDxgiInterfaceAccess).GUID;
WinGcNativeQiHr = Marshal.QueryInterface(thisPtr, ref iidAccess, out IntPtr accessPtr);
```

新增诊断字段：
- `WinGcNativeQiHr` — 原生 ABI QI 的 HRESULT
- `WinGcSurfaceType` — surface 实际 CLR 类型 (`WinRT.IInspectable`)

### 4.2 `DynamicIsland/LiquidGlass/GpuGlassBackend.cs`

**变更内容**：仅 `Name` 属性第 70 行，新增 `nativeQiHr` 和 `surfType` 字段显示。

### 4.3 `报告/PLAN_LOG.md`

已追加分支 1A 完整日志。

---

## 5. 构建结果

```
dotnet build DynamicIsland/DynamicIsland.csproj -c Debug
→ 0 错误，2 警告（CS9191 ref→in 建议，不影响运行）
```

---

## 6. 规划者需决策

### 选项 A：更换设备创建方案（推荐）

将 `CreateWinRTDevice` 从 d3d11.dll P/Invoke 改为官方 WinRT API：

```csharp
// 当前（不兼容）：
[DllImport("d3d11.dll")]
static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

// 改为（官方投影）：
using Windows.Graphics.DirectX.Direct3D11;
var d3dDevice = Direct3D11Device.CreateFromDXGIDevice(dxgiDevice);
```

**理由**：`Direct3D11Device.CreateFromDXGIDevice` 是 Windows SDK 中官方提供的 WinRT API，它创建的 `IDirect3DDevice` 与 `Direct3D11CaptureFramePool` 属于同一投影体系，产生的 `frame.Surface` 应该正确支持 `IDirect3DDxgiInterfaceAccess`。

**影响范围**：仅 `WinGCCapture.CreateWinRTDevice` 方法。不违反计划铁律（无新 NuGet、不改 TFM、不改图形库）。

### 选项 B：回滚到 ZIP 基线，换其他方向

### 选项 C：其他方案

---

## 7. 附：当前文件清单

| 文件 | 路径 |
|---|---|
| 修改后 WinGCCapture.cs | `DynamicIsland/LiquidGlass/WinGCCapture.cs` |
| 修改后 GpuGlassBackend.cs | `DynamicIsland/LiquidGlass/GpuGlassBackend.cs` |
| 执行日志 | `报告/PLAN_LOG.md` |
| 执行计划 V2 | `报告/DynamicIsland_GPU_EXECUTION_PLAN_V2.md` |
| 原始源码 ZIP | `DynamicIsland_完整源码_v1.0_2026-07-20.zip` |