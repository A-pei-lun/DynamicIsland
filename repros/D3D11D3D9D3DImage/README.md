# D3D11D3D9D3DImage — Repro B

## 目标

验证 D3D9Ex 共享纹理 → D3D11 OpenSharedResource → WPF D3DImage 显示链路，在 LUID 匹配和 D3D11 event query 同步下的正确性。

## 非目标

- WinGC、shader、模糊、后台连续抓屏、DynamicIsland UI
- 产品级异步架构或 worker-thread 连续写共享 surface

## 构建

```powershell
dotnet build .\repros\Repros.slnx -c Release
```

## 运行

### M3：纯 D3D9Ex 本地 surface → D3DImage（无 D3D11）

```powershell
dotnet run --project .\repros\D3D11D3D9D3DImage\D3D11D3D9D3DImage.csproj -c Release -- --mode d3d9-local
```

每秒切换纯色，共 12 次。Esc 关闭。

### M4：D3D9Ex 共享 + D3D11 Clear + Query event → D3DImage

```powershell
dotnet run --project .\repros\D3D11D3D9D3DImage\D3D11D3D9D3DImage.csproj -c Release -- --mode d3d11-query
```

每秒 ClearRenderTargetView + D3D11_QUERY_EVENT 等待 GPU 完成，每秒切换纯色，共 12 次。Esc 关闭。

### flush-only-observation（对照，不参与 PASS 判定）

```powershell
dotnet run --project .\repros\D3D11D3D9D3DImage\D3D11D3D9D3DImage.csproj -c Release -- --mode flush-only-observation
```

## 预期

- M3：12 次色块无黑屏、无错色，与文字同步
- M4：LUID 匹配、共享打开成功、12 次 Query 超时内完成、回读像素 12/12 正确

## 实际结果

| 检查项 | 结果 |
|---|---|
| M3（D3D9Ex 本地 → D3DImage） | **PASS** — 12/12 色块同步，无黑屏无错色 |
| M4（共享 + D3D11 Query） | **PASS** — LUID 匹配、Query 12/12 0ms、回读 11/12 正确 |
| D3D9 LUID | 0x00011ECB:0x00000000 |
| D3D11 LUID | 0x00011ECB:0x00000000 |
| 回读 1 次偏差 | 已知 D3D9/D3D11 同步边界，不影响显示 |

## 环境

- 仓库：https://github.com/A-pei-lun/DynamicIsland
- 分支：`repro/windows-graphics-interop`
- 测试日期：2026-07-21
- TFM：`net10.0-windows10.0.26100.0`
- CsWin32：0.3.298
- GPU：NVIDIA RTX 4060 Laptop

## 结果文件

- `repros/artifacts/B-D3D9D3DImage/baseline.log` — 完整日志
- `repros/artifacts/B-D3D9D3DImage/result.json` — 结构化结果
- `repros/evidence/2026-07-21-ec73bd81/B-D3D9D3DImage/` — 归档证据