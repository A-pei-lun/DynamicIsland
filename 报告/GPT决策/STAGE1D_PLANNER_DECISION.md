# DynamicIsland GPU · 阶段 1D 规划者决策

> 日期：2026-07-20  
> 输入：`STAGE1C_OBSERVATION_REPORT.md`、本轮 `GpuGlassBackend.cs`、`WinGCCapture.cs`  
> 本轮性质：Copy 层归因校正；只诊断，不实施架构替换

---

## 0. 最终决策

**阶段 1C 的计数证据有效：WinGC 表面提取已贯通，当前第一处失败位于 Copy 层。**

但执行报告提出的根因——“WinGC 传出的纹理 RCW 有缺陷，必须把事件改成 `ID3D11Resource`”——**证据不足，暂不批准**。本轮代码在同一条表达式中仍包含目标纹理转换、源纹理对象转换和 `CopySubresourceRegion` 参数封送，`InvalidCastException` 不能证明究竟是哪一个对象失败。

更关键的是，当前代码第一个显式强制转换是：

```csharp
(ID3D11Resource)_inputTex
```

它是本机 D3D11 设备创建的目标纹理，不是 WinGC 帧纹理。如果异常发生在这里，则阶段 1C 报告对源纹理的归因完全错误。

因此批准阶段 1D：**仍然只修改 `GpuGlassBackend.cs`，把 Copy 层拆成原生 QI、目标转换、源转换、实际 Copy 四个独立观测点。** 在新证据返回前：

- 不修改 `WinGCCapture.FrameArrived` 的参数类型；
- 不传递裸 `IntPtr`；
- 不手写 D3D11 vtable；
- 不进入 D3DImage 修复；
- 不修改 Blur、shader 或设备创建路径。

---

## 1. 本轮为何不是“架构级失败”

阶段 1C 的实机计数为：

```text
wgc=411, frame=411, cast=0, hr=0, size=411,
copy=0, blur=0, flush=0, queue=0, present=0,
stage=copy, fail=InvalidCastException, renderHr=0x80004002
```

这证明：

1. WinGC 回调正常；
2. `IDirect3DDxgiInterfaceAccess.GetInterface(ID3D11Texture2D)` 已成功；
3. GPU 后端收到全部 411 帧；
4. 尺寸、裁剪和资源前置检查全部通过；
5. 失败发生在托管 COM 接口转换或 Copy 调用边界，尚未证明底层 GPU 纹理不兼容。

`ID3D11Texture2D` 在原生 D3D11 接口契约上继承 `ID3D11Resource`。`CopySubresourceRegion` 的源、目标参数也都要求 `ID3D11Resource*`。所以必须分开验证“原生对象是否支持该 IID”和“.NET 投影能否完成同一转换”。

---

## 2. 对阶段 1C 执行偏差的判定

阶段 1C 允许在 `GpuGlassBackend.cs` 内增加诊断，但禁止进行功能性修复。执行者加入了以下未经批准的适配：

```csharp
Marshal.GetIUnknownForObject(frameTex)
    -> Marshal.GetObjectForIUnknown(...)
    -> as ID3D11Resource
```

这段适配没有隔离故障，反而增加了第二个 RCW、对象身份缓存和引用所有权变量；`frameObj` 也没有明确释放。它不能作为最终修复保留。

阶段 1D 必须删除这段适配，恢复为可逐点观测的直接路径。不得在本轮尝试新的 RCW 包装方案。

---

## 3. 本轮唯一目标

回答两个问题：

1. 源/目标原生 COM 对象分别能否 `QueryInterface(IID_ID3D11Resource)`？
2. `InvalidCastException` 精确发生在目标转换、源转换，还是 `CopySubresourceRegion` 调用内部？

本轮不要求看到洋红，也不要求 Copy 成功。

---

## 4. 修改边界

### 允许修改

- `GpuGlassBackend.cs`
- `PLAN_LOG.md`
- 新增 `STAGE1D_OBSERVATION_REPORT.md`

### 禁止修改

- `WinGCCapture.cs`
- `GpuBlur.cs`
- `D3D11Interop.cs`
- `LiquidGlassRenderer.cs`
- `PresentOnUI`
- shader / `.cso`
- DWM 底座、UI、设置项、设备创建方式
- NuGet 版本、目标框架和项目属性

---

## 5. 规定实现

### 5.1 删除阶段 1C 的 Copy 适配

