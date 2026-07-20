# DynamicIsland GPU 液态玻璃 · 模糊半径失效排查交接文档

> 交接对象：Fable5 / 下一位排查者
> 写于：2026-07-20
> 一句话现状：GPU 模式下模糊半径滑块完全无效，即便着色器已改成读 cbuffer、即便 C# 把 texel 写死成 0.5 的诊断 hack，模糊依旧不变。根因尚未定位，但已排除一大批可能性。

---

## 0. TL;DR

- **现象**：DynamicIsland 设置里抓屏后端选 GPU（或 Auto 落到 GPU），胶囊液态玻璃的"模糊半径"滑块拖动**完全无反应**。GPU 模式下玻璃一直显示一个**固定的中等模糊**，从不随半径/底色/任何参数变。
- **已查实**（见 §6）：着色器已正确读 cbuffer（`cb0`），且新着色器**确实已嵌入编译产物 DLL**，C# 侧 cbuffer 创建/绑定/更新链路看起来全对，编译 0 错 0 警。
- **仍未解**：为什么 texel 写死 0.5（应产生极强模糊）也没效果。
- **第一件事（成本为零）**：打开设置窗，看"当前生效"那行文字，形如 `当前生效：GPU 硬件加速 (r=12.0, 720x240, mapFail=N)`。`mapFail` 计数和后端名直接决定下一步方向（见 §7）。

---

## 1. 项目背景

DynamicIsland 是一个 WPF（.NET 10）桌面灵动岛应用。核心视觉特性是"液态玻璃"：实时抓取岛后方的屏幕内容，做高斯模糊，作为胶囊背景显示，模拟苹果液态玻璃质感。

液态玻璃有两套后端（`LiquidGlass/IGlassBackend.cs`）：
- **HlslGlassBackend**（基线，可用）：BitBlt 抓屏 + WPF ShaderEffect（`GaussianBlurH/V.ps`，ps_3_0）模糊。缺点：BitBlt 在 UI 线程 ~4.2ms/帧，阻塞 UI，默认 30fps。
- **GpuGlassBackend**（本排查目标）：WinGC 抓屏 + D3D11 模糊 + D3D9/D3D11 共享纹理 + D3DImage 显示。目标：60fps 事件驱动 + UI 零阻塞。**UI 零阻塞已达成**（拖窗/动态壁纸无延迟），但**模糊半径控不动**。

工作目录：`E:\VS Studio Programs\DynamicIsland`
主项目：`DynamicIsland\DynamicIsland.csproj`（TFM `net10.0-windows10.0.26100.0`，x64，AllowUnsafeBlocks，CsWin32 0.3.298）
验证沙盒：`GlassBench\`（独立控制台探针项目）

---

## 2. GPU 玻璃架构

```
后台线程（WinGC FreeThreaded 回调 ThreadPool）
  WinGC monitor 纹理 (frameTex, B8G8R8A8)
    └─ CopySubresourceRegion(岛矩形) ─> inputTex (岛尺寸, D3D11 SRV)
         └─ GpuBlur.Blur:  H-pass(inputTex -> midTex) + V-pass(midTex -> 共享纹理 RTV)
              cbuffer TexelSize(float2) 每帧 Map WRITE_DISCARD 更新
              ├─ VS: FullscreenQuad (SV_VertexID 生成全屏三角形, 无顶点缓冲)
              ├─ PS: GaussianBlurH/V_D3D.cso (ps_4_0, 9-tap σ≈1.5)
              └─ D3D11 Flush (让写对 D3D9 可见, 无 keyed mutex)
                   └─ UI Dispatcher: D3DImage.SetBackBuffer(surf9) + Lock/AddDirtyRect/Unlock
