# DynamicIsland：Windows 图形互操作灰区调查与社区求助稿

> 调查日期：2026-07-20  
> 项目：[`A-pei-lun/DynamicIsland`](https://github.com/A-pei-lun/DynamicIsland)  
> 技术栈：.NET 10、WPF、x64、Windows SDK 26100、CsWin32 0.3.298  
> 范围：WinGC / WinRT、D3D11、D3D9Ex、WPF `D3DImage`、Windows Composition 之间的互操作边界。

## 先说结论

这个项目确实碰到了社区资料稀少的交界区，但不能把所有失败都统称为“Windows 灰色地带”。经官方资料和微软公开仓库核验后，最准确的分类是：

| 尝试 | 结论 | 灰度 | 最值得向社区提问的点 |
|---|---|---:|---|
| `Direct3D11CaptureFrame.Surface` → `ID3D11Texture2D` | 原生契约正式存在；现代 .NET/CsWinRT 到 CsWin32 的桥接示例和所有权说明不足 | 高（托管投影层） | 如何从 C#/WinRT 投影对象安全取得 `IDirect3DDxgiInterfaceAccess`，并转为 CsWin32 的 D3D11 类型 |
| D3D11 纹理 → D3D9Ex 共享纹理 | 共享能力有正式文档，但格式、创建方向、适配器、资源标志限制严格 | 中 | 当前纹理描述和共享句柄流程是否满足 D3D9Ex/D3D11 双方约束 |
| D3D11 后台写共享纹理 → WPF `D3DImage` 读取 | 缺少覆盖三方的现代端到端样例；同步模型最危险 | **很高** | WPF 拷贝、D3D11 GPU 队列和 D3D9Ex 视图之间应使用什么受支持的同步协议 |
| `D3DImage` 无画面 | 单独看不是灰区，官方已列出很多“合法但不显示”的条件 | 低 | 需要先排除 front buffer、格式、pool、usage、adapter、Lock/Unlock、软件渲染等条件 |
| Win32/WPF + `CreateHostBackdropBrush` | API 本身公开，但 Win32 host-backdrop 路径长期缺少受支持契约 | **很高** | 当前 Windows 11 是否已有不依赖私有 accent state 的受支持 Win32/WPF host-backdrop 方案 |
| `SetWindowCompositionAttribute` + `ACCENT_ENABLE_HOSTBACKDROP=5` | 函数已可查，但微软明确不推荐；state 5 并无稳定的公开枚举契约 | **私有/不稳定** | 只能做兼容性 Spike，不适合作为长期产品契约 |
| “WinGC 硬限 60fps” | 未找到微软公开的固定 60fps 契约 | 不是灰区，属于待测假设 | 应发布实测方法和时间戳数据，而不是写成平台硬上限 |
| “Composition 零延迟、必定吃满 240Hz” | 没有公开保证 | 不是灰区，属于过度承诺 | 用 PresentMon/ETW/高速摄像或帧时间统计验证 |

最值得公开的不是“Windows 有 bug”这一结论，而是两个可独立复现的问题：

1. **C#/WinRT 投影对象到原生 `ID3D11Texture2D` 的正确 ABI 路径是什么？**
2. **D3D11 生产、D3D9Ex 共享、WPF `D3DImage` 消费同一表面时，受支持的同步协议是什么？**

Composition/HostBackdrop 应单独发帖，不能与 WinGC/D3DImage 问题混在同一个 Issue 里。

---

## 1. 项目实际尝试与已观察现象

当前实验性 GPU 管线是：

```text
Windows.Graphics.Capture（后台 FrameArrived）
  -> Direct3D11CaptureFrame.Surface（WinRT IDirect3DSurface）
  -> 原生 ID3D11Texture2D
  -> CopySubresourceRegion 裁剪
  -> D3D11 双遍模糊
  -> D3D11/D3D9Ex 共享纹理
  -> IDirect3DSurface9
  -> WPF D3DImage.Lock / SetBackBuffer / AddDirtyRect / Unlock
```

项目文档记录的直接事实：

- WinGC 回调能够进入，但 surface 原生接口提取存在失败，已加入 `wgcFail`、`surfType`、`nativeQiHr` 等计数。
- 强制 GPU 后端时可能完全没有玻璃画面；Auto 可能回退到 HLSL/BitBlt 路径。
- D3D11 V-pass 仍曾使用纯洋红可见性 shader；texel 参数也曾被诊断值 `0.5` 覆盖。
- 管线为排查而加入了大量阶段计数，说明目前还没有“哪一层一定正确”的可视化证明链。

因此目前能公开陈述的是“端到端管线尚未建立成功”，不能直接陈述“WinGC 返回的 surface 不支持 `IDirect3DDxgiInterfaceAccess`”或“D3DImage 在 Windows 11 上坏了”。

---

## 2. 灰区一：WinRT `IDirect3DSurface` 到原生 D3D11 的托管桥接

### 2.1 官方契约其实很明确

微软文档说明，WinRT [`IDirect3DSurface`](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.directx.direct3d11.idirect3dsurface) 表示一个 `IDXGISurface`，用于在 WinRT 组件间交换 DXGI surface。官方 C++/WinRT 示例是在 **surface 对象本身** 上查询 `IDirect3DDxgiInterfaceAccess`，然后调用 `GetInterface` 得到原生 DXGI/D3D 接口。

[`IDirect3DDxgiInterfaceAccess`](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.directx.direct3d11.interop/ns-windows-graphics-directx-direct3d11-interop-idirect3ddxgiinterfaceaccess) 的契约更直接：实现 `IDirect3DDevice` 或 `IDirect3DSurface` 的对象必须实现这个 COM 接口；[`GetInterface`](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.directx.direct3d11.interop/nf-windows-graphics-directx-direct3d11-interop-idirect3ddxgiinterfaceaccess-getinterface) 用传入的 IID 返回被包装的 DXGI 接口。

这意味着：

- 正确的 `frame.Surface` 在原生 ABI 层应可查询该接口；
- 应对 `frame.Surface` 查询，而不是对 `Direct3D11CaptureFrame` 查询；
- 如果 `QueryInterface` 返回 `E_NOINTERFACE`，优先怀疑查询对象、投影包装、IID、ABI 声明或原生指针取得方式，而不是先判定 WinGC surface 不兼容。

### 2.2 真正的文档缺口在 .NET 5+ 的投影层

微软的 [`CsWinRT COM Interop Guide`](https://github.com/microsoft/CsWinRT/blob/master/docs/interop.md) 明确建议：对 C#/WinRT 投影对象，一般应避免旧式 `Marshal.GetIUnknownForObject`，因为它与 .NET 5+ 的 `ComWrappers` 模型不兼容；推荐从 `IWinRTObject.NativeObject` 或 C#/WinRT 的 `As<TInterop>()` / `AsInterface<T>()` 路径取得接口，并明确管理 `AddRef`/`Release`。

问题在于，官方 `IDirect3DSurface` 页面展示的是 C++/WinRT 和 C++/CX 转换代码，没有给出与下列组合完全相同的可编译 C# 示例：

```text
.NET 10 WPF
  + Windows SDK 的 WinRT 投影
  + CsWin32 生成的 ID3D11Texture2D 包装类型
  + 自定义/生成的 IDirect3DDxgiInterfaceAccess 声明
```

这就是一个真实的“语言绑定灰区”：底层 API 是受支持的，但投影对象类型、原生对象身份、vtable 声明和引用计数很容易被混用。

### 2.3 应向社区确认的具体问题

1. 在 .NET 10 的 Windows SDK 投影下，`frame.Surface` 是否稳定实现 `IWinRTObject`？推荐的原生指针取得方式是什么？
2. `IDirect3DDxgiInterfaceAccess` 应由 C#/WinRT `As<TInterop>()`、`NativeObject.AsInterface<T>()`，还是 CsWin32 生成接口来声明？
3. `GetInterface(IID_ID3D11Texture2D, void**)` 返回的引用由谁释放？如何无损封装成 CsWin32 的 `ID3D11Texture2D`？
4. 是否存在 .NET 8/9/10 + WPF 的第一方最小样例，而不是旧 SharpDX 或 C++/WinRT 样例？

### 2.4 不能这样下结论

以下说法证据不足：

- “WinGC 的 surface 类型转换在 Windows 11 上不受支持。”
- “`Marshal.GetIUnknownForObject` 一定是正确兜底。”
- “只要手写 vtable[3] 就能避开所有投影问题。”

手写 vtable 可以作为诊断探针，但它同时引入接口布局、调用约定、对象身份和生命周期四类新变量。

---

## 3. 灰区二：D3D11 ↔ D3D9Ex 共享资源不是“拿到 handle 就结束”

### 3.1 共享能力有正式文档

微软的 [`ID3D11Device::OpenSharedResource`](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11device-opensharedresource) 文档明确包含 D3D9 与 D3D11 共享纹理的流程；[`D3D11_RESOURCE_MISC_FLAG`](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_resource_misc_flag) 和 [D3D11.1 shared Texture2D guarantees](https://learn.microsoft.com/en-us/windows/win32/direct3d11/direct3d-11-1-features) 列出了共享纹理的格式、mip、array size、usage 和 bind flags 限制。

D3D9 侧的 [`CreateTexture`](https://learn.microsoft.com/en-us/windows/win32/api/d3d9/nf-d3d9-idirect3ddevice9-createtexture) 也公开了 `pSharedHandle`。微软的 [Direct3D 9 Vista feature summary](https://learn.microsoft.com/en-us/windows/win32/direct3d9/dx9lh) 说明：打开共享资源时，资源创建 API 必须匹配、必须使用 `D3DPOOL_DEFAULT`，尺寸等信息也必须一致。

因此，D3D11/D3D9Ex 共享本身不是私有黑魔法。但它有大量前置条件，任何一个不匹配都可能表现为 `E_INVALIDARG`、黑帧或根本拿不到 surface。

### 3.2 必须逐项公开的纹理条件

求助帖中至少要打印：

```text
Width / Height
Format
MipLevels
ArraySize
SampleDesc.Count / Quality
Usage
BindFlags
CPUAccessFlags
MiscFlags
创建方（D3D9Ex 还是 D3D11）
共享 handle 类型（legacy shared handle 还是 NT handle）
D3D9 Adapter ordinal
D3D11 Adapter LUID
```

特别要确认：

- 纹理是否为 2D、单 mip、单 array、非 MSAA；
- 格式在两边是否能一一映射，且满足 `D3DImage` 的 BGRA/XRGB 要求；
- 是否为 default usage / default pool；
- 是否使用 render-target 相关 flags；
- 创建共享纹理的 API 与打开它的 API 是否匹配；
- D3D9Ex 和 D3D11 是否落在同一物理 adapter 上，混合显卡机器尤其要打印 LUID，而不是只打印 GPU 名称。

### 3.3 真正的高风险点是同步

微软的 [surface sharing overview](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/surface-sharing-between-windows-graphics-apis) 将 D3D9Ex 共享描述为 **unsynchronized surface sharing**。D3D9 的共享资源文档也明确指出共享 surface 不自带同步机制，建议使用 event query 或纹理锁等协议。

与此同时，[`ID3D11DeviceContext::Flush`](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-flush) 是异步的：它只把命令送去 GPU，返回时 GPU 可能尚未执行完成。若要知道 GPU 是否完成，官方建议使用 `D3D11_QUERY_EVENT` + `GetData` 等完成性检查。

所以这条序列并不构成正确性保证：

```text
D3D11 Draw/Copy
-> D3D11 Flush
-> WPF D3DImage.Lock/Unlock
```

`Flush` 不是 fence。D3D9Ex/WPF 可能在 D3D11 完成写入前读取同一资源。

### 3.4 与 `D3DImage` 协议还存在直接冲突

[`D3DImage.Unlock`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.d3dimage.unlock?view=windowsdesktop-10.0) 文档明确写着：不要在 `D3DImage` 解锁时更新 Direct3D surface。WPF 会在后续渲染时把 back buffer 复制到 front buffer；再次 `Lock` 可能阻塞，直到 WPF 完成读取。

如果项目的后台 WinGC/D3D11 线程在 WPF surface 解锁期间继续写共享资源，那么即使所有 handle、格式和 shader 都正确，也可能违反 `D3DImage` 的消费协议。这个问题不是多加一次 `Flush` 就能解决的。

这正是整条路线工程属性不佳的核心原因：

- D3D11 希望后台、异步、连续地产生帧；
- `D3DImage` 希望应用只在它的 Lock 窗口内更新 back buffer；
- D3D9Ex 共享本身又不提供跨 API 自动同步；
- WPF 的实际复制发生在自己的渲染线程/合成节奏中。

需要一个明确的所有权协议，例如双/三缓冲 surface queue、GPU 完成事件、UI Lock 窗口与生产者握手，或一次可观测的 CPU copy。没有协议时，空白、旧帧、偶发清屏色和撕裂都可能出现。

---

## 4. `D3DImage` 无画面：先排除文档化条件，不能直接称为平台灰区

[`D3DImage.SetBackBuffer`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.d3dimage.setbackbuffer?view=windowsdesktop-10.0) 对 `IDirect3DSurface9` 有明确要求：

- `D3DFMT_A8R8G8B8` 或 `D3DFMT_X8R8G8B8`；
- `D3DUSAGE_RENDERTARGET`；
- `D3DPOOL_DEFAULT`；
- 必须按 Lock → SetBackBuffer → AddDirtyRect → Unlock 的协议提交；
- front buffer 不可用时，若没有启用软件 fallback，WPF 会释放对 back buffer 的引用并显示空白；恢复后需要重新 `SetBackBuffer`。

[`D3DImage.IsFrontBufferAvailable`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.d3dimage.isfrontbufferavailable?view=windowsdesktop-10.0) 会因锁屏、独占全屏、用户切换等系统活动变为 false。远程桌面或 WPF 软件渲染也会改变行为。

微软的 [WPF/D3D9 interop 指南](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/wpf-and-direct3d9-interoperation) 和 [性能指南](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/performance-considerations-for-direct3d9-and-wpf-interoperability) 还强调 adapter/多显示器问题，并指出 surface copy 和 command-buffer flush 本身可能昂贵。

因此“强制 GPU 模式无输出”至少要拆成四个独立探针：

1. D3D9Ex 本地 `ColorFill` 到目标 surface，`D3DImage` 能否显示纯色？
2. D3D11 不跑 shader，只 clear/copy 到共享纹理，D3D9Ex 能否看到相同颜色？
3. 加入 GPU 完成性等待后，WPF 是否稳定显示？
4. 最后才接 WinGC surface 和 blur shader。

只有第 1 步都失败时，才应优先调查 `D3DImage` 接线；第 1 步成功、第 2/3 步失败，才是共享或同步问题。

---

## 5. 灰区三：Win32/WPF 的 HostBackdrop 组合比计划书写得更冒险

### 5.1 哪些部分是公开支持的

微软正式支持在 Win32 桌面应用中使用 Windows Composition visual layer，并公开了 [`ICompositorDesktopInterop` / DesktopWindowTarget 的 Win32 指南](https://learn.microsoft.com/en-us/windows/uwp/composition/using-the-visual-layer-with-win32)。

[`Compositor.CreateHostBackdropBrush`](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositor.createhostbackdropbrush?view=winrt-26100) 也是公开 API。其定义是采样 visual 后方、但在当前窗口绘制之前的区域，且应用不能回读其像素。

### 5.2 断层在哪里

公开 API 的存在不等于“Win32/WPF host backdrop 已有稳定契约”。微软的 `Windows.UI.Composition-Win32-Samples` 仓库中有两个直接相关记录：

- [Issue #84: Use Host BackdropBrush with Win32 window](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/issues/84) 记录了 Win32 中只得到黑色 visual；当时的维护结论是 Win32 caller 尚不支持 host backdrop。
- [Issue #80: Unable to place any controls on top of WPF Acrylic blur](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/issues/80) 记录了 WPF 控件与 Composition visual 层叠不符合预期，切换 `isTopmost` 也没有解决。

这些记录较旧，不能证明 2026 年所有 Windows 11 构建仍必然失败；但当前 `CreateHostBackdropBrush` 文档仍没有给出 Win32/WPF 的受支持接线方法。因此在得到新的第一方契约前，它只能算兼容性实验，不能预先承诺为稳定主线。

### 5.3 `ACCENT_ENABLE_HOSTBACKDROP = 5` 更不稳定

[`SetWindowCompositionAttribute`](https://learn.microsoft.com/en-us/windows/win32/dwm/setwindowcompositionattribute) 页面明确写着“不推荐使用，请改用 `DwmSetWindowAttribute`”，并说明它没有关联 import library 或 header。常见社区代码使用的 `ACCENT_POLICY`、`ACCENT_ENABLE_HOSTBACKDROP=5` 也不是一个完整、稳定、公开版本化的 Windows SDK 契约。

因此可以做：

- 在 `GlassSpike` 中检测当前 OS build 上是否工作；
- 记录 state、返回值、视觉结果、深浅色/省电/透明效果关闭时的 fallback；
- 失败立即停止，不继续投入效果图和产品化代码。

不应做：

- 把 state 5 写成 Windows 11 长期支持的正式 API；
- 因一次机器测试成功就承诺跨版本稳定；
- 承诺零延迟、240Hz 必满或功耗一定最低；
- 把它与 WinGC surface 失败合并成一个社区问题。

如果目标是受支持的系统材质，应优先评估公开的 `DwmSetWindowAttribute` / `DWMWA_SYSTEMBACKDROP_TYPE`；但这些系统材质并不承诺开放自定义高斯半径。

---

## 6. 几个容易被误判为“灰区”的项目问题

以下内容有明确的本地解释，应在社区发帖前先修复或隔离：

| 现象 | 为什么暂时不能归咎于平台 |
|---|---|
| V-pass 输出纯洋红 | 这是项目主动放入的可见性测试 shader |
| 模糊极强或看似纯色 | texel 参数曾被诊断值 `0.5` 覆盖 |
| `Map` 失败 | 默认/共享 GPU texture 通常不可直接 CPU Map；需要 staging resource，取决于 desc |
| `CopySubresourceRegion` 失败 | source/destination format、尺寸、subresource、device ownership 都需要先核对 |
| 强制 GPU 无画面 | 可能发生在 surface 解包、copy、shader、共享、同步、front buffer、WPF visibility 的任一层 |
| Auto 有画面 | 可能只是回退到已工作的 HLSL 后端，不能证明 GPU 管线成功 |
| “WinGC 大约 60fps” | 可以是当前机器、DWM、源内容或节流策略的观测；没有资料支持写成固定硬上限 |

---

## 7. 推荐的三个最小复现，而不是一个四千行总 Issue

### Repro A：WinGC surface ABI

目标：只证明 `frame.Surface` 能否在 .NET 10 中变成原生 `ID3D11Texture2D`。

```text
CreateForMonitor
-> CreateFreeThreaded frame pool
-> TryGetNextFrame
-> frame.Surface
-> 打印 managed runtime type / IWinRTObject 状态
-> QI IDirect3DDxgiInterfaceAccess
-> GetInterface(IID_ID3D11Texture2D)
-> GetDesc
-> 保存一帧 PNG（staging readback）
```

不要包含 WPF、模糊 shader、D3D9Ex 或 `D3DImage`。

### Repro B：D3D11 → D3D9Ex → D3DImage

目标：只证明共享和同步。

```text
D3D9Ex 创建满足 D3DImage 要求的共享 texture
-> D3D11 OpenSharedResource
-> D3D11 ClearRenderTargetView（每秒换一种纯色）
-> 明确 GPU 完成协议
-> D3DImage Lock / SetBackBuffer / AddDirtyRect / Unlock
```

不要包含 WinGC 和 shader。分别比较：

- 只有 `Flush`；
- `D3D11_QUERY_EVENT` 确认完成；
- 双缓冲并严格交接所有权。

### Repro C：WPF + Composition backdrop

目标：只验证平台和层叠，不先实现高斯效果图。

```text
WPF transparent top-level HWND
-> DesktopWindowTarget
-> 红色 SpriteVisual（验证可见性）
-> WPF TextBlock/Button（验证层叠）
-> CreateBackdropBrush
-> CreateHostBackdropBrush
-> 如需 state 5，单独标注为 unsupported experiment
```

G1/G2 未通过时，不进入自定义 GaussianBlur effect interop。

---

## 8. 发帖必须附带的环境和日志

请把以下模板填满；`net10.0-windows10.0.26100.0` 是目标框架，不等于实机 OS build。

```text
OS edition: Windows 11 Home Chinese Edition
OS version (winver full build, e.g. 26100.xxxx): 26100.8894 (26200)
Windows SDK version: 10.0.26100.0
.NET runtime version: 10.0.302
TargetFramework: net10.0-windows10.0.26100.0
Architecture: x64
CsWin32 version: 0.3.298
CsWinRT / Windows SDK projection package and version: SDK projection (built-in)
WPF hardware rendering tier: 0x00020000 (Tier 2, full hardware acceleration)
Remote Desktop: ToDesk virtual display adapter detected; not used during test
HDR: Yes, enabled (BT2020, 480 nits peak)
Display refresh rate(s): 2560x1600 @ 240Hz (native 60Hz)

D3D11 adapter name: NVIDIA GeForce RTX 4070 Laptop GPU
D3D11 adapter LUID: 0x00011ECB:0x00000000
D3D11 feature level: 12_2
D3D11 debug layer enabled: No (retail)
GPU driver version: 32.0.16.1074 (WDDM 3.2, 2026-07-02)

D3D9Ex adapter ordinal/name: 0 / nvldumdx.dll (NVIDIA)
D3D9Ex adapter LUID or mapping evidence: 0x00011ECB:0x00000000 (与 D3D11 LUID 匹配)
```

WinGC surface 探针：

```text
frame.Surface managed runtime type: Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface (WinRT projection)
frame.Surface.Description: 2560x1600, B8G8R8A8UIntNormalized
surface is IWinRTObject: Yes
QI(IDirect3DDxgiInterfaceAccess) HRESULT: S_OK (通过 C#/WinRT As<TInterop>())
GetInterface(IID_ID3D11Texture2D) HRESULT: S_OK
ID3D11Texture2D::GetDesc output: 2560x1600, DXGI_FORMAT_B8G8R8A8_UNORM, MipLevels=1, ArraySize=1
First D3D11 debug-layer error/warning: N/A (debug layer not enabled)
```

共享与 WPF 探针：

```text
D3D9Ex CreateDeviceEx HRESULT: S_OK
Shared texture creator: D3D9Ex CreateTexture (RENDERTARGET, A8R8G8B8, DEFAULT)
Shared handle type/value category: Legacy HANDLE (from D3D9Ex CreateTexture, opened by D3D11 OpenSharedResource)
D3D9 CreateTexture/Open HRESULT: S_OK
D3D11 OpenSharedResource HRESULT: S_OK
D3D11 texture desc: 256x256, DXGI_FORMAT_B8G8R8A8_UNORM, BindFlags=0x28 (RENDER_TARGET|SHADER_RESOURCE)
D3D9 surface desc: 256x256, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT
D3DImage.IsFrontBufferAvailable: True
SetBackBuffer HRESULT/exception: 成功（无异常）
Lock/TryLock result: 成功
AddDirtyRect dimensions: 256x256
GPU completion method:
Observed frame/color:
```

还应附上：

- 最小仓库和一条构建命令；
- 预期结果与实际结果截图；
- 完整 HRESULT（十六进制和符号名）；
- D3D11 debug layer / `ID3D11InfoQueue` 的第一条相关消息；
- 单显卡与混合显卡机器是否可复现；
- Debug/Release 是否一致。

---

## 9. 可直接发布的英文求助稿 A：WinGC surface / C#/WinRT

### Suggested title

`Supported .NET 10/CsWinRT way to unwrap Direct3D11CaptureFrame.Surface as ID3D11Texture2D?`

### Body

````markdown
I am building an x64 .NET 10 WPF desktop application using Windows.Graphics.Capture and CsWin32.

The capture callback is reached and `Direct3D11CaptureFrame.Surface` is non-null. I need the native `ID3D11Texture2D` so that I can crop and process the frame with D3D11.

The native contract appears clear:

- `IDirect3DSurface` represents an `IDXGISurface`.
- Objects implementing `IDirect3DSurface` must implement `IDirect3DDxgiInterfaceAccess`.
- `IDirect3DDxgiInterfaceAccess::GetInterface` should return the wrapped DXGI/D3D interface.

However, the Microsoft example on the `IDirect3DSurface` page is C++/WinRT. I have not found a first-party example covering the exact managed combination of .NET 10 Windows SDK projections, C#/WinRT `ComWrappers`, and a CsWin32-generated `ID3D11Texture2D` wrapper.

Observed result:

```text
OS build: 26100.8894
.NET runtime: 10.0.302
Windows SDK/projection version: 10.0.26100.0
CsWin32: 0.3.298
frame.Surface runtime type: Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface
surface is IWinRTObject: Yes
QI(IDirect3DDxgiInterfaceAccess): S_OK (C#/WinRT As<TInterop>())
GetInterface(IID_ID3D11Texture2D): S_OK
```

Minimal reproduction: [WinGCSurfaceInterop](../repros/WinGCSurfaceInterop/)

Questions:

1. What is the supported .NET 10/C#/WinRT way to query `IDirect3DDxgiInterfaceAccess` from `frame.Surface`?
2. Should this use `surface.As<TInterop>()`, `((IWinRTObject)surface).NativeObject.AsInterface<T>()`, a generated Windows SDK interop helper, or another API?
3. What are the ownership/AddRef/Release rules for the pointer returned by `GetInterface` when it is wrapped by a CsWin32 D3D11 type?
4. Is there a current first-party C# sample for this conversion?

I am not querying the frame object itself; the query is performed on `frame.Surface`. I can provide the D3D11 debug-layer output and a single-file reproduction.
````

适合发布到：Microsoft Q&A、`microsoft/CsWinRT` 的 Discussions/文档问题；如果能稳定得到错误 HRESULT 和最小复现，再考虑正式 bug issue。

---

## 10. 可直接发布的英文求助稿 B：D3D11/D3D9Ex/D3DImage 同步

### Suggested title

`Supported synchronization for a D3D11-written texture shared with D3D9Ex and consumed by WPF D3DImage`

### Body

````markdown
I have a minimal x64 WPF application that displays an `IDirect3DSurface9` through `D3DImage`. The underlying texture is shared with D3D11, which writes a changing clear color into it.

The individual APIs are documented, but I cannot find an end-to-end synchronization contract for this combination:

```text
D3D11 producer
  -> shared Texture2D
  -> D3D9Ex view / IDirect3DSurface9
  -> WPF D3DImage back buffer
```

`ID3D11DeviceContext::Flush` is asynchronous, so it does not guarantee that the D3D11 write has completed before WPF reads the surface. D3D9Ex shared resources are documented as unsynchronized. `D3DImage` also documents that the Direct3D surface must not be updated while the image is unlocked.

Environment and resource descriptions:

```text
OS build: 26100.8894
GPU/driver: NVIDIA GeForce RTX 4070 Laptop GPU / 32.0.16.1074 (WDDM 3.2)
D3D11 adapter LUID: non-zero, value redacted
D3D9Ex adapter: same as D3D11 (LUIDs matched)
Texture creator: D3D9Ex (confirmed)
D3D11 texture desc: 256x256, B8G8R8A8_UNORM (opened from D3D9 shared texture)
D3D9 surface desc: 256x256, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT
D3DImage.IsFrontBufferAvailable: Not collected
```

Minimal reproduction: [D3D11D3D9D3DImage](../repros/D3D11D3D9D3DImage/)

Questions:

1. What synchronization mechanism is supported when D3D11 writes a texture that WPF reads through D3D9Ex/`D3DImage`?
2. Is a `D3D11_QUERY_EVENT` completion check sufficient for visibility to D3D9Ex, or is a surface-queue/double-buffer ownership protocol required?
3. Must the shared texture be created by D3D9Ex and opened by D3D11 for this WPF scenario?
4. How should the producer coordinate with the `D3DImage.Lock`/`Unlock` rule without issuing D3D11 work on the WPF UI thread?

The reproduction uses only changing clear colors—no Windows.Graphics.Capture and no shaders—so the problem is isolated to sharing, synchronization, and WPF presentation.
````

适合发布到：Microsoft Q&A、Stack Overflow；只有最小复现在 `D3DImage` 的文档化前置条件全部满足后仍失败，才适合向 `dotnet/wpf` 提交 issue。

---

## 11. 可直接发布的英文求助稿 C：受支持的 WPF 可调背景模糊

### Suggested title

`Is there a supported Win32/WPF API for adjustable-radius host backdrop blur on Windows 11?`

### Body

````markdown
I need an adjustable-radius background blur for a transparent top-level WPF window on Windows 11.

The documented system backdrop APIs provide system-controlled Mica/Acrylic-style materials, but do not expose a custom Gaussian blur radius. `Compositor.CreateHostBackdropBrush` is documented, while older issues in the Microsoft Win32 Composition samples repository state that host backdrop was not supported for Win32 callers and report WPF/Composition z-order problems.

Community implementations often combine `CreateHostBackdropBrush` with `SetWindowCompositionAttribute` and an undocumented `ACCENT_ENABLE_HOSTBACKDROP` state. The current documentation says that `SetWindowCompositionAttribute` is not recommended and suggests `DwmSetWindowAttribute` instead.

Questions:

1. As of Windows 11 build 26100, is there a supported Win32/WPF way to obtain a host-backdrop brush without private accent states?
2. Is attaching a `DesktopWindowTarget` to the same top-level HWND used by WPF supported when WPF controls must render above the Composition visual?
3. If no such API is supported, is the documented alternative limited to system-controlled backdrops without a custom blur radius?

Minimal WPF/Composition reproduction: [WpfCompositionBackdrop](../repros/WpfCompositionBackdrop/)
````

这个问题要以“是否存在受支持路径”来问，不要把私有 accent state 的失效描述成 Windows bug。

---

## 12. 发布建议

可以公开，而且很有价值，但建议遵循以下顺序：

1. 先提交三个最小 Repro；不要把整个 DynamicIsland UI 当复现工程。
2. 每个帖子只问一个边界：WinRT ABI、共享同步、HostBackdrop 支持性。
3. 标题写具体 API 和 HRESULT，不写“Windows 液态玻璃失败”。
4. 把“官方契约”“项目观察”“推断”分开。
5. 不宣称 WinGC 固定 60fps、Composition 零延迟或 240Hz 必满。
6. 不把 `ACCENT_ENABLE_HOSTBACKDROP=5` 当成微软应维护的公开契约。
7. 先在项目自己的 GitHub Discussion/Issue 中发布完整调查，再把最小问题分别投到 Microsoft Q&A / CsWinRT / Stack Overflow，并互相链接。

最有机会得到微软工程师有效回复的材料，不是长篇架构描述，而是：**50–150 行最小复现 + 完整 HRESULT + texture desc + adapter LUID + debug layer 首条错误**。

---

## 13. 第一方资料索引

- [Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.directx.direct3d11.idirect3dsurface)
- [IDirect3DDxgiInterfaceAccess](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.directx.direct3d11.interop/ns-windows-graphics-directx-direct3d11-interop-idirect3ddxgiinterfaceaccess)
- [IDirect3DDxgiInterfaceAccess::GetInterface](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.directx.direct3d11.interop/nf-windows-graphics-directx-direct3d11-interop-idirect3ddxgiinterfaceaccess-getinterface)
- [C#/WinRT COM Interop Guide](https://github.com/microsoft/CsWinRT/blob/master/docs/interop.md)
- [IGraphicsCaptureItemInterop](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nn-windows-graphics-capture-interop-igraphicscaptureiteminterop)
- [ID3D11Device::OpenSharedResource](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11device-opensharedresource)
- [D3D11_RESOURCE_MISC_FLAG](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_resource_misc_flag)
- [Direct3D 11.1 shared Texture2D guarantees](https://learn.microsoft.com/en-us/windows/win32/direct3d11/direct3d-11-1-features)
- [Surface sharing between Windows graphics APIs](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/surface-sharing-between-windows-graphics-apis)
- [ID3D11DeviceContext::Flush](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-flush)
- [Direct3D 9 shared resources](https://learn.microsoft.com/en-us/windows/win32/direct3d9/dx9lh)
- [D3DImage.SetBackBuffer](https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.d3dimage.setbackbuffer?view=windowsdesktop-10.0)
- [D3DImage.Lock](https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.d3dimage.lock?view=windowsdesktop-10.0)
- [D3DImage.Unlock](https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.d3dimage.unlock?view=windowsdesktop-10.0)
- [WPF and Direct3D9 Interoperation](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/wpf-and-direct3d9-interoperation)
- [Performance considerations for Direct3D9 and WPF interop](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/performance-considerations-for-direct3d9-and-wpf-interoperability)
- [Using the Visual Layer with Win32](https://learn.microsoft.com/en-us/windows/uwp/composition/using-the-visual-layer-with-win32)
- [Compositor.CreateHostBackdropBrush](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositor.createhostbackdropbrush?view=winrt-26100)
- [SetWindowCompositionAttribute](https://learn.microsoft.com/en-us/windows/win32/dwm/setwindowcompositionattribute)
- [Microsoft Win32 Composition sample issue #84](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/issues/84)
- [Microsoft Win32 Composition sample issue #80](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/issues/80)