删除 `frameUnk`、`frameObj`、`Marshal.GetObjectForIUnknown` 和 `copy-no-resource` 这一整段临时逻辑。

### 5.2 增加最小诊断字段

字段名可等价，但 `Name` 中必须能读到以下值：

```text
dstQi=...
srcQi=...
dstCast=...
srcCast=...
copyCall=...
```

语义：

- `dstQi`：`_inputTex` 原生对象查询标准 `IID_ID3D11Resource` 的 HRESULT；
- `srcQi`：`frameTex` 原生对象查询标准 `IID_ID3D11Resource` 的 HRESULT；
- `dstCast`：托管目标转换成功次数；
- `srcCast`：托管源转换成功次数；
- `copyCall`：`CopySubresourceRegion` 正常返回次数。

`dstQi/srcQi` 初始值使用一个明确的“尚未执行”哨兵值，不要让默认 `0` 被误判为成功。

标准 `IID_ID3D11Resource`：

```text
DC8E63F3-D12B-4952-B47B-5E45026A862D
```

### 5.3 原生 QI 探针

实现一个只读辅助方法，分别探测 `_inputTex` 和 `frameTex`：

```csharp
private static int ProbeResourceQI(object obj)
{
    IntPtr unk = IntPtr.Zero;
    IntPtr resource = IntPtr.Zero;
    try
    {
        unk = Marshal.GetIUnknownForObject(obj);
        Guid iidResource = new("DC8E63F3-D12B-4952-B47B-5E45026A862D");
        return Marshal.QueryInterface(unk, ref iidResource, out resource);
    }
    catch (Exception ex)
    {
        return ex.HResult;
    }
    finally
    {
        if (resource != IntPtr.Zero) Marshal.Release(resource);
        if (unk != IntPtr.Zero) Marshal.Release(unk);
    }
}
```

这只是能力探针，**不得把返回的 `resource` 指针包装成新 RCW 或传给渲染管线**。

### 5.4 把 Copy 拆成五个有序阶段

在每帧 Copy 位置按以下顺序执行，阶段名必须可区分：

```csharp
_lastStage = "copy-qi";
_dstQiHr = ProbeResourceQI(_inputTex);
_srcQiHr = ProbeResourceQI(frameTex);

_lastStage = "copy-dst-cast";
ID3D11Resource dstResource = (ID3D11Resource)_inputTex;
_dstCastOk++;

_lastStage = "copy-src-cast";
ID3D11Resource srcResource = (ID3D11Resource)frameTex;
_srcCastOk++;

_lastStage = "copy-call";
_ctx11.CopySubresourceRegion(
    dstResource, 0, 0, 0, 0,
    srcResource, 0, box);
_copyCallOk++;
_copyOk++;
```

说明：

- 两次 QI 必须先完成，确保即使第一个托管转换失败，也能同时拿到源/目标原生能力数据；
- 不要在同一表达式中写两个强制转换；
- catch 继续沿用阶段 1C 的总异常边界；
- 不得吞掉 Copy 层异常；
- 不得用 `dynamic`、反射、`as` 或新 RCW 绕过失败。

### 5.5 静态投影取证

不修改项目配置，仅记录：

- `Microsoft.Windows.CsWin32` 的实际版本；
- 生成代码中 `ID3D11Texture2D` 的声明是否继承 `ID3D11Resource`；
- 生成的 `CopySubresourceRegion` 签名；
- `typeof(ID3D11Texture2D).GUID` 与 `typeof(ID3D11Resource).GUID` 的实际值。

如果当前构建默认不保留生成代码，可用一次性命令行 MSBuild 属性将生成文件输出到 `obj` 下；不得把生成文件加入项目或提交源码。

---

## 6. 构建验收

执行：

```powershell
dotnet clean
dotnet build
```

门槛：

- 0 error；
- 只允许保留已知 2 个 `CS9191`；
- 正确的 `IDirect3DDxgiInterfaceAccess` IID 仍为 `A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1`；
- `WinGCCapture.cs` 与 `PresentOnUI` 必须零改动；
- 阶段 1C 的强制 GPU 保留现场逻辑继续生效。

如新增警告、无法找到生成接口声明，或必须修改禁止文件才能编译，立即停止回传。

---

## 7. 实机验收

1. 完全退出旧进程；
2. 启动新构建；
3. 明确选择“强制 GPU”，不要选 Auto；
4. 保持灵动岛可见 10 秒，不切屏、不拖动、不跨显示器；
5. 回传完整“当前生效”诊断行和肉眼观察。

