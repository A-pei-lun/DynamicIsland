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
| M4 visual presentation | **PASS** — 12/12，用户三次目视均正常，无黑屏无错色 |
| M4 strict automated readback validation | **FAIL** — 见下节已知限制 |

## 已知限制 / Known limitation

当前 Repro 的 legacy D3D9Ex ↔ D3D11 shared-resource 路径中，D3D11 event query 完成后立即执行的 D3D9-side staging readback 未表现出稳定的逐帧可见性。

三次实测中，用户视觉显示均为 12/12，但即时自动 readback 结果为：

| 测试 | 自动 readback | 环境 |
|---|---|---|
| 原始正式运行 | 11/12 | 2026-07-21，`repro/windows-graphics-interop` |
| 后续重测 #1 | 10/12 (artifact overwritten, unavailable) | 2026-07-22，`fix/m4-verdict-consistency` |
| 后续重测 #2 | 9/12 | 2026-07-22，`fix/m4-verdict-consistency` |

因此，本 Repro **不将 `D3D11_QUERY_EVENT` 视为已经证明足以提供该 legacy 跨 API shared-resource 路径的严格同步/可见性保证**。

注意：
- 屏幕视觉正常 **不等于** 严格同步已经证明；
- 此结果 **不自动证明** Windows 或驱动存在 bug；
- exact underlying cause has **not been isolated**；
- 不得把"D3D11 写入尚未完全提交"写成已经证明的唯一根因。

## 环境

- 仓库：https://github.com/A-pei-lun/DynamicIsland
- 分支：`repro/windows-graphics-interop`
- 测试日期：2026-07-21
- TFM：`net10.0-windows10.0.26100.0`
- CsWin32：0.3.298
- GPU：NVIDIA GeForce RTX 4070 Laptop GPU

## 结果文件

- `repros/artifacts/B-D3D9D3DImage/baseline.log` — 完整日志（最新运行）
- `repros/artifacts/B-D3D9D3DImage/result.json` — 结构化结果（最新运行）
- `repros/evidence/2026-07-21-ec73bd81/B-D3D9D3DImage/` — 原始正式运行归档证据（11/12）
- Retest #1（10/12）— 仅有观察记录，原始产物已被后续运行覆盖，不可恢复
- `repros/evidence/2026-07-22-fix-m4/retest-02/` — 审计重测 #2 归档证据（9/12，已脱敏）