```

关键资源链：
- `_dev11` / `_ctx11`：D3D11 设备 + immediate context（**未加 D3D11_CREATE_DEVICE_FLAG.Multithreaded**，但 _ctx 只从后台 FrameArrived 线程访问，UI 线程只碰 _d3dImage，实测无竞态）
- `_capture` (WinGCCapture)：FreeThreaded 抓屏，复用 _dev11
- `_blur` (GpuBlur)：双遍高斯模糊
- `_interop` (D3D11Interop)：D3D9 建 + D3D11 开的共享纹理 + RTV + D3D9 surface（给 D3DImage）
- `_inputTex`：岛尺寸中间纹理（裁剪用）

---

## 3. 关键文件

| 文件 | 作用 |
|---|---|
| `LiquidGlass/GpuGlassBackend.cs` | GPU 后端主体。`OnFrameArrived`(186) 后台管线；`PresentOnUI`(246) UI 呈现；`UpdateSettings`(149) 接收半径/底色；`Name`(68) 含 mapFail 诊断串 |
| `LiquidGlass/GpuBlur.cs` | D3D11 双遍模糊。`Configure`(68) 记字段；`Blur`(79) 渲染线程落地；`UpdateTexel`(155) cbuffer 更新（**tx=0.5 诊断 hack 在 159**）；`CreateConstantBuffer`(209) DYNAMIC+WRITE |
| `LiquidGlass/D3D11Interop.cs` | D3D9 建 + D3D11 开 共享纹理 + RTV + surf9 |
| `LiquidGlass/WinGCCapture.cs` | WinGC FreeThreaded 抓屏，frame.Surface 取 ID3D11Texture2D |
| `LiquidGlass/IDesktopCapture.cs` | 抓屏抽象 |
| `LiquidGlass/IGlassBackend.cs` / `HlslGlassBackend.cs` | 后端接口 + Hlsl 基线 |
| `LiquidGlassRenderer.cs` | 门面。`CreateAndStart`(66) 选后端 + Auto 回退；`BackendName`(25) |
| `LiquidGlass/Shaders/GaussianBlurH_D3D.hlsl` + `.cso` | H-pass 着色器（**已换成 cbuffer 版**） |
| `LiquidGlass/Shaders/GaussianBlurV_D3D.hlsl` + `.cso` | V-pass 着色器（**已换成 cbuffer 版**） |
| `LiquidGlass/Shaders/FullscreenQuadVS.hlsl` + `.cso` | 全屏三角形 VS |
| `LiquidGlass/Shaders/GaussianBlurH/V.hlsl` + `.ps` | ps_3_0 WPF ShaderEffect 版（Hlsl 后端用，**算法参考基准**） |
| `MainWindow.xaml.cs` | `ApplyUltraEffect`(1113) 彩虹；`GlassBackendName`(90) |
| `SettingsWindow.xaml.cs:237` | "当前生效"文字显示 BackendName（含 mapFail） |
| `DisplaySettings.cs` | `GlassBlurRadius` / `GlassTintIntensity` / `CaptureMode` / `UltraEffectEnabled` |

---

## 4. 改动日志（GPU 玻璃迁移全程）

### P0 spike（2026-07-13 ~ 07-14）
- **BitBlt 实测**（GlassBench, RTX 4070 Laptop 240Hz）：720×240 全流程 avg 4.17ms/p99 5.1ms，BitBlt 本身占 95%，阻塞 UI 线程。结论：BitBlt 不可接受。
- **WinGC 地基**：`D3D11CreateDevice` → `IDXGIDevice` → `CreateDirect3D11DeviceFromDXGIDevice`（在 `d3d11.dll`，不是 interop.dll）→ `IDirect3DDevice` → `IGraphicsCaptureItemInterop.CreateForMonitor`（iid `79C3F95B-31F7-4EC2-A464-632EF5D30760`）→ `Direct3D11CaptureFramePool.CreateFreeThreaded`。
- **帧率证伪**：WinGC 实测 56fps（≈60Hz），240Hz 屏只给 60fps。WinGC API 硬限 60fps。**方案一决策：接受 60fps**（仍优于 BitBlt 30fps + 不阻塞 UI）。
- **方向纠正（重要）**：原计划 D3D11 建 KEYEDMUTEX + D3D9 OpenSharedResource 开。实测 **D3D9 根本没有 OpenSharedResource**（vtable[119] 实为 `SetConvolutionMonoKernel`，旧"空 handle 返回 INVALIDCALL = idx 对"是误读）。**方向必须反过来：D3D9 建共享纹理（CreateTexture pSharedHandle，legacy），D3D11 OpenSharedResource 开**。GlassBench `--shared` 探针闭环通过（洋红回读 65536/65536）。无 KeyedMutex，靠 D3D11 Flush + D3DImage.Lock/Unlock 同步。

### P1-P5 GpuGlassBackend 实现（2026-07-14，代码完成 0 错 0 警）
- 落地主项目（非沙盒）。新增 `IDesktopCapture.cs` / `WinGCCapture.cs` / `GpuBlur.cs` / `D3D11Interop.cs` / `GpuGlassBackend.cs`，重写 `LiquidGlassRenderer.cs`。
- 主项目改 TFM 26100 + CsWin32 0.3.298 + x64 + AllowUnsafeBlocks + NativeMethods.txt。
- 实测：强制 GPU 模式拖窗/动态壁纸几乎无延迟，**UI 零阻塞达成**。

### 第一轮 bug 修复（2026-07-14 实测发现并修）
1. **WinGC 金色边框**：抓屏指示边框（屏幕周围金圈）。修：`session.IsBorderRequired=false` + `IsCursorCaptureEnabled=false`。
2. **Ultra 彩虹变紫**：`RefreshDisplay` 的 mood 边框块没检查 `_ultraActive`，每秒把彩虹覆盖成 Media 紫。修：mood 块加 `if(!_ultraActive)` 守卫。
3. **模糊半径+底色浓度失效（GPU 模式）**：初判根因 D3D11 immediate context 跨线程——`UpdateSettings`(UI) 调 `Configure`→`UpdateTexel`→`_ctx.UpdateSubresource`，`Blur`(后台) 同时用 `_ctx`，跨线程更新丢失。修：`Configure` 改纯字段，midTex 重建+cbuffer 更新推迟到 `Blur` 渲染线程。**底色浓度随后生效，半径仍未生效。**

### 第二轮 bug 修复（2026-07-14）
- **半径**：DEFAULT cbuffer 用 `UpdateSubresource` 更新已绑定 cbuffer 不可靠。改 cbuffer 为 `DYNAMIC` + `CPU_ACCESS_WRITE`，用 `Map(WRITE_DISCARD)` 更新（`GpuBlur.CreateConstantBuffer`/`UpdateTexel`）。
- **彩虹**：`ApplyUltraEffect` 原 `want = UltraEffectEnabled && IsUltraMode`（边框彩虹还要滑块拉满）。改 `want = UltraEffectEnabled`（开关直接控边框彩虹，解耦滑块）。
- 结果：**底色浓度生效，半径仍不生效，彩虹开关仍无彩虹**。

### 第三轮（2026-07-15，诊断中途停滞）
- 在 `GpuBlur.UpdateTexel` 加 tx=0.5 硬编码诊断 hack（测 cbuffer 绑定是否通）。
- 把 `GaussianBlurH/V_D3D.hlsl` 改成洋红诊断版（`return float4(1,0,1,1)`，测管线是否渲染），**但从未编译成 .cso，对运行无效**。
- 记忆停在"待第三轮复测半径+彩虹"。

### 第四轮（2026-07-20，本次）—— 找到半径真根因（部分）
- **反汇编旧 `GaussianBlurH/V_D3D.cso`**（`fxc /dumpbin`）发现：**Resource Bindings 只有 `Sampler s0` + `Input t0`，根本没有 cbuffer！** 9-tap 的 texel 偏移全是编译期字面量 `l(-0.6,0,-0.45,0)…l(0.45,0,0.6,0)` = `offset[-4..4] × 0.15`，即 `radius/width = 0.15` 被写死。
- **这就是半径从未生效的真根因**：C# 侧 `GpuBlur` 无论 `UpdateSubresource` 还是 `Map WRITE_DISCARD`、无论绑 slot 0、无论 tx=0.5 写啥——**着色器一律不看 cbuffer，永远用 0.15**。前两轮的 cbuffer 修复全是徒劳（C# 代码本身没错，错在着色器没接 cbuffer）。
- 旧 `_D3D.hlsl` 源码已被 07-15 洋红编辑覆盖丢失。从 ps_3_0 参考版（`GaussianBlurH/V.hlsl`，9-tap 同核同权值）+ D3D11 约定重建：
  ```hlsl
  Texture2D Input : register(t0);
  SamplerState Sampler : register(s0);
  cbuffer CB : register(b0) { float2 TexelSize; };   // H 读 .x, V 读 .y
  float4 main(float4 pos : SV_POSITION, float2 uv : TEXCOORD) : SV_TARGET {
      float4 color = 0;
      float weight[9] = { 0.016, 0.054, 0.122, 0.184, 0.248, 0.184, 0.122, 0.054, 0.016 };
      float offset[9] = { -4, -3, -2, -1, 0, 1, 2, 3, 4 };
      [unroll] for (int i = 0; i < 9; i++)
          color += Input.Sample(Sampler, uv + float2(offset[i] * TexelSize.x, 0)) * weight[i];
      return color;
  }
  ```
- `fxc /T ps_4_0 /O3 /E main /Fo ...` 编译。反汇编新 .cso 确认：`CB cbuffer cb0` + `dcl_constantbuffer CB0[1], immediateIndexed` + `cb0[0].xxxx` 读 cbuffer。**cb0 == C# slot 0 绑定 ✓**。
- 装进活动 `Shaders/`（.hlsl + .cso 都换）。旧 baked .cso + 重建源码存 `backups/shaders-real-20260720/`。
- **保留 tx=0.5 hack 一轮**做端到端验证（之前因着色器没 cbuffer 一直空转，现在首次有意义）。
- 编译 0 错 0 警。
- **用户实机跑：半径/tx=0.5 仍无效。** ← 当前卡点。

---

## 5. 当前代码状态（活动工作树）

### `GpuBlur.cs`（核心，已读已确认）
- `BlurCB` 结构（26-30）：`{ float TexelSizeX, TexelSizeY, Pad0, Pad1 }` = 16 字节，`[StructLayout(LayoutKind.Sequential)]`。
- `Configure`(68-76)：UI 线程调，只设字段 `_cfgW/_cfgH` + `Volatile.Write(_texelX/_texelY)`，**不碰 _ctx**。
- `Blur`(79-125)：渲染线程。先 `EnsureMidTex`（尺寸变时重建 midTex），再 `UpdateTexel(Volatile.Read texelX, Volatile.Read texelY)`，然后 H-pass + V-pass。cbuffer 绑定：
  ```csharp
  _ctx.VSSetConstantBuffers(0, new[] { _cbTexel });   // slot 0
  _ctx.PSSetConstantBuffers(0, new[] { _cbTexel });   // slot 0
  ```
- `UpdateTexel`(155-170)：**tx=0.5 hack 在此**：
  ```csharp
  if (_cbTexel == null) return;
  // ⚠️ 诊断（保留一轮验证）：Map 写死 0.5（极大）...
  tx = 0.5f; ty = 0.5f;
  var cb = new BlurCB { TexelSizeX = tx, TexelSizeY = ty };
  try {
      _ctx.Map(_cbTexel, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, out D3D11_MAPPED_SUBRESOURCE mapped);
      *(BlurCB*)mapped.pData = cb;
      _ctx.Unmap(_cbTexel, 0);
  }
  catch { MapFailCount++; }
  ```
- `CreateConstantBuffer`(209-222)：`ByteWidth=16`，`Usage=DYNAMIC`，`BindFlags=CONSTANT_BUFFER`，`CPUAccessFlags=CPU_ACCESS_WRITE`。
- `MapFailCount`(53)：public 诊断字段，`Name` 串里暴露。

### 着色器（已换 cbuffer 版，反汇编确认）
- 活动 `GaussianBlurH_D3D.cso` / `V_D3D.cso` = 1928 字节 cbuffer 版（`CB cbuffer cb0`，读 `cb0[0].x` / `.y`）。
- **已验证嵌入 DLL**：从 `bin/Debug/.../DynamicIsland.dll` 提取 `DynamicIsland.g.resources` 里的 `gaussianblurh_d3d.cso`，反汇编确认为 cbuffer 版。**不是增量编译没更新资源的问题。**

### `GpuGlassBackend.cs`
- `Name`(68)：`$"GPU 硬件加速 (r={_radius:0.0}, {_texW}x{_texH}, mapFail={_blur?.MapFailCount ?? 0})"`。
- `OnFrameArrived`(186-233)：后台线程，CopySubresourceRegion → Blur → Flush → Dispatcher.BeginInvoke(PresentOnUI)。连续 3 空帧 `RegisterEmpty`→`FallbackRequested`。
- 设备创建(89-91)：`default(D3D11_CREATE_DEVICE_FLAG)` = 无 Multithreaded。

### `LiquidGlassRenderer.cs`
- Auto/Gpu 试 GPU，构造失败或 `FallbackRequested` 回退 Hlsl。`BackendName`(25) 暴露当前后端名。

### 彩虹
- `MainWindow.xaml.cs:1115` `want = DisplaySettings.Instance.UltraEffectEnabled`（已解耦滑块）。
- ⚠️ `SettingsWindow.xaml:363` 描述仍写"需同时将液态玻璃抓屏帧率拉满才生效"——过时文案，与解耦后行为矛盾，待改。

---

## 6. 已验证的事实（别再重复查）

1. ✅ 旧 .cso 无 cbuffer、texel 写死 0.15（`fxc /dumpbin` 反汇编证实）——这是半径历史失效的根因，**已修**。
2. ✅ 新 .cso 有 `cb0`、读 `cb0[0].x`（反汇编证实）。
3. ✅ 新 .cso **已嵌入编译产物 DLL**（从 DLL 的 `DynamicIsland.g.resources` 提取后反汇编证实）——排除"增量编译没重嵌资源"。
4. ✅ C# `BlurCB`(16B) 与 shader `float2`(8B) 兼容（shader 读前 8 字节，pad 忽略）。
5. ✅ C# 绑 cbuffer 到 slot 0 = shader `cb0`（寄存器匹配）。
6. ✅ cbuffer 创建为 `DYNAMIC` + `CPU_ACCESS_WRITE`，`Map(WRITE_DISCARD)` 更新。
7. ✅ 编译 0 错 0 警。
8. ✅ tx=0.5 hack 在 `UpdateTexel` 里、在 Map 写入之前、每帧都调（`Blur` 里 `UpdateTexel` 在 Draw 之前）。

**逻辑推论**：若以上全真，texel=0.5 应让 9-tap 在 ±2.0 UV 采样（远超 [0,1]，clamp 到边缘），产生明显的"边缘拉伸"式极强模糊。用户报告"还是没用"——**说明上述链条某处实际断裂，或观察到的现象不是"不变"而是"变成了不被识别为模糊的边缘拉伸"**。

---

## 7. 排查方向（按优先级）

### ① 先读设置窗"当前生效"（零成本，必做）
打开设置窗，看底部"当前生效"那行（`SettingsWindow.xaml.cs:237`）。分支：

- **显示 `GPU 硬件加速 (r=…, WxH, mapFail=0)`**：GPU 在跑，Map 没抛异常。→ 跳到 ②③。
- **`mapFail > 0`**：`Map` 每帧抛异常被 `catch` 吞，cbuffer 从未更新，shader 读到创建时的零值 → texel=0 → 9-tap 全采样中心点 → **几乎无模糊**（这符合"半径无效"现象！）。→ 跳到 ④。
- **显示 Hlsl 后端名**：GPU 构造失败或连续 3 空帧触发 `FallbackRequested`，已回退 Hlsl。那用户看到的其实是 Hlsl 模糊（Hlsl 半径是工作的，除非 Hlsl 也坏了）。→ 跳到 ⑤。

### ② 若 GPU 激活 + mapFail=0：加一个"cbuffer 读没读"诊断着色器
临时把 `GaussianBlurH_D3D.hlsl` 改成：
```hlsl
cbuffer CB : register(b0) { float2 TexelSize; };
float4 main(float4 pos : SV_POSITION, float2 uv : TEXCOORD) : SV_TARGET {
    return float4(TexelSize.x, TexelSize.x, TexelSize.x, 1);  // 把 texel 灰度可视化
}
```
`fxc /T ps_4_0 /O3 /E main /Fo GaussianBlurH_D3D.cso GaussianBlurH_D3D.hlsl` 编译（注意 Git Bash 下 fxc 的 `/T` 会被 MSYS 路径转换，需 `MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'`），重跑。
- 胶囊变亮灰（0.5）→ cbuffer **被读了**，问题在模糊采样/绑定 RTV 链路。
- 胶囊全黑（0）→ cbuffer **没被读**或 Map 没写入，尽管 mapFail=0（可能 Map"成功"但写到了别的地方，或绑定没生效）。
- 胶囊不变色 → H-pass 根本没执行/没显示，管线更底层断了。