必需字段：

```text
wgc= frame= cast= hr= size=
dstQi= srcQi= dstCast= srcCast= copyCall= copy=
blur= flush= queue= present=
renderFail= stage= fail= renderHr= hold=
```

`dstQi/srcQi` 必须以十进制和十六进制各报告一次。

---

## 8. 下一轮决策树

按第一条命中的分支执行；执行模型不得自行进入下一轮。

| 结果 | 判定 | 规划者下一步 |
|---|---|---|
| `dstQi != 0` | 本机创建的 Texture2D 连原生 Resource QI 都失败，接口 IID/对象类型/绑定生成异常 | 停止；核对 GUID、生成接口和对象真实类型 |
| `dstQi=0, srcQi!=0` | WinGC 返回指针不满足 Texture2D→Resource 原生契约 | 停止；回到 `GetInterface` 返回指针和 IID 取证 |
| `dstQi=0, srcQi=0, stage=copy-dst-cast` | 本机目标纹理的托管基接口转换失败 | 证明不是 WinGC 特有问题；修 CsWin32 投影/封送边界 |
| 两个 QI 为 0，`dstCast>0`、`stage=copy-src-cast` | 仅 WinGC 纹理 RCW 的托管基接口转换失败 | 才批准评估 `WinGCCapture` 的 typed RCW / Resource 事件方案 |
| 两个 cast 均增长，`stage=copy-call` | 对象转换成功，异常发生在调用封送或生成签名 | 进入最小原生调用适配评估，不改 WinGC 事件类型 |
| `copyCall/copy > 0`，下一失败为 `blur` | Copy 层贯通 | 回到总计划的 Blur 定向诊断分支 |
| `copy/blur/flush/queue/present` 均增长 | 前半管线贯通 | 批准阶段 2：D3DImage 呈现修复/洋红验收 |

---

## 9. 必须停止的情况

- 需要修改 `WinGCCapture.cs` 才能完成阶段 1D；
- 发生访问冲突、设备移除、黑屏或崩溃；
- `dstQi/srcQi` 没有执行却显示为 0；
- 引用计数无法成对释放；
- 执行者想用裸指针、vtable、C++/CLI、SharpDX/Vortice 或更换互操作库直接“修掉”；
- 诊断字段计数逆序或互相矛盾；
- 构建警告类型增加。

---

## 10. 回传文件模板

```markdown
# 阶段 1D 实机观察报告

## 修改文件
- ...

## 构建
- errors：
- warnings：
- CsWin32 版本：

## 生成接口取证
- ID3D11Texture2D 声明：
- ID3D11Resource GUID：
- CopySubresourceRegion 签名：

## 10 秒原始诊断行
...

## HRESULT
- dstQi：十进制 / 十六进制
- srcQi：十进制 / 十六进制

## 肉眼观察
- 后端是否保持 GPU：
- 洋红：
- 桌面/模糊内容：
- 卡顿/崩溃：

## 执行者推断
只写推断；不要实施下一阶段修复。
```

---

## 11. 给执行模型的短指令

> 执行 `STAGE1D_PLANNER_DECISION.md`。阶段 1C 已证明第一故障在 Copy 层，但尚未证明是 WinGC 源纹理 RCW。只修改 `GpuGlassBackend.cs`：删除阶段 1C 的临时 RCW 适配，分别记录目标/源原生 `IID_ID3D11Resource` QI、目标托管转换、源托管转换和实际 Copy 调用。禁止修改 `WinGCCapture.cs`、事件参数、D3DImage、Blur、shader 和设备创建方式。完成一次固定 10 秒强制 GPU 测试后，按模板停止回传。

---

## 12. 依据

- Microsoft Learn：`ID3D11Texture2D` 原生接口继承 `ID3D11Resource`。
- Microsoft Learn：`ID3D11DeviceContext::CopySubresourceRegion` 的源与目标参数均为 `ID3D11Resource*`。
- Microsoft Learn：`Marshal.QueryInterface` 等价于对原生 COM 对象执行接口查询，成功返回的指针会增加引用计数，使用后必须 `Release`。
- Microsoft Learn：`Marshal.GetObjectForIUnknown` 返回 RCW，而且任意 COM 接口指针都可作为输入；因此“`ID3D11Resource*` 不能传给该方法”的说法不成立。
