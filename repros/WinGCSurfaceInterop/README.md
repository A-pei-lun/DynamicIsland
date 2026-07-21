# WinGCSurfaceInterop — Repro A

## 目标

验证 .NET 10 + Windows SDK 26100 + C#/WinRT + CsWin32 环境下，能否从 `Direct3D11CaptureFrame.Surface` 安全取得 `IDirect3DDxgiInterfaceAccess` 和原生 `ID3D11Texture2D`。

## 非目标

- WPF UI、D3D9Ex、D3DImage、模糊 shader、共享纹理
- 连续抓屏或产品化异步架构

## 构建

```powershell
dotnet build .\repros\Repros.slnx -c Release
```

## 运行

```powershell
dotnet run --project .\repros\WinGCSurfaceInterop\WinGCSurfaceInterop.csproj -c Release
```

## 预期

1. D3D11CreateDevice 成功
2. 捕获主显示器，收到一帧（超时 10 秒内）
3. `frame.Surface` 非空
4. C#/WinRT `As<TInterop>()` 现代路径成功
5. `GetInterface(ID3D11Texture2D)` 成功
6. 纹理描述正确（匹配桌面分辨率）
7. 回读成功，写入 frame.bmp

## 实际结果

| 检查项 | 结果 |
|---|---|
| M1（收帧基线） | PASS |
| M2（surface interop + 回读） | PASS |
| 桌面分辨率 | 2560x1600 |
| BMP 大小 | 16384054 bytes |
| BMP 内容 | 正常 |

## 环境

- 仓库：https://github.com/A-pei-lun/DynamicIsland
- 分支：`repro/windows-graphics-interop`
- 测试日期：2026-07-21
- TFM：`net10.0-windows10.0.26100.0`
- CsWin32：0.3.298
- GPU：NVIDIA RTX 4060 Laptop

## 结果文件

- `repros/artifacts/A-WinGC/baseline.log` — 完整日志
- `repros/artifacts/A-WinGC/result.json` — 结构化结果
- `repros/artifacts/A-WinGC/frame.bmp` — 桌面截图
- `repros/evidence/2026-07-21-ec73bd81/A-WinGC/` — 归档证据