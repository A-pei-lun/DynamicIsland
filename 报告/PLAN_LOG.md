# DynamicIsland GPU 液态玻璃修复 · 执行日志

## 阶段 0 · 基线确认

**日期**：2026-07-20
**改动**：无（零改动，仅构建 + 观察）
**编译结果**：0 错 0 警
**步骤**：
1. `dotnet build` 成功，0 错 0 警
2. 运行程序，设置窗选「液态玻璃 > 抓屏后端 = GPU」
3. 读出设置窗底部「当前生效」整行文字

**用户观察**：
`当前生效：GPU 硬件加速 (r=1.6, 296x59, mapFail=0)`

**判读结论**：
GPU 在跑，Map 正常（mapFail=0），无静默回退

**跳转**：
→ 阶段 1（洋红管线可见性测试）

---

## 阶段 1 · 洋红管线可见性测试

**日期**：2026-07-20
**改动**：把 `GaussianBlurV_D3D.hlsl` 改为无条件输出 `float4(1,0,1,1)` 纯洋红；fxc 编译 → `dotnet build` → 双重验证嵌入
**编译结果**：0 错 0 警
**步骤**：
1. 备份原版 H/V shader 到 `backups/plan-20260720/`
2. 修改 V-pass hlsl 为纯洋红
3. fxc 编译成功（0 错）
4. `dotnet build` 成功
5. fxc /dumpbin 反汇编确认：`mov o0.xyzw, l(1.000000,0,1.000000,1.000000)` ✓
6. PowerShell 提取 DLL 内嵌资源确认：456 bytes（原版 1928 bytes），匹配新 CSO ✓

**用户观察**：
胶囊完全没变，无洋红色出现。仍然显示那层"固定中等模糊"。

**判读结论**：
❌ GPU 输出根本没到用户眼前。之前所有轮次都在错误战场作战。那层模糊很可能来自 DWM 底座。

**跳转**：
→ 分支 B（呈现链路排查）

---

## 分支 B · 步骤 1 — DWM 底座嫌疑实验

**日期**：2026-07-20
**改动**：把 `WindowBackdrop.cs` 中 LiquidGlass 模式的 backdrop 从 `ACCENT_ENABLE_TRANSPARENTGRADIENT` 改为 `ACCENT_DISABLED`（完全关闭 DWM 底座）
**编译结果**：0 错 0 警
**步骤**：
1. 修改 `WindowBackdrop.cs` LiquidGlass case → ACCENT_DISABLED
2. `dotnet build` 成功
3. 用户运行，观察胶囊

**用户观察**：
"应该是全透明"（DWM backdrop 禁用后，胶囊变透明，看不到任何玻璃内容）

**判读结论**：
✅ DWM 底座提供了视觉效果（之前看到的"固定中等模糊"来自 DWM 还是半透明底座暂不明确）
❌ GPU 玻璃渲染层（D3DImage）完全未显示任何内容

**跳转**：
→ 分支 B · 步骤 2 — D3DImage 呈现链路诊断

---

## 分支 B · 步骤 2 — WinGC 帧诊断

**日期**：2026-07-20
**改动**：恢复 DWM 底座 + 在 GpuGlassBackend 和 WinGCCapture 添加帧计数器
**编译结果**：0 错 0 警
**用户观察**：
`r=0.0, 296x59, mapFail=0, present=0, frame=0` — 帧计数器全是 0

**判读结论**：
WinGC 从未送帧到 GpuGlassBackend.OnFrameArrived

**跳转**：
→ 分支 B · 步骤 3 — WinGC 回调细分

---

## 分支 B · 步骤 3 — WinGC 回调细分

**日期**：2026-07-20
**改动**：在 WinGCCapture 内部添加 wgc/wgcFail/cast/hr/lastHr 细分计数器
**编译结果**：0 错 0 警
**用户观察**：
`wgc=754, wgcFail=754, cast=754, hr=0, lastHr=2147467262`

**判读结论**：
✅ WinGC 回调正常触发（wgc 增长）
❌ `surface.As<IDirect3DDxgiInterfaceAccess>()` 失败（cast=wgc）
❌ HRESULT=0x80004002 (E_NOINTERFACE) — IDirect3DSurface 不支持该接口

