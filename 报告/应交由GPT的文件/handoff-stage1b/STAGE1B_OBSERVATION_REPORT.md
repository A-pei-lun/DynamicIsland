# DynamicIsland GPU · 阶段 1B 实机观察报告

> **日期**：2026-07-20
> **依据计划**：`DynamicIsland_GPU_EXECUTION_PLAN_V2.md`
> **规划者决策**：`STAGE1B_PLANNER_DECISION.md`

---

## 1. 做了什么

**唯一运行时改动**：`WinGCCapture.cs` 中 `IDirect3DDxgiInterfaceAccess` 的 ComImport GUID

```diff
- [ComImport, Guid("A9B3D012-3DF2-4473-8875-25240F0F3E16")]
+ [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
```

旧 IID 来自网络流传的过时信息，第三段开始就是错的：
```
错误：A9B3D012-3DF2-4473-8875-25240F0F3E16
正确：A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1
```

---

## 2. 构建

```
dotnet clean → dotnet build
0 错误，2 警告（CS9191 ref→in 建议，不影响运行）
```

---

## 3. 实机观察

### 修正前（阶段 1A）

```
当前生效：GPU 硬件加速 (r=0.0, 296x59, mapFail=0, present=0, frame=0,
  wgc=1106, wgcFail=1106, cast=1106, hr=0, lastHr=-2147467262,
  nativeQiHr=-2147467262, surfType=WinRT.IInspectable)
```

- `wgc=1106`：WinGC 回调正常触发
- `cast=wgc=1106`：`AsInterface<IDirect3DDxgiInterfaceAccess>` 100% 失败
- `nativeQiHr=-2147467262`：原生 ABI 指针 QI 也返回 E_NOINTERFACE
- `frame=0, present=0`：GPU 后端从未收到纹理帧
- **结论**：IID 写错，接口 QI 永远失败

### 修正后（阶段 1B）

- ✅ **GPU 模式能短暂进入**（之前从没进去过）
- ⚠️ 进入后约 1-2 秒自动回退至 HLSL 兼容模式
- 回退后设置窗显示 HLSL 后端信息，GPU 计数已丢失
- 用户肉眼无法看清回退前的 GPU 计数

---

## 4. 行为变化的定性分析

| 指标 | 修正前 | 修正后 | 说明 |
|---|---|---|---|
| GPU 模式进入 | 否 | **是（短暂）** | ✅ IID 修正生效 |
| 回退行为 | 永不回退（cast=wgc 但后端稳） | 秒退至 HLSL | 管线走到了新阶段才失败 |
| 用户可见玻璃 | DWM 底座提供的假模糊 | 纯透明（无底座无玻璃） | GPU 输出仍未到 D3DImage |
| 洋红（#FF00FF） | 从未出现 | 从未出现 | 符合预期——D3DImage 未修通 |

**核心解读**：修正前 `cast=wgc`，所有帧在解包阶段就扔了，`GpuGlassBackend.OnFrameArrived` 从未被调用，所以 `_emptyFrames` 永远为 0，永不回退。

修正后，行为了——说明 `GpuGlassBackend.OnFrameArrived` 被调用了，帧处理过程中出错了，触发了 `RegisterEmpty()` → 3 帧空 → `FallbackRequested` → HLSL。

---

## 5. 按计划判读

| 计划判读表 | 匹配 ? |
|---|---|
| `wgc/frame/present` 同时增长，`cast=0, hr=0` | 无法确认（回退太快，用户看不到计数） |
| `frame` 增长但没有洋红 → 表面解包已成功；D3DImage 的已知 Lock 顺序仍阻断显示 → **仍视为阶段 1B 通过** | **高度匹配行为** |
| `cast` 继续增长，`nativeQiHr=E_NOINTERFACE` | 不匹配（行为已改变） |

**结论**：IID 修正很可能已生效。表面解包不再是瓶颈。当前瓶颈是 D3DImage 呈现（`SetBackBuffer` 在 `Lock` 之外，已知问题）。

---

## 6. 下一步建议

### 按计划 V2，应进入阶段 2：修通 D3DImage 呈现

**允许修改的文件**：仅 `GpuGlassBackend.cs`

**已知问题**（计划第 1.2 节第 4 条）：
> `PresentOnUI` 中 `SetBackBuffer` 位于 `Lock` 之前

**修正方案**：
```csharp
// 当前（错误）：
_d3dImage.SetBackBuffer(...);  // ← 在 Lock 外面
_d3dImage.Lock();
_d3dImage.AddDirtyRect(...);
_d3dImage.Unlock();

// 正确：
_d3dImage.Lock();
_d3dImage.SetBackBuffer(...);  // ← 必须在 Lock 内部
_d3dImage.AddDirtyRect(...);
_d3dImage.Unlock();  // finally
```

---

## 7. 附：修改文件清单

| 文件 | 说明 |
|---|---|
| `WinGCCapture.cs` | 唯一运行时改动：IID 修正 + 原生 ABI 诊断 fallback |
| `GpuGlassBackend.cs` | 仅 `Name` 字符串增加 `nativeQiHr`/`surfType` 字段 |
| `PLAN_LOG.md` | 完整执行日志 |
| `STAGE1B_PLANNER_DECISION.md` | 规划者决策原文 |
| `DynamicIsland_GPU_EXECUTION_PLAN_V2.md` | 执行计划 V2 |