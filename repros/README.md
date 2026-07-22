# DynamicIsland — Windows 图形互操作最小复现

> 本目录包含三个互不依赖的最小复现项目，用于验证 Windows 图形 API 互操作边界。
> 不修改主应用 `DynamicIsland/`，不依赖于 `GlassBench/` 或 `GlassSpike/`。

## 项目导航

| 目录 | 目标 | 依赖 | 结果 |
|---|---|---|---|
| `WinGCSurfaceInterop/` | WinGC `Direct3D11CaptureFrame.Surface` → `ID3D11Texture2D` ABI 路径 | WinRT, D3D11, CsWinRT, CsWin32 | **M1+M2 PASS** ✅ |
| `D3D11D3D9D3DImage/` | D3D11 → D3D9Ex 共享纹理 → WPF D3DImage 稳定显示 | D3D11, D3D9Ex, WPF | **M3 PASS** ✅; **M4 visual PASS, strict validation FAIL** |
| `WpfCompositionBackdrop/` | WPF 窗口 + Windows Composition SpriteVisual + Backdrop/HostBackdrop | WPF, Comp, CsWin32 | **M5-C** 🟡（DispatcherQueue 边界） |

## 判读表

- 每个项目都有独立的 README，说明目标、非目标、构建和运行方法。
- 所有项目使用 `net10.0-windows10.0.26100.0`、x64、Nullable + ImplicitUsings 开启。
- 异常记录类型、`HResult` 和消息；API 调用记录十六进制 HRESULT。
- 所有等待都有超时；超时输出 `INCONCLUSIVE`。

## 构建

```powershell
dotnet build .\repros\Repros.slnx -c Release
```

## 环境收集

```powershell
powershell -ExecutionPolicy Bypass -File .\repros\Collect-Environment.ps1
```

输出到 `repros/artifacts/environment/environment.md`。

## 证据归档

`repros/evidence/2026-07-21-ec73bd81/` 包含已脱敏的日志、脱敏 PNG 视觉证据、截图和环境摘要。