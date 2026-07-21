# ACCEPTANCE CHECKLIST — DynamicIsland 图形互操作最小复现

> 每阶段完成后，AI 把用户验收结果写入本文件。
> 本文件与 `AI执行计划书_严格拆分版.md` 配套使用。

## 基线

- **仓库**: https://github.com/A-pei-lun/DynamicIsland
- **基线 commit**: d0607b3b3cd25ee2d335d4f04a69c60d016d3f48
- **工作分支**: repro/windows-graphics-interop
- **主项目构建**: 0 errors, 4 warnings (CS9191, 既有)
- **测试日期**: 2026-07-21

## 阶段状态

| 阶段 | 状态 | 日期 | 备注 |
|---|---|---|---|
| M0 | PASS | 2026-07-21 | 用户确认 M0 通过 |
| M1 | PASS | 2026-07-21 | Frame received in 41ms, Surface != null, callback on thread pool thread |
| M2 | PASS | 2026-07-21 | As<TInterop> + GetInterface + staging + Map + BMP all OK, BMP 2560x1600 visual confirmed |
| M3 | PASS | 2026-07-21 | 自动 PASS + 用户确认 12/12 色块同步、无黑屏无错色 |
| M4 | PASS | 2026-07-21 | LUID 匹配、共享打开、12 次 Query 0ms、Readback 11/12（D3D9/D3D11 同步边界）、用户视觉确认一切正常 |
| M5 | M5-C | 2026-07-21 | WPF 内容正常，红色不可见。DispatcherQueue 创建成功但 Compositor 构造函数无法识别（SDK 投影边界）。三种方案均无效。不进入 M6 |
| M6 | NOT RUN | — | — |
| M7 | PASS | 2026-07-21 | 发布整理完成：README、LICENSE、证据归档、调查报告更新。用户确认允许发布 |

## 用户验收记录

### M0

| 检查项 | 结果 |
|---|---|
| 个人信息泄露 | 无泄露 |
| Windows build 字段 | 已确认 |
| .NET SDK/Host 字段 | 已确认区分 |
| 刷新率字段 | 用户已自行填写确认 |
| 主项目基线构建 | 0 errors, 4 warnings（既有） |

### M1

| 检查项 | 结果 |
|---|---|
| D3D11CreateDevice | S_OK |
| CreateDirect3D11DeviceFromDXGIDevice | S_OK |
| GraphicsCaptureItem | 2560x1600 |
| 第一帧 | 41ms 内收到 |
| frame.Surface != null | True |
| 回调线程 | 4（非 UI 线程） |
| 最终判定 | PASS |

### M2

| 检查项 | 结果 |
|---|---|
| As<TInterop> 现代路径 | 成功 |
| GetInterface(ID3D11Texture2D) | hr=0x00000000 (S_OK) |
| TextureDesc | 2560x1600, B8G8R8A8_UNORM |
| CopyResource + Map | 成功 |
| BMP 文件 | 16384054 bytes, 2560x1600, 内容正常 |
| 最终判定 | PASS |

### M3

| 检查项 | 结果 |
|---|---|
| Direct3DCreate9Ex | hr=0x00000000 (S_OK) |
| CreateDeviceEx | hr=0x00000000 (S_OK) |
| CreateTexture (256x256, A8R8G8B8, RENDERTARGET) | 成功 |
| GetSurfaceLevel(0) | 成功 |
| Surface desc | 256x256, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT |
| D3DImage.SetBackBuffer | 成功 |
| WPF rendering tier | 2 |
| 颜色序列 12/12 | 全部完成 |
| 用户视觉确认 | 文字正常、同步变化、无黑屏无错色 |
| 最终判定 | PASS |

### M4

| 检查项 | 结果 |
|---|---|
| Direct3DCreate9Ex | hr=0x00000000 (S_OK) |
| CreateDeviceEx | hr=0x00000000 (S_OK) |
| CreateTexture (shared, 256x256, A8R8G8B8, RENDERTARGET) | 成功，handle=non-zero, value redacted |
| GetSurfaceLevel(0) | 成功 |
| D3D9 LUID | non-zero, value redacted |
| D3D11 LUID | non-zero, value redacted |
| LUID 匹配 | ✓ |
| OpenSharedResource | 成功 |
| CreateRenderTargetView | 成功 |
| CreateQuery(D3D11_QUERY_EVENT) | 成功 |
| 12 次 Query GetData | 全部 0ms 完成 |
| D3D9 staging readback | 11/12 正确（1 次偏差：D3D9/D3D11 同步边界） |
| D3DImage.SetBackBuffer | 成功 |
| WPF rendering tier | 2 |
| 颜色序列 12/12 | 全部完成 |
| 用户视觉确认 | 一切正常 |
| 最终判定 | PASS |

### M7

| 检查项 | 结果 |
|---|---|
| 最终工作分支 | `repro/windows-graphics-interop` |
| M7 主体发布 commit | `0df099a` 发布材料整改 |
| M7 审计整改 commit | `c3a4608` |
| PR 合并 commit | 合并后以 GitHub PR 记录为准 |
| 许可证 | 根目录 MIT LICENSE，Copyright (c) 2026 A-pei-lun |
| 证据归档 | `repros/evidence/2026-07-21-ec73bd81/`（已脱敏） |
| 调查报告更新 | `docs/WINDOWS_GRAPHICS_INTEROP_GREY_AREAS.md` 占位符已清除 |
| `DynamicIsland.slnx -c Release` | 0 errors, 0 warnings |
| `Repros.slnx -c Release` | 0 errors, 0 warnings |
| 用户决定 | 允许发布 ✅ |
| 最终判定 | PASS |