### ③ 若 ② 灰度可视化也不变：怀疑绑定/PSSetConstantBuffers friendly 重载
CsWin32 生成的 `PSSetConstantBuffers(int, ReadOnlySpan/数组)` friendly 重载可能行为异常。可改走原始接口确认。也可在 `Blur` 里 Draw 前立即重新绑一次 cbuffer，并在 `PresentOnUI` 前确认 RTV。

### ④ 若 mapFail > 0：定位 Map 为何抛
`D3D11_MAP_WRITE_DISCARD` 对 DYNAMIC cbuffer 几乎不会失败。可能原因：
- CsWin32 `Map` 签名：生成的 Map 可能把失败 HRESULT 抛成异常（而非返 HRESULT）。检查生成的签名——是否期望 `out D3D11_MAPPED_SUBRESOURCE` 且失败时抛。
- cbuffer 实际创建标志不对（尽管代码写 DYNAMIC+WRITE）——在 `CreateConstantBuffer` 后加断言查 `desc`。
- `_ctx` 在 Map 时已释放（不太可能，`Blur` 入口没查 `_ctx` null，但 `OnFrameArrived` 查了 `_ctx11==null`）。
- 诊断：把 `catch { MapFailCount++; }` 改成 `catch (Exception ex) { MapFailCount++; System.Diagnostics.Debug.WriteLine("Map: " + ex.Message); }`，附加调试器看输出。

### ⑤ 若回退到 Hlsl：GPU 管线底层断了
- `OnFrameArrived` 是否触发？WinGC 是否真的在送帧？可在 `OnFrameArrived` 入口加计数器，在 `Name` 里暴露帧数。
- `RegisterEmpty` 是否频发（窗口矩形读不到、裁剪盒越界、`_inputTex`/`OutputRtv` 为 null）？
- GlassBench `--frame` 探针可独立验证 WinGC 帧率。

### ⑥ 现象学核查：是不是"变了但没认出来"
tx=0.5 产生的不是常规高斯模糊，而是边缘像素拉伸（所有 tap 都 clamp 到纹理边缘）。在窄胶囊（如 200×40）上可能表现为左右边缘颜色被拉满整条，看着像"颜色渐变"而非"模糊"。可对比：把 hack 临时改成 `tx=0.02f`（轻微）vs `tx=0.5f`（极端），看两者是否有可见差异。有差异=绑定通，只是 0.5 的视觉效果特殊；无差异=绑定断。

---

## 8. 如何构建/运行/探查

### 构建
```bash
cd "E:/VS Studio Programs/DynamicIsland"
dotnet build DynamicIsland/DynamicIsland.csproj -c Debug
```

### 运行
跑 `DynamicIsland\bin\Debug\net10.0-windows10.0.26100.0\DynamicIsland.dll`。
设置窗选"液态玻璃 > 抓屏后端 = GPU"（或 Auto）。看"当前生效"行。

