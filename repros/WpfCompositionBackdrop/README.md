# WpfCompositionBackdrop — Repro C

## 目标

验证 Win32/WPF 顶层窗口在公开 Composition 接线下能否显示 SpriteVisual、让 WPF 内容位于其上方，并测试 Backdrop/HostBackdrop 支持边界。

## 非目标

- WinGC、D3DImage、自定义高斯效果、模糊半径、产品化后端

## 构建

```powershell
dotnet build .\repros\Repros.slnx -c Release
```

## 运行

### M5：红色 SpriteVisual 与 WPF 层叠

```powershell
dotnet run --project .\repros\WpfCompositionBackdrop\WpfCompositionBackdrop.csproj -c Release -- --mode red-visual
```

## 实际结果

| 检查项 | 结果 |
|---|---|
| M5（红色 SpriteVisual + WPF 层叠） | **M5-C** |
| WPF 控件（TextBlock/Button/Border） | 可见，正常 |
| 红色背景 | 不可见 |
| 失败原因 | `Compositor()` 构造函数需要当前线程的 `DispatcherQueue`，通过 `coremessaging.dll` 创建的队列 WinRT 投影无法识别（`GetForCurrentThread()` 返回 null） |
| 尝试方案 | ① `CreateOnDedicatedThread()` ② `IDispatcherQueueControllerInterop` COM 工厂 ③ `CreateDispatcherQueueController` 导出函数 — 均无法使 `Compositor` 构造函数通过检查 |

### M6：Backdrop / HostBackdrop

**未执行** — 需要 M5-A 门禁才能进入，当前结果为 M5-C。

## 环境

- 仓库：https://github.com/A-pei-lun/DynamicIsland
- 分支：`repro/windows-graphics-interop`
- 测试日期：2026-07-21
- TFM：`net10.0-windows10.0.26100.0`
- CsWin32：0.3.298
- GPU：NVIDIA RTX 4060 Laptop

## 结果文件

- `repros/artifacts/C-Composition/baseline.log` — 完整日志
- `repros/artifacts/C-Composition/result.json` — 结构化结果
- `repros/evidence/2026-07-21-ec73bd81/C-Composition/` — 归档证据