**跳转**：
→ 分支 B · 步骤 4 — 改用原始 COM QueryInterface 绕过 WinRT 投影层

---

## 分支 B · 步骤 4 — 尝试原始 COM QueryInterface

**日期**：2026-07-20
**改动**：把 `surface.As<>()` 改为 `Marshal.QueryInterface` 直接访问
**编译结果**：0 错 0 警
**用户观察**：
`cast 仍增长, hr=0, lastHr=2147467262`

**结论**：
原始 COM QueryInterface 也返回 E_NOINTERFACE。IDirect3DSurface COM 对象确实不支持 IDirect3DDxgiInterfaceAccess 接口。

**根因**：`CreateDirect3D11DeviceFromDXGIDevice` 创建的 WinRT IDirect3DDevice 与 Direct3D11CaptureFramePool 不兼容，表面帧不支持标准 DxgiInterfaceAccess。

**下一步**：尝试改用 `Windows.Graphics.DirectX.Direct3D11.Direct3D11Device.CreateFromDXGIDevice` 替代 `CreateDirect3D11DeviceFromDXGIDevice` 创建 WinRT 设备。

---

## 分支 1A · 原生 ABI 指针诊断（计划 V2）

**日期**：2026-07-20
**改动**：改用 `IWinRTObject.NativeObject.AsInterface<IDirect3DDxgiInterfaceAccess>()` → `GetInterface(IID_ID3D11Texture2D)`
**编译结果**：0 错 2 警

**修改变量**：
- `WinGCCapture.OnFrameArrived` 解包路径从 `Marshal.GetIUnknownForObject` + 直接 QI `ID3D11Texture2D` 改为 `IWinRTObject` → `AsInterface<IDirect3DDxgiInterfaceAccess>` → `GetInterface`

**用户观察**：
```
当前生效：GPU 硬件加速 (r=0.0, 296x59, mapFail=0, present=0, frame=0,
  wgc=1106, wgcFail=1106, cast=1106, hr=0, lastHr=-2147467262,
  nativeQiHr=-2147467262, surfType=WinRT.IInspectable)
```

**判读**：
- `cast=1106`：`AsInterface<IDirect3DDxgiInterfaceAccess>` 100% 失败
- `nativeQiHr=-2147467262`：原生 ABI 指针 `QueryInterface` 也返回 E_NOINTERFACE
- `surfType=WinRT.IInspectable`：WinRT 投影未正确识别 surface 类型

**结论**：`frame.Surface` 的 COM 对象确实不支持 `IDirect3DDxgiInterfaceAccess`。违反互操作契约。

**命中的计划停止条件**：第 12 节第 1 条 —— `NativeObject.ThisPtr` 原生 QI `IDirect3DDxgiInterfaceAccess` 仍为 E_NOINTERFACE。

**根因**：`CreateDirect3D11DeviceFromDXGIDevice`（d3d11.dll）创建的 WinRT 设备与 `Direct3D11CaptureFramePool` 不兼容，产生的表面帧不是标准 `IDirect3DSurface` 实现。

**下一步**：回规划者。归因完成，修复需超出阶段 1D 范围。

---

## 阶段 1D · Copy 层归因校正

**日期**：2026-07-20
**改动**：删除阶段 1C 临时适配，新增 ProbeResourceQI 探针，Copy 拆为 5 阶段归因
**编译结果**：0 错 4 警（CS9191）

**运行计数**：
```
dstQi=-2146233049, srcQi=0, dstCast=207, srcCast=207, copyCall=0
stage=copy-call, fail=InvalidComObjectException, renderHr=-2146233049
```

**关键发现**：
- `srcQi=0`：WinGC 帧纹理原生支持 ID3D11Resource ✅
- `dstCast=207, srcCast=207`：两个托管转型均成功
- `copyCall=0`：CopySubresourceRegion 内部抛 InvalidComObjectException
- 故障点在 CsWin32 封送内部，不是 WinGC 纹理 RCW 缺陷

**命中决策树**：两个 cast 均增长，stage=copy-call → 进入最小原生调用适配评估

**下一步**：回规划者决策。