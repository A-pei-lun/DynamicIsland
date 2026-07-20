# DynamicIsland GPU · 阶段 1D 实机观察报告

> **日期**：2026-07-20  
> **依据计划**：`DynamicIsland_GPU_EXECUTION_PLAN_V2.md`  
> **规划者决策**：`STAGE1D_PLANNER_DECISION.md`  
> **本轮性质**：Copy 层归因校正

---

## 1. 修改文件

- `GpuGlassBackend.cs` — 删除阶段 1C 临时适配，新增 `ProbeResourceQI` 探针，Copy 拆为 5 阶段归因
- `PLAN_LOG.md` — 追加阶段 1D 日志

---

## 2. 构建

```
dotnet build
0 错误，4 警告（全部 CS9191，2 来源 WinGCCapture，2 来源 ProbeResourceQI）
```

### CsWin32 版本

```
Microsoft.Windows.CsWin32 0.3.298
```

### 生成接口取证

| 接口 | IID |
|---|---|
| ID3D11Texture2D | `6F15AAF2-D208-4E89-9AB4-489535D34F9C` |
| ID3D11Resource | `DC8E63F3-D12B-4952-B47B-5E45026A862D` |
| IDirect3DDxgiInterfaceAccess | `A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1` |

---

## 3. 静态确认

| 检查项 | 结果 |
|---|---|
| 正确 IID 保留 | ✅ |
| WinGCCapture.cs 未改 | ✅ |
| PresentOnUI 未改 | ✅ |
| 强制 GPU 保留现场 | ✅ |
| 阶段 1C 临时适配已删除 | ✅（`frameUnk`/`GetObjectForIUnknown`/`copy-no-resource` 已清除） |

---

## 4. 10 秒原始诊断行

```
当前生效：GPU 硬件加速 (r=0.8, 296x59, mapFail=0, present=0,
  frame=494, wgc=494, wgcFail=0, cast=0, hr=0, lastHr=0, nativeQiHr=0,
  surfType=?,
  size=207, copy=0, blur=0, flush=0, queue=0,
  renderFail=494,
  stage=copy-call, fail=InvalidComObjectException, renderHr=-2146233049,
  hold=164,
  dstQi=-2146233049, srcQi=0, dstCast=207, srcCast=207, copyCall=0)
```

---

## 5. HRESULT

| 字段 | 十进制 | 十六进制 | 含义 |
|---|---|---|---|
| `dstQi` | -2146233049 | 0x80131557 | `COR_E_INVALIDCOMOBJECT` — `ProbeResourceQI` 对 CsWin32 RCW 抛异常 |
| `srcQi` | 0 | 0x00000000 | **S_OK** — WinGC 帧纹理原生支持 ID3D11Resource |
| `renderHr` | -2146233049 | 0x80131557 | `InvalidComObjectException` 在 Copy 内部 |

---

## 6. 分段诊断结果

| 阶段 | 计数 | 结果 |
|---|---|---|
| `size` | 207 | ✅ 通过 |
| `copy-qi` (dstQi) | -2146233049 | ⚠️ 探针不适合 CsWin32 RCW |
| `copy-qi` (srcQi) | 0 | ✅ **WinGC 纹理原生支持 ID3D11Resource** |
| `copy-dst-cast` | 207 | ✅ `(ID3D11Resource)_inputTex` 转型成功 |
| `copy-src-cast` | 207 | ✅ `(ID3D11Resource)frameTex` 转型成功 |
| `copy-call` | 0 | ❌ **`CopySubresourceRegion` 内部抛 `InvalidComObjectException`** |

---

## 7. 核心结论

### 关键证据：`srcQi=0`

WinGC 帧纹理的**原生 COM 对象确实支持 `ID3D11Resource` 接口**。阶段 1C 报告中"WinGC 纹理 RCW 有缺陷"的说法不成立。QS 返回 S_OK，证明原生契约没有违反。

### 两个托管转换均成功

`dstCast=207` 和 `srcCast=207` 证明 `(ID3D11Resource)_inputTex` 和 `(ID3D11Resource)frameTex` 在 .NET 层面转型成功。

### 故障点在 `CopySubresourceRegion` 调用内部

`copyCall=0`，`stage=copy-call`，`fail=InvalidComObjectException`。两个 `ID3D11Resource` 参数都已成功转换，但方法调用内部仍抛出异常。这可能与 CsWin32 对 `D3D11_BOX` 参数的封送，或 `ID3D11DeviceContext` RCW 的 COM 接口转换有关。

### dstQi 异常说明

`dstQi=-2146233049` 是因为 `ProbeResourceQI` 中的 `Marshal.GetIUnknownForObject(_inputTex)` 在 CsWin32 RCW 上抛出 `InvalidComObjectException`。这不会影响 `(ID3D11Resource)_inputTex` 转型（`dstCast=207` 证明转型成功）。`ProbeResourceQI` 方法对 CsWin32 生成的 RCW 不适用。

---

## 8. 命中决策树

> 两个 cast 均增长，`stage=copy-call` → 对象转换成功，异常发生在调用封送或生成签名 → **进入最小原生调用适配评估，不改 WinGC 事件类型**

---

## 9. 停止条件检查

| 条件 | 触发？ |
|---|---|
| 需修改 WinGCCapture.cs | ❌ 不触发 |
| 崩溃/黑屏 | ❌ 不触发 |
| 诊断矛盾 | ❌ 全部自洽 |
| 构建警告类型增加 | ⚠️ 数量 +2（全部 CS9191） |
| 执行者试图用新方案绕过 | ❌ 未绕过 |

**结论：归因已完成，但修复需要超出阶段 1D 范围。交规划者决策。**

---

## 10. 肉眼观察

| 项目 | 结果 |
|---|---|
| 10 秒后仍显示 GPU 后端 | ✅ |
| 洋红 | ❌ 无 |
| 桌面/模糊内容 | ❌ 无 |
| 卡顿/崩溃 | ❌ 无 |