### 编译着色器（改 .hlsl 后必须重编 .cso，否则无效）
```bash
FXC="/c/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/fxc.exe"
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'
"$FXC" /T ps_4_0 /O3 /E main /Fo DynamicIsland/LiquidGlass/Shaders/GaussianBlurH_D3D.cso DynamicIsland/LiquidGlass/Shaders/GaussianBlurH_D3D.hlsl
"$FXC" /T ps_4_0 /O3 /E main /Fo DynamicIsland/LiquidGlass/Shaders/GaussianBlurV_D3D.cso DynamicIsland/LiquidGlass/Shaders/GaussianBlurV_D3D.hlsl
# VS 不用重编（已是 .cso，SDK 自带）
```
**改 .cso 后必须 `dotnet build` 重嵌进 DLL**（WPF `<Resource>` 从 DLL 内 pack URI 加载，不从磁盘读）。

### 反汇编 .cso 看绑定
```bash
"$FXC" /dumpbin <file.cso>   # 看 Resource Bindings 有无 cbuffer + cb0
```

### 验证 DLL 内嵌入的 .cso（排除增量编译坑）
```powershell
$dll='...\DynamicIsland.dll'
$asm=[System.Reflection.Assembly]::LoadFile($dll)
$st=$asm.GetManifestResourceStream('DynamicIsland.g.resources')
$rr=New-Object System.Resources.ResourceReader($st)
foreach($e in $rr){ if($e.Key -like '*gaussianblurh_d3d.cso'){ $e.Value.CopyTo([IO.File]::Create('out.cso')) } }
# 然后 fxc /dumpbin out.cso
```

