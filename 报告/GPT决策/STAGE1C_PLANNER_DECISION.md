# DynamicIsland GPU · 阶段 1C 规划者决策

> 日期：2026-07-20  
> 决策角色：高级规划 / 验收 AI  
> 输入：阶段 1B 源码与实机观察报告  
> 本轮性质：故障分层与取证，不进行显示层修复

---

## 0. 最终决策

**阶段 1B 有条件通过：正确 IID 已让执行路径越过 WinGC 表面解包层，但尚未证明 GPU 帧完成了 Copy / Blur / Flush。**

**暂不批准进入阶段 2，也暂不修改 `PresentOnUI` 的 `D3DImage.Lock/SetBackBuffer` 顺序。**

先执行阶段 1C：只在 `GpuGlassBackend.cs` 增加渲染线程分段计数、首个异常信息和“强制 GPU 模式下保留现场”的诊断机制。拿到一次 10 秒实机计数后，再由本规划者决定进入哪一分支。

---

## 1. 为什么驳回“直接修 D3DImage”的建议

当前源码的真实控制流是：

1. `OnFrameArrived` 中的尺寸、资源、Copy、Blur 或 Flush 失败，才会调用 `RegisterEmpty()`。
2. 连续 3 次 `RegisterEmpty()` 才会触发 `FallbackRequested`，随后切到 HLSL。
3. `PresentOnUI` 中的 `D3DImage` 异常被本地 `catch { }` 吞掉；这里**没有**调用 `RegisterEmpty()`，也**不会**直接触发 HLSL 回退。

因此，“进入 GPU 后 1–2 秒自动回退”证明的只是失败发生在 GPU 后端的前半段或其前置检查；它不能证明瓶颈已经来到 D3DImage。若此时直接改 Lock 顺序，可能修到一个确实存在、但并非当前回退原因的后续问题。

另外，`UpdateIslandSize(w, h)` 目前位于渲染 `try` 之外。如果它抛出异常，异常会继续传回 `WinGCCapture` 的回调；这会让上层把消费者异常误记成表面转换失败，并可能启动一次不必要的原生 fallback。阶段 1C 必须先把这类异常收进 GPU 后端自己的诊断边界。

---

## 2. 本轮唯一目标

回答下面这个问题：

> 第一处失败究竟发生在尺寸/资源检查、纹理 Copy、Blur、Flush、UI 排队，还是 UI Present？

本轮不追求看到正确玻璃，也不以洋红是否出现作为通过条件。

---

## 3. 修改边界

### 允许修改

- `GpuGlassBackend.cs`
- `PLAN_LOG.md`
- 新增本轮观察报告 `STAGE1C_OBSERVATION_REPORT.md`

### 禁止修改

- `WinGCCapture.cs`（保留阶段 1B 的正确 IID 和现有路径）
- `GpuBlur.cs`
- `D3D11Interop.cs`
- `LiquidGlassRenderer.cs`
- `PresentOnUI` 内部的 Lock / SetBackBuffer 顺序
- shader、UI 样式、窗口尺寸、DWM 底座和其他后端

不要顺手重构、优化或清理警告。若编译所需的枚举/设置名称与本文示例不一致，只可做等价适配，并在报告中记录。

---

## 4. 执行要求

### 4.1 增加分段诊断字段

在 `GpuGlassBackend` 内增加至少以下状态；字段名可等价，但报告中的含义必须一致：

```csharp
private int _sizeOk;
private int _copyOk;
private int _blurReturn;
private int _flushOk;
private int _queueOk;
private int _renderFail;
private int _fallbackSuppressed;
private string _lastStage = "none";
private string _lastFail = "none";
private int _lastRenderHr;
```

语义：

- `sizeOk`：本帧尺寸、监器裁剪范围及必要资源全部有效。
- `copyOk`：`CopySubresourceRegion` 正常返回。
- `blurReturn`：`GpuBlur.Blur` 正常返回；它只表示调用未抛异常，不等于已经证明画面正确。
- `flushOk`：`_interop.Flush()` 正常返回。
- `queueOk`：成功调用 `Dispatcher.BeginInvoke(PresentOnUI)`。
- `renderFail`：GPU 后端在本帧记录到的失败次数。
- `lastStage`：当前或最近执行阶段，值至少区分 `size`、`resize`、`bounds`、`resource`、`copy`、`blur`、`flush`、`queue`。
- `lastFail`：短失败原因，禁止塞入完整堆栈。
- `lastRenderHr`：异常 `HResult`；非异常前置条件失败可填 0。
- `fallbackSuppressed`：强制 GPU 模式下达到回退阈值、但为保留诊断现场而未切换后端的次数。

