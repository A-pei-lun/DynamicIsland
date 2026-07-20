# DynamicIsland GPU · 阶段 1E 规划者决策

> 日期：2026-07-20  
> 输入：`stage1d-observation-2026-07-20.zip`  
> 本轮性质：目标纹理 RCW 生命周期单变量证伪；不批准原生 vtable 或互操作库替换

---

## 0. 最终决策

**阶段 1D 已证明 WinGC 源纹理不是当前第一故障。下一轮也不应进入 `D3D11_BOX` 封送或裸指针调用。**

当前最强证据指向目标 `_inputTex` 的 RCW 已被主动释放并与原生对象断开：

```text
srcQi=0
dstQi=-2146233049
dstCast=207
srcCast=207
stage=copy-call
fail=InvalidComObjectException
```

`-2146233049` 的正确十六进制是：

```text
0x80131527 = COR_E_INVALIDCOMOBJECT
```

阶段 1D 报告将其写成 `0x80131557`，这是换算错误。该异常不是 D3D11 驱动返回的 HRESULT，而是 CLR 在使用无效/已断开的 COM 包装对象时抛出的托管异常。

结合源码：

```csharp
Rel(_inputTex);
_inputTex = CreateInputTexture(w, h);
```

尺寸重建会先对旧 RCW 调用 `Marshal.ReleaseComObject`，再创建新纹理。这个顺序存在两个风险：

1. 旧纹理仍可能被当前或并行帧使用，却已被强制断开；
2. 旧原生对象先释放后，D3D11 可能复用其 COM identity 地址；CLR 的 RCW identity 缓存随后可能命中已断开的包装对象。

微软文档明确警告，不恰当地调用 `ReleaseComObject` 会使仍持有该 RCW 的托管代码抛出 `InvalidComObjectException`，执行中的调用甚至可能发生访问冲突。

因此批准阶段 1E：**只取消尺寸重建路径中的主动 `ReleaseComObject(_inputTex)`，保留其他全部管线与诊断。** 这是一轮单变量证伪，不是最终资源管理重构。

---

## 1. 对阶段 1D 回传的判读修正

### 1.1 已成立的结论

- `wgc=494`、`frame=494`：WinGC 回调和纹理提取继续贯通；
- `srcQi=0`：WinGC 源纹理原生支持 `ID3D11Resource`；
- `srcCast>0`：源纹理的托管接口转换没有在 cast 表达式处失败；
- 当前异常发生在 CLR/RCW 边界，尚未进入可由 D3D11 runtime 返回设备错误的阶段；
- 不需要修改 `WinGCCapture.FrameArrived` 的参数类型。

### 1.2 不成立或证据不足的结论

1. **“`ProbeResourceQI` 不适合 CsWin32 RCW”不成立。**

   同一个探针对 `frameTex` 返回 `srcQi=0`。它只对 `_inputTex` 抛 `InvalidComObjectException`，差异更像是目标对象状态不同，而不是探针普遍不兼容。

2. **两个 cast 成功不等于两个 RCW 可用。**

   强制转换语句成功后，CLR 仍可能在方法调用封送阶段提取 COM interface pointer；如果 RCW 已断开，异常正会在这里暴露。因此 `dstCast>0` 不能覆盖 `dstQi=COR_E_INVALIDCOMOBJECT` 这条更直接的证据。

3. **当前没有证据指向 `D3D11_BOX`。**

   `D3D11_BOX` 只是六个 `UINT` 的 blittable 结构，不包含 COM 对象。`InvalidComObjectException` 更直接对应 `_ctx11`、`dstResource` 或 `srcResource` 中的无效 RCW；而现有探针已经明确指出 `dstResource` 对应对象无效。

4. **阶段 1D 计数并非完全自洽。**

   `renderFail=494`，但 `size=207`、`dstCast=207`。这说明至少有 287 次异常发生在 `_sizeOk++` 之前，或回调/诊断字段存在并发交错。不能把最后一个 `stage=copy-call` 当成全部 494 次失败的唯一分布。

这不阻止阶段 1E，因为 `dstQi=0x80131527` 已足以支持一次最小生命周期证伪；但执行者不得把 1D 报告写成“全部失败均由 CopySubresourceRegion 内部封送造成”。

---

## 2. 本轮唯一问题

回答：

> 取消尺寸重建中的主动 `ReleaseComObject(_inputTex)` 后，新建目标纹理是否恢复为有效 RCW，并使 Copy 前进？

本轮不要求看到洋红，也不修 D3DImage。

---

## 3. 修改边界

### 允许修改

- `GpuGlassBackend.cs`
- `PLAN_LOG.md`
- 新增 `STAGE1E_OBSERVATION_REPORT.md`

### 禁止修改

- `WinGCCapture.cs`
- `GpuBlur.cs`
- `D3D11Interop.cs`
- `LiquidGlassRenderer.cs`
- `PresentOnUI`
- shader / `.cso`
- `CopySubresourceRegion` 调用签名和 `D3D11_BOX`
- NuGet、目标框架、CsWin32 版本、项目属性
- 设备创建方式、WinGC 事件类型、DWM 底座

