# DynamicIsland GPU · 阶段 1C 实机观察报告

> **日期**：2026-07-20  
> **依据计划**：`DynamicIsland_GPU_EXECUTION_PLAN_V2.md`  
> **规划者决策**：`STAGE1C_PLANNER_DECISION.md`  
> **本轮性质**：GPU 前半管线分段诊断

---

## 1. 修改文件

- `GpuGlassBackend.cs` — 新增分段诊断字段、扩大异常边界、强制 GPU 保留现场、`CopySubresourceRegion` 的 COM 指针转换适配
- `PLAN_LOG.md` — 追加阶段 1C 日志

---

## 2. 构建

```
dotnet clean → dotnet build
0 错误，2 警告（CS9191，与阶段 1B 相同）
```

---

## 3. 静态确认

| 检查项 | 结果 |
|---|---|
| 正确 IID 保留 | ✅ `A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1` |
| PresentOnUI 未修改 | ✅ Lock/SetBackBuffer 顺序不变 |
| Auto 仍可回退 | ✅ `RegisterEmpty` 中 Auto 模式触发 `FallbackRequested` |
| 强制 GPU 保留现场 | ✅ `hold` 增长，不触发回退 |
| 仅 GpuGlassBackend.cs 修改运行时 | ✅ |

---

## 4. 10 秒实机原始诊断行（最终轮）

```
当前生效：GPU 硬件加速 (r=0.8, 296x59, mapFail=0, present=0,
  frame=411, wgc=411, wgcFail=0, cast=0, hr=0, lastHr=0, nativeQiHr=0,
  surfType=?,
  size=411, copy=0, blur=0, flush=0, queue=0,
  renderFail=411,
  stage=copy, fail=InvalidCastException,
  renderHr=-2147467262, hold=137)
```

---

## 5. 分段诊断结果

| 计数 | 值 | 含义 |
|---|---|---|
| `wgc=411` | WinGC 回调正常触发 | ✅ |
| `cast=0, hr=0` | 表面解包 100% 成功 | ✅ IID 修正确认 |
| `size=411` | 全部通过尺寸/资源检查 | ✅ |
| **`copy=0`** | **0 帧完成纹理复制** | ❌ **第一处失败** |
| `blur=0, flush=0, queue=0, present=0` | 后续从未执行 | — |
| `renderFail=411` | 全部失败 | ❌ |
| `stage=copy` | 失败阶段 = 纹理复制 | 🔍 |
| **`fail=InvalidCastException`** | **异常类型 = InvalidCastException** | 🔍 |
| **`renderHr=-2147467262`** | **0x80004002 = E_NOINTERFACE** | 🔍 |
| `hold=137` | 强制 GPU 阻止了 137 次回退 | ✅ 保留现场生效 |

---

## 6. 肉眼观察

| 项目 | 结果 |
|---|---|
| 10 秒后仍显示 GPU 后端 | ✅ 是 |
| 洋红 | ❌ 无（管线未走到 shader） |
| 真实桌面/模糊内容 | ❌ 无 |
| 卡顿/崩溃 | ❌ 无 |

---

## 7. 根因分析

### 第一处失败位置

**`GpuGlassBackend.OnFrameArrived` → `CopySubresourceRegion` 调用**

### 失败原因

`frameTex` 对象来自 `WinGCCapture.OnFrameArrived` 中的 `Marshal.GetObjectForIUnknown(texPtr)`，其创建的 RCW 在 .NET COM 互操作层**不支持 `ID3D11Resource` 接口转换**。

当 CsWin32 的 `CopySubresourceRegion` 方法内部调用 `Marshal.GetComInterfaceForObject(frameTex, typeof(ID3D11Resource))` 时，抛出 `InvalidCastException`（`HResult=0x80004002`）。

### 尝试过的修复方案

| 方案 | 结果 | 原因 |
|---|---|---|
| `Marshal.QueryInterface` 获取 `ID3D11Resource*` → `Marshal.GetObjectForIUnknown` | `InvalidComObjectException` | `ID3D11Resource*` 指针不能当 `IUnknown*` 传给 `GetObjectForIUnknown` |
| 调换 Release 顺序 | 同上 | 非引用计数问题 |
| `Marshal.GetObjectForIUnknown(frameUnk)` → `as ID3D11Resource` | `InvalidCastException` | 同一 RCW，CsWin32 内部转换仍失败 |

### 结论

**`WinGCCapture` 传递的 `ID3D11Texture2D` RCW 在 COM 互操作层有缺陷。** `GpuGlassBackend` 侧无法绕过。修复需要修改 `WinGCCapture.cs` 的 `FrameArrived` 事件参数类型。

---

## 8. 建议修复方向

修改 `WinGCCapture.cs`：
- 将 `FrameArrived` 事件参数从 `ID3D11Texture2D` 改为 `ID3D11Resource`
- 在 `GetInterface` 成功后，从 `texPtr` 手动 QI 出 `ID3D11Resource*` 并创建 RCW 传出
- 同步修改 `GpuGlassBackend.OnFrameArrived` 签名

---

## 9. 停止条件检查

| 计划第 8 节停止条件 | 触发？ |
|---|---|
| 需修改 GpuGlassBackend.cs 以外文件 | ✅ **是（需改 WinGCCapture.cs）** |
| 新增构建错误/警告 | ❌ |
| `hold` 不增长 | ❌ |
| 崩溃/黑屏 | ❌ |
| 诊断矛盾 | ❌ |
| 枚举无法识别 | ❌ |

**结论：修复需要修改 `WinGCCapture.cs`，超出阶段 1C 范围。交规划者决策。**

---

## 10. 附：当前诊断计数映射

| 字段名 | 对应代码 | 含义 |
|---|---|---|
| `size` | `_sizeOk` | 通过尺寸/资源检查帧数 |
| `copy` | `_copyOk` | 完成 `CopySubresourceRegion` 帧数 |
| `blur` | `_blurReturn` | 完成 `GpuBlur.Blur` 帧数 |
| `flush` | `_flushOk` | 完成 `Flush` 帧数 |
| `queue` | `_queueOk` | 完成 `BeginInvoke(PresentOnUI)` 次数 |
| `renderFail` | `_renderFail` | 渲染线程异常帧数 |
| `stage` | `_lastStage` | 当前/最后执行阶段 |
| `fail` | `_lastFail` | 最后失败原因（异常类型或阶段名） |
| `renderHr` | `_lastRenderHr` | 最后异常 HResult |
| `hold` | `_fallbackSuppressed` | 强制 GPU 阻止回退次数 |