把这些字段追加到 `Name`。字段名称必须简短、固定，确保用户能在设置页读出或截图，例如：

```text
size=… copy=… blur=… flush=… queue=… renderFail=… stage=… fail=… renderHr=… hold=…
```

保留现有 `frame/present/wgc/cast/hr/lastHr/nativeQiHr` 字段。

### 4.2 扩大 GPU 后端自己的异常边界

`OnFrameArrived` 中，在完成基础的 `IsRunning/null` 快速返回之后，用同一个 `try/catch (Exception ex)` 覆盖以下全部操作：

- `GetWindowRect`
- 宽高判定
- `UpdateIslandSize`
- 监器信息和裁剪范围计算
- 资源判定
- `CopySubresourceRegion`
- `GpuBlur.Blur`
- `Flush`
- `Dispatcher.BeginInvoke`

在每一操作之前写入对应的 `_lastStage`；操作成功后增加对应计数。

所有当前的 `RegisterEmpty()` 改为带原因的等价调用，例如：

```csharp
RegisterEmpty("window-rect");
RegisterEmpty("zero-size");
RegisterEmpty("resize-false");
RegisterEmpty("crop-empty");
RegisterEmpty("resource-null");
```

总 catch 必须记录 `_lastStage`、异常类型和 `ex.HResult`，然后走同一个 `RegisterEmpty` 逻辑。不要让 GPU 消费者异常重新冒泡给 `WinGCCapture`。

### 4.3 强制 GPU 模式保留现场

当前 3 次失败就替换后端，导致所有 GPU 计数随对象一起丢失。本轮采用以下临时诊断策略：

- 若用户明确选择的是**强制 GPU**，达到 3 次失败时：增加 `fallbackSuppressed`，保留当前 GPU 后端，不触发 `FallbackRequested`。
- 若模式是 **Auto**，维持原有 3 次失败后回退 HLSL 的行为。
- 不得把这一逻辑扩展到 GPU 初始化失败；`Start` 构造失败仍应安全回退。

优先读取项目现有的 `DisplaySettings.Instance.CaptureMode` / GPU 枚举进行判断。若名称不同，只做最小等价适配。

这是诊断保留机制，不是对自动降级策略的最终产品决策。后续完成根因修复后，本规划者会决定保留还是移除。

### 4.4 不修改 Present

本轮 `PresentOnUI` 只允许保留已有 `_presentCount++` 计数，不得调整：

- `Lock` / `Unlock`
- `SetBackBuffer`
- `AddDirtyRect`
- COM 引用管理
- catch 行为

原因：要先用 `queue` 与 `present` 的差值判断 UI 队列是否真正执行，避免同时改变被测对象。

---

## 5. 本地静态与构建验收

执行模型必须完成：

1. `dotnet clean`
2. `dotnet build`
3. 记录错误数和警告数
4. 搜索并确认只有允许文件发生运行时代码变化
5. 确认正确 IID 仍为 `A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1`
6. 确认 `PresentOnUI` 的原代码本轮未变

构建门槛：

- 必须 0 error。
- 已知的 2 个 CS9191 警告可保留；若警告数量或类型增加，先停止并报告。

---

## 6. 用户实机验收步骤

请执行模型构建并打包后，让用户只做这一轮：

1. 完全退出旧进程，启动新构建。
2. 设置捕获模式为“GPU 硬件加速/强制 GPU”，不要选 Auto。
3. 保持灵动岛可见 10 秒，不切屏、不拖动、不跨显示器。
4. 打开能显示后端 `Name` 的位置并截图，或逐字抄回完整诊断行。
5. 若应用崩溃，记录崩溃时间、异常窗口文字及 Windows 事件查看器条目；不要继续修改代码。

