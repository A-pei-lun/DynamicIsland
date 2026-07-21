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
| M3 | NOT RUN | — | — |
| M4 | NOT RUN | — | — |
| M5 | NOT RUN | — | — |
| M6 | NOT RUN | — | — |
| M7 | NOT RUN | — | — |

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