### 明确禁止的新方案

- 裸 `IntPtr` 传入渲染管线；
- 手写 vtable 或 function pointer；
- C++/CLI；
- SharpDX、Vortice 或其他互操作库；
- `Marshal.GetObjectForIUnknown` 再包装目标纹理；
- 加锁、串行队列或整体资源生命周期重构。

如果执行者认为必须触碰以上任一项，立即停止回传。

---

## 4. 规定实现

### 4.1 唯一功能变量：取消 resize 路径的主动释放

在 `UpdateIslandSize` 中，把：

```csharp
Rel(_inputTex);
_inputTex = CreateInputTexture(w, h);
```

改成：

```csharp
_inputTex = CreateInputTexture(w, h);
```

只删除这一处 `Rel(_inputTex)`。

说明：赋值表达式会先完成 `CreateInputTexture`，再覆盖字段；因此旧 `_inputTex` 在新纹理创建期间仍由字段持有，不会在创建前被主动断开。覆盖后旧 RCW 交由 GC 正常回收。本轮允许这项行为用于证伪。

以下位置保持不变：

- `Stop()` 中的 `Rel(_inputTex)`；
- `Rel(_ctx11)`、`Rel(_dev11)`；
- `WinGCCapture` 中现有引用处理；
- 其他类的 `Dispose` / `ReleaseComObject`。

不要“顺手清理所有 COM 释放”。

### 4.2 增加一个只读创建代数计数

增加：

```csharp
private int _inputGeneration;
```

每次 `CreateInputTexture` 成功并赋给 `_inputTex` 后：

```csharp
_inputGeneration++;
```

在 `Name` 诊断行追加：

```text
inputGen=...
```

语义：

- `inputGen=1`：只有启动时首次创建，未实际覆盖旧纹理；
- `inputGen>1`：本轮确实走过至少一次尺寸重建，可直接检验释放顺序假设。

不新增保留列表，不强制 GC，不调用 `GC.Collect()`。

### 4.3 保留阶段 1D 探针

保留：

```text
dstQi srcQi dstCast srcCast copyCall
```

本轮不得删除 `ProbeResourceQI`，因为 `dstQi` 是主要验收信号。

将报告中的 HRESULT 换算修正为：

```text
-2146233049 / 0x80131527
```

### 4.4 不改变 Copy 表达式

继续保持：

```csharp
ID3D11Resource dstResource = (ID3D11Resource)_inputTex;
ID3D11Resource srcResource = (ID3D11Resource)frameTex;

_ctx11.CopySubresourceRegion(
    dstResource, 0, 0, 0, 0,
    srcResource, 0, box);
```

不得换成 `CopyResource`、无 box 调用、反射调用、dynamic 或原生调用。

---

## 5. 静态验收

执行模型在构建前给出最小 diff，并确认：

- `WinGCCapture.cs` 零改动；
- `PresentOnUI` 零改动；
- `CopySubresourceRegion` 与 `box` 零改动；
- resize 路径只删除一处 `Rel(_inputTex)`；
- `Stop()` 中的释放仍存在；
- 新增内容仅为 `inputGen` 计数与诊断字符串。

如果 diff 还包含 COM 包装、锁、线程队列、try/catch 范围或 shader 变化，拒绝构建并回退这些越界修改。

---

## 6. 构建验收

执行：

```powershell
dotnet clean
dotnet build
```

门槛：

- 0 error；
- 允许保留当前已知 4 个 `CS9191`，不得新增其他警告类型或更多警告；
- 正确的 `IDirect3DDxgiInterfaceAccess` IID 仍为 `A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1`；
- 阶段 1C 的强制 GPU 保留现场逻辑仍生效。

阶段 1D 报告曾把“警告数量从 2 增加到 4”列为注意项；这些新增 `CS9191` 来自规划要求的 QI 探针。本轮不再扩大警告基线。

---

## 7. 实机验收

1. 完全退出所有旧进程；
2. 启动新构建；
3. 明确选择“强制 GPU”，不要选 Auto；
4. 保持灵动岛可见 10 秒；
5. 不拖动、不跨屏、不连续切换设置；
6. 如果应用自身在启动时发生一次尺寸稳定过程，正常保留；不要人为制造高频 resize；
7. 回传完整“当前生效”行和肉眼观察。

必需字段：

```text
wgc= frame= cast= hr= size=
inputGen=
dstQi= srcQi= dstCast= srcCast= copyCall= copy=
blur= flush= queue= present=
renderFail= stage= fail= renderHr= hold=
```

同时报告：

```text
dstQi：十进制 / 十六进制
srcQi：十进制 / 十六进制
renderHr：十进制 / 十六进制
```

肉眼项目：

- 10 秒后是否仍为 GPU 后端；
- 是否出现洋红；
- 是否出现桌面或模糊内容；
- 是否卡顿、黑屏、闪烁或崩溃。

---

## 8. 下一轮决策树

执行模型只报告，不得自行进入下一轮。

