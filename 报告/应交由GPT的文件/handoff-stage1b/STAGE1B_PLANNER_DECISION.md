# DynamicIsland GPU · 阶段 1B 规划者决策

> **结论**：拒绝回传报告中的“选项 A：更换设备创建方案”。当前根因已经定位为 `IDirect3DDxgiInterfaceAccess` 的 IID 写错。
>
> 本轮只允许修改一个 GUID；禁止同时修 D3DImage、shader、设备创建或性能问题。

---

## 1. 根因判定

当前源码/回传报告使用的 IID：

```text
A9B3D012-3DF2-4473-8875-25240F0F3E16
```

这是错误值。

Windows SDK 中 `IDirect3DDxgiInterfaceAccess` 的正确 IID 是：

```text
A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1
```

两者从第三段开始就不同：

```text
错误：A9B3D012-3DF2-4473-8875-25240F0F3E16
正确：A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1
                         ^^^^^^^^^^^^^^^^^^^^^
```

因此以下两个结果完全符合预期，并不能证明 surface 违反契约：

- `AsInterface<IDirect3DDxgiInterfaceAccess>() → E_NOINTERFACE`
- `NativeObject.ThisPtr + QueryInterface(错误 IID) → E_NOINTERFACE`

`surfType=WinRT.IInspectable` 也不能证明 frame surface 不是 `IDirect3DSurface`；这是 C#/WinRT 包装对象的 CLR 类型信息，不等于原生对象支持的 COM 接口集合。

---

## 2. 为什么拒绝更换设备创建方案

`CreateDirect3D11DeviceFromDXGIDevice` 本身就是 Windows SDK 官方提供的 Win32/WinRT 互操作函数，输入 `IDXGIDevice*`，输出包装该设备的 `IInspectable* / IDirect3DDevice`。

回传中提出的：

```csharp
Direct3D11Device.CreateFromDXGIDevice(...)
```

不是本项目当前 Windows SDK 投影中可依赖的公共 API，不得尝试。即使存在其他包装库提供同名辅助方法，也违反“不新增图形包装层”的约束。

---

## 3. 阶段 1B 唯一批准的运行时改动

### 允许修改

仅：

```text
DynamicIsland/LiquidGlass/WinGCCapture.cs
```

把接口声明改为：

```csharp
[ComImport,
 Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
private interface IDirect3DDxgiInterfaceAccess
{
    [PreserveSig]
    int GetInterface(ref Guid iid, out IntPtr ppv);
}
```

如果代码中另有硬编码 `IID_IDirect3DDxgiInterfaceAccess`，必须同步改成同一个正确值。除此之外不得改动运行逻辑。

### 必须保留

- `((IWinRTObject)surface).NativeObject` 主路径。
- `AsInterface<IDirect3DDxgiInterfaceAccess>()`。
- `GetInterface(IID_ID3D11Texture2D)`。
- `cast/hr/lastHr/nativeQiHr/frame/present` 诊断。
- 纯洋红 V-pass。
- `GpuBlur` 中 `tx=0.5f; ty=0.5f`。
- 当前 D3DImage 的未修状态。

### 禁止改动

- `CreateWinRTDevice`。
- `CreateDirect3D11DeviceFromDXGIDevice` P/Invoke。
- NuGet、TFM、平台目标。
- `GpuGlassBackend.PresentOnUI`。
- D3D9/D3D11 共享纹理。
- HLSL/CSO。

---

## 4. 构建前静态确认

执行全文搜索：

```powershell
Get-ChildItem -Recurse -File | Select-String \
  "A9B3D012-3DF2-4473-8875-25240F0F3E16"
```

结果必须为 0 个运行时代码命中。历史报告/日志中的旧值可以保留，但不得被代码引用。

再搜索正确 IID：

```powershell
Get-ChildItem -Recurse -File | Select-String \
  "A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"
```

报告代码命中位置。

---

## 5. 构建与实机验收

```powershell
dotnet build DynamicIsland/DynamicIsland.csproj -c Debug
```

要求：

- 0 错误。
- 现有两条 CS9191 警告可以原样记录，本轮不得顺手清理。

运行步骤：

1. 设置为强制 GPU。
2. 等待 10 秒。
3. 原样抄下“当前生效”整行。
4. 记录胶囊是否出现纯洋红；但本阶段主要依据仍是计数器。

### 判读表

| 实机观察 | 判定 | 下一步 |
|---|---|---|
| `wgc/frame/present` 同时增长，`cast=0, hr=0` | ✅ 正确 IID 修通了表面解包 | 停止本轮，回传；规划者批准进入阶段 2（D3DImage） |
| `frame` 增长但没有洋红 | 表面解包已成功；D3DImage 的已知 Lock 顺序仍阻断显示 | 仍视为阶段 1B 通过；不要在本轮修 D3DImage |
| `cast` 继续增长，`nativeQiHr=E_NOINTERFACE` | 可能仍在运行旧 DLL，或代码中仍使用错误 IID | 先核对设置页运行路径、清理并重建；只允许重测一次 |
| `cast=0`、`hr` 增长、`frame=0` | 访问接口成功，但 `GetInterface(ID3D11Texture2D)` 失败 | 停止，回传 `lastHr` 和实际纹理 IID |
| AccessViolation/崩溃 | 接口方法签名、ABI 或引用计数错误 | 立即停止，回规划者 |

如果清理重建后仍然是 `cast/nativeQiHr=E_NOINTERFACE`，回传以下额外信息：

```text
运行 EXE/DLL 的绝对路径
DLL 修改时间
代码中实际打印的 access IID
nativeQiHr
surface CLR 类型
```

不得自行改用 Desktop Duplication、SoftwareBitmap 或其他设备创建方式。

---

## 6. PLAN_LOG 追加格式

```text
【阶段 1B：纠正 IDirect3DDxgiInterfaceAccess IID】
唯一运行时改动：错误 IID → A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1
构建：
运行路径：
10 秒计数：
肉眼观察：
命中判读表：
下一跳：停止并回规划者
```