### GlassBench 沙盒探针
```bash
dotnet "GlassBench/bin/Debug/net10.0-windows10.0.26100.0/GlassBench.dll" --<arg>
# --probe: CsWin32 覆盖+签名 dump
# --wingc: GraphicsCaptureItem 创建
# --frame: FramePool 帧率+阻塞
# --shared: D3D9建->D3D11开 共享纹理闭环（含洋红回读校验）
# --d3d9: ⚠️旧探针，vtable[119]=SetConvolutionMonoKernel 非 OpenSharedResource（反例）
```
注意：`dotnet run --project X -- --arg` 传参有坑（`--arg` 没传给程序），直接跑 dll。

---

## 9. 技术坑（必读，别重踩）

### CsWin32 / D3D11 COM
- CsWin32 生成的 D3D11 COM 对象**非 IDisposable**，用 `Marshal.ReleaseComObject` 释放（封装 `Rel()` 辅助）。
- `CreateVertexShader`/`CreatePixelShader` **无 friendly out 重载**（friendly 版丢弃结果），走原始接口 + `ID3D11VertexShader_unmanaged*` + `Marshal.GetObjectForIUnknown` 拿 RCW。
- D3D11 context setter 多为 friendly：`OMSetRenderTargets(rtv[], dsv)`、`RSSetViewports(ReadOnlySpan)`、`VSSetShader(shader, classInstance[])`（2 参，无第三 NumClassInstances）、`PSSetConstantBuffers(int, array)`。
- `D3D11_TEXTURE2D_DESC.BindFlags` 是 `D3D11_BIND_FLAG` 枚举（非 uint），直接赋枚举别 cast。
- DXGI_FORMAT / DXGI_SAMPLE_DESC 在 `Windows.Win32.Graphics.Dxgi.Common`（非 Dxgi）；D3D_FEATURE_LEVEL 在 `.Direct3D`（无 D3D11_FEATURE_LEVEL）。
- `Map` 可能抛异常（失败 HRESULT 转异常），所以 `GpuBlur.UpdateTexel` 用 try/catch + `MapFailCount`。