必须回传的原始字段：

```text
frame=
present=
wgc=
wgcFail=
cast=
hr=
lastHr=
nativeQiHr=
size=
copy=
blur=
flush=
queue=
renderFail=
stage=
fail=
renderHr=
hold=
```

同时回答：

- 10 秒后是否仍显示 GPU 后端？
- 是否看到洋红？
- 是否看到真实桌面内容或模糊内容？
- UI 是否卡顿或崩溃？

---

## 7. 规划者下一步判读树

拿到数据后严格按第一条命中的分支处理：

| 观察 | 判定 | 下一轮方向 |
|---|---|---|
| `wgc` 增长但 `frame=0` | WinGC 表面解包仍失败 | 回到 WinGC / COM 边界；不碰 D3DImage |
| `frame` 增长，`size=0` 或 `fail` 为尺寸/裁剪/资源 | 前置状态失败 | 修尺寸、监器坐标或资源生命周期 |
| `size>0, copy=0` 且 `stage=copy` | 纹理复制失败 | 检查源纹理格式、尺寸、设备归属和裁剪盒 |
| `copy>0, blur=0` 且 `stage=blur` | 模糊调用抛异常 | 进入 `GpuBlur` 定向诊断 |
| `blur>0, flush=0` 且 `stage=flush` | 共享纹理/Flush 路径失败 | 检查 D3D11/D3D9 互操作与资源生命周期 |
| `flush>0, queue=0` | UI 调度请求失败 | 检查 Dispatcher/窗口生命周期 |
| `queue>0, present=0` | UI 队列未执行或 Present 前置返回 | 检查 UI 线程状态和后端生命周期 |
| `present>0` 且无洋红/无画面 | 前半管线已贯通，显示层失败 | **此时才批准阶段 2：修 D3DImage Lock/SetBackBuffer** |
| `present>0` 且出现洋红 | D3DImage 已显示共享纹理 | 进入 shader/Blur 输出验证，不先修 Lock |
| `cast` 随 `frame` 同步异常增长 | 消费者异常仍污染 WinGC 诊断 | 下一轮拆分 `WinGCCapture` 的提取与事件回调异常边界 |

不得仅凭“GPU 能短暂进入”“没有洋红”或“肉眼透明”跳过计数判读。

---

## 8. 停止条件

出现下列任一情况，执行模型必须停止并将控制权交回规划者，不得自行扩展修复：

- 需要修改 `GpuGlassBackend.cs` 以外的运行时代码才能编译。
- 新增构建错误或新增警告类型。
- 强制 GPU 仍自动切到 HLSL，且 `hold` 没有增长。
- 应用崩溃、设备移除、访问冲突或出现不可恢复黑屏。
- 诊断字段互相矛盾，例如 `copy > frame`、`flush > blur`、`present > queue`（允许少量在途队列差，但不允许长期逆序）。
- 无法识别项目中“强制 GPU”和“Auto”的枚举值。

---

## 9. 回传模板

执行模型完成后提交 `STAGE1C_OBSERVATION_REPORT.md`，至少包含：

```markdown
# 阶段 1C 实机观察报告

## 修改文件
- ...

## 构建
- clean：
- build：
- errors：
- warnings：

## 静态确认
- 正确 IID 是否保留：
- PresentOnUI 是否未变：
- Auto 是否仍可回退：
- 强制 GPU 是否保留现场：

## 10 秒实机原始诊断行
...

## 肉眼观察
- 10 秒后后端：
- 洋红：
- 桌面/模糊内容：
- 卡顿/崩溃：

## 执行者推断
只写推断，不得擅自进入下一阶段。
```

---

## 10. 给执行模型的短指令

> 执行 `STAGE1C_PLANNER_DECISION.md`。本轮只允许修改 `GpuGlassBackend.cs`，目标是找出 GPU 前半管线的第一失败阶段并在强制 GPU 模式保留诊断现场。不要修改 `PresentOnUI` 的 D3DImage 逻辑，不要改 `WinGCCapture.cs`，不要进入阶段 2。构建通过后让用户执行一次固定 10 秒测试，按模板回传完整计数；任何超出范围的情况立即停止并交回规划者。