| 第一条命中的结果 | 判定 | 规划者下一步 |
|---|---|---|
| `inputGen>1`、`dstQi=0`、`srcQi=0`、`copyCall/copy>0` | 目标 RCW 的主动释放/创建顺序是 Copy 阻断根因 | 阶段 1E 通过；按新首个失败阶段进入 Blur 或呈现分支 |
| `inputGen>1`、`dstQi=0`，但仍在 `copy-call` 抛 `InvalidComObjectException` | 目标 RCW 已恢复，另一个对象（可能 `_ctx11` 或源）仍在调用封送时无效 | 停止；下一轮增加 context/source 精确栈和本机纹理控制 Copy |
| `inputGen>1`、`dstQi` 仍为 `0x80131527` | 删除该释放仍未恢复目标对象 | 停止；搜索项目内其他释放路径，并取 `_inputTex` 首次创建后的即时 QI 与异常完整 stack trace |
| `inputGen=1`、`dstQi=0x80131527` | 本轮未发生覆盖旧纹理；问题可能在 `CreateTexture2D` 返回包装或启动前生命周期 | 停止；下一轮在创建返回点立即 QI，并记录 runtime type / `Marshal.IsComObject` |
| `inputGen=1`、`dstQi=0`、Copy 前进 | 旧报告中的无效目标对象未复现，可能受运行时 resize/生命周期时序影响 | 重复一次固定测试；不得直接宣布修复 |
| Copy 成功，首个失败变为 `blur` | Copy 层闭环 | 进入 Blur 定向诊断；不进入 D3DImage |
| `copy/blur/flush/queue` 全增长但 `present=0` | 后台 GPU 管线贯通，UI 调度/呈现仍阻断 | 进入队列/Dispatcher 分支 |
| `present>0` 但无洋红 | 已到 UI 呈现函数 | 批准阶段 2：修复 `D3DImage.Lock → SetBackBuffer → AddDirtyRect → Unlock` 顺序 |
| 出现崩溃、访问冲突、设备移除或明显显存增长 | 生命周期测试触发安全问题 | 立即退出程序并回传，不重复运行 |

---

## 9. 通过与失败标准

### 阶段 1E 通过

必须同时满足：

```text
dstQi=0
srcQi=0
copyCall>0
copy>0
```

若 `inputGen>1`，同时确认“释放顺序/主动断开”假设；若 `inputGen=1`，只算 Copy 暂时通过，根因仍需复现确认。

### 阶段 1E 未通过

以下任一成立：

- `dstQi=0x80131527`；
- `copyCall=0`；
- Copy 前出现新的异常；
- 需要越界修改才能构建；
- 发生崩溃、黑屏、设备移除或异常显存增长。

未通过不是架构失败；按第 8 节分支回规划者。

---

## 10. 回传模板

```markdown
# DynamicIsland GPU · 阶段 1E 实机观察报告

## 修改文件
- ...

## 最小 diff 摘要
- resize 路径删除：
- 新增 inputGen：
- 禁止文件零改动确认：

## 构建
- errors：
- warnings：

## 10 秒原始诊断行
...

## HRESULT
- dstQi：十进制 / 十六进制
- srcQi：十进制 / 十六进制
- renderHr：十进制 / 十六进制

## 关键计数
- inputGen：
- copyCall/copy：
- blur/flush/queue/present：

## 肉眼观察
- 后端是否保持 GPU：
- 洋红：
- 桌面/模糊内容：
- 卡顿/黑屏/闪烁/崩溃：

## 执行者推断
只写推断，不实施下一阶段。
```

---

## 11. 给执行模型的短指令

> 执行 `STAGE1E_PLANNER_DECISION.md`。阶段 1D 的 `-2146233049` 正确值是 `0x80131527 (COR_E_INVALIDCOMOBJECT)`，不是 `0x80131557`；`dstQi` 已证明目标 `_inputTex` RCW 无效，暂不批准归因于 `D3D11_BOX`。本轮只修改 `GpuGlassBackend.cs`：删除 `UpdateIslandSize` 中 resize 前的那一处 `Rel(_inputTex)`，新增 `inputGen` 计数；保留 Copy、探针、WinGC、Blur、D3DImage 和其他释放逻辑不变。完成 clean/build 和一次固定 10 秒强制 GPU 测试后，按模板停止回传。

---

## 12. 依据

- Microsoft Learn：`InvalidComObjectException` 的 HRESULT 是 `0x80131527 (COR_E_INVALIDCOMOBJECT)`：<https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.invalidcomobjectexception>
- Microsoft Learn：`ReleaseComObject` 会递减 RCW 引用；不当使用会使仍持有 RCW 的代码抛 `InvalidComObjectException`，执行中的释放还可能导致访问冲突：<https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.releasecomobject>
- Microsoft Learn：`CopySubresourceRegion` 的两个资源参数是 `ID3D11Resource*`，source box 是可选的 `const D3D11_BOX*`：<https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-copysubresourceregion>
- Microsoft Learn：`D3D11_BOX` 仅包含六个 `UINT` 字段：<https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ns-d3d11-d3d11_box>