### WinGC interop（.NET 10 + CsWin32 + SDK.NET 26100）
- TFM `net10.0-windows10.0.26100.0`：SDK.NET 26100 自带 WinRT 投影，**不需要 CsWinRT 包**（CsWinRT 和 SDK.NET 冲突 CS0436 类型重复）。
- 用户机器只装 Windows SDK 10.0.26100.0 UAP（无 19041 UAP）。
- `CreateDirect3D11DeviceFromDXGIDevice` 在 **`d3d11.dll`**（不是 windows.graphics.directx.direct3d11.interop.dll，系统没这 DLL）。
- `GraphicsCaptureItem` 创建用底层 `IGraphicsCaptureItemInterop`（SDK.NET 的 GraphicsCaptureItem 没 CreateFromMonitor）：
  - `IID_IGraphicsCaptureItemInterop = {3628E81B-3CAC-4C60-B7F4-23CE0E0C3356}`，IUnknown 派，vtable: QI(0) AddRef(1) Release(2) CreateForWindow(3) **CreateForMonitor(4)**。
  - `RoGetActivationFactory` 返回 IActivationFactory，要 `Marshal.QueryInterface` 拿真 interop。
  - **CreateForMonitor 的 iid 要 `IGraphicsCaptureItem` IID = {79C3F95B-31F7-4EC2-A464-632EF5D30760}**（不是 IInspectable，不是 typeof(GraphicsCaptureItem).GUID）。
  - RCW：`Marshal.GetTypedObjectForIUnknown`（GetObjectForIUnknown + cast 失败 InvalidCastException）。
- HSTRING：不用 `[MarshalAs(HString)]`（MarshalDirectiveException）。手动 `WindowsCreateString`/`RoGetActivationFactory`/`WindowsDeleteString`（combase.dll），第一参 IntPtr。
- `Direct3D11CaptureFramePool.CreateFreeThreaded` 回调 ThreadPool，不阻塞 UI；普通 Create 回调 UI 线程要消息泵。**WinGC 硬限 60fps**。

### D3D9/D3D11 共享纹理（方向纠正）
- **D3D9（IDirect3DDevice9/9Ex）没有 OpenSharedResource**。vtable[119] 是 `SetConvolutionMonoKernel`，旧"空 handle 返回 INVALIDCALL = idx 对"是误读。
- **正确方向：D3D9 建共享纹理，D3D11 开**：`D3D9 CreateTexture(RENDERTARGET, DEFAULT, &pSharedHandle)` 出 legacy handle → `D3D11 ID3D11Device.OpenSharedResource(handle, IID_ID3D11Texture2D)`。
- **无 KeyedMutex**：D3D9 CreateTexture 不产 KEYEDMUTEX，D3D9 侧无 keyed mutex API。靠 **D3D11 Flush + D3DImage.Lock/Unlock** 同步（WPF D3DImage+D3D11 经典做法）。
- D3D9 共享纹理必须 `D3DPOOL_DEFAULT` + `RENDERTARGET`。
- `DXGI_FORMAT_B8G8R8A8_UNORM` == `D3DFMT_A8R8G8B8`，内存都是 B,G,R,A。
- `OpenSharedResource` 只有 raw 重载；`CreateTexture` 有 friendly `out IDirect3DTexture9` + `HANDLE*`。

### .NET 10 WPF D3DImage
- `D3DImage.SetBackBuffer` 第一参是 **`D3DResourceType`**（不是旧 `Direct3DSurfaceType`，值仍 `IDirect3DSurface9`）。
- `IDirect3DSurface9` 取 D3DImage 指针：`Marshal.GetComInterfaceForObject(surf9, typeof(IDirect3DSurface9))`，SetBackBuffer 后 Release 平衡。

### fxc + Git Bash
- fxc 的 `/T` `/Fo` 等斜杠参数会被 MSYS 路径转换。必须 `export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'`。
- `//T` 双斜杠不行（fxc 报 unknown option），用单 `/T` + 环境变量。

---

## 10. 备份与关联资源

- `backups/shaders-real-20260720/`：旧 baked .cso（1460B，无 cbuffer）+ 重建 cbuffer 版 .recon.cso（1928B）+ 重建 .hlsl 源 + 从 DLL 提取的 embedded_H.cso。
- `backups/DynamicIsland_20260712_213842.zip`：07-12 全量备份（早于 _D3D 着色器）。
- `backups/60帧分歧.zip`：07-13 主项目 + GlassBench。
- 计划文件：`.claude/plans/d3d9ex-glass-migration.md`、`p0-p1-gpu-dpi-foundation.md`、`p2-glass-dpi-wmdpichanged.md`。
- 记忆文件（~/.claude/.../memory/）：`dynamicisland-gpu-migration-critique.md`（本排查全程）、`dynamicisland-wingc-interop-details.md`（WinGC interop 必读）、`dynamic-island-recent-work.md`（项目整体改动）。

---

## 11. 当前未决清单

1. **【主】GPU 模式模糊半径失效**——按 §7 排查。第一步行刑：读设置窗"当前生效"的 `mapFail` 和后端名。
2. tx=0.5 hack 验证通过后，删 `GpuBlur.cs:159` 的 `tx=0.5f; ty=0.5f;` 两行，恢复 `Volatile.Read` 真实半径。
3. `SettingsWindow.xaml:363` 彩虹开关描述过时文案待改（解耦后不再需要拉满帧率）。
4. （可选）GPU 模式 Ultra 彩虹与帧率滑块的 UX 耦合（滑块不控 GPU 帧率但仍控 Ultra 开关）。
5. （可选）补 `DxgiDuplicationCapture` 兼容层。
6. （可选）设置窗显示实际解析后后端名——已部分实现（"当前生效"行），可增强。

---

*本文档由 Claude 整理于 2026-07-20，基于实读代码 + fxc 反汇编 + DLL 资源提取实测。所有"已验证"项均有实测依据。*
