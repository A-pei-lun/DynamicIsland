# Environment — DynamicIsland 图形互操作最小复现

> 自动收集时间：2026-07-21 12:16:27
> 本文件已脱敏，不包含用户名、计算机名、产品 ID、IP、MAC、设备序列号或绝对用户目录。

## 1. 测试基本信息

| 字段 | 值 |
|---|---|
| 测试日期 | 2026-07-21 |
| Git commit | 395c12a7ea76826525173734f36c24c474b78537 |
| 工作分支 | repro/windows-graphics-interop |

## 2. 操作系统

| 字段 | 值 |
|---|---|
| Windows 版本 | Microsoft Windows 11 家庭版 中文版 |
| 版本 (WinVer) | 26200 (Insider Preview)，对应正式版基线 26100.8894 |
| DisplayVersion | 25H2 |
| CurrentBuild | 26200 |
| UBR (Update Build Revision) | 8894 |
| 完整 OS build 字符串 | Microsoft Windows NT 10.0.26200.0 |
| 系统架构 | 64 位 |
| 进程架构 | AMD64 |

> **注意：** 26200 是 Insider Preview 版本号，26100.8894 是 build + UBR。
> DxDiag 显示 OS Build Version: 10.0.26100.8894 (26200.8894)，两者为同一系统的不同维度标识。

## 3. .NET SDK 与运行时

| 字段 | 值 |
|---|---|
| SDK 版本 | 10.0.302 |
| Host 版本 | 10.0.10 |
| 运行时版本 | Microsoft.NETCore.App 3.1.4 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]; Microsoft.NETCore.App 8.0.15 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]; Microsoft.NETCore.App 8.0.23 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]; Microsoft.NETCore.App 9.0.12 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]; Microsoft.NETCore.App 10.0.10 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App] |
| 目标框架 (TFM) | net10.0-windows10.0.26100.0 |
| Windows SDK 版本 | 10.0.26100.0 |

dotnet --info 完整输出：

```
.NET SDK:
 Version:           10.0.302
 Commit:            35b593bebf
 Workload version:  10.0.300-manifests.714b12c0
 MSBuild version:   18.6.11+35b593beb

运行时环境:
 OS Name:     Windows
 OS Version:  10.0.26200
 OS Platform: Windows
 RID:         win-x64
 Base Path:   C:\Program Files\dotnet\sdk\10.0.302\

已安装 .NET 工作负载:
没有要显示的已安装工作负载。
已配置为在安装新清单时使用 workload sets。
未安装任何 workload sets。运行 “dotnet workload restore” 以安装工作负载集。

Host:
  Version:      10.0.10
  Architecture: x64
  Commit:       f7d90799ce

.NET SDKs installed:
  8.0.417 [C:\Program Files\dotnet\sdk]
  9.0.309 [C:\Program Files\dotnet\sdk]
  10.0.302 [C:\Program Files\dotnet\sdk]

.NET runtimes installed:
  Microsoft.AspNetCore.App 8.0.23 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
  Microsoft.AspNetCore.App 9.0.12 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
  Microsoft.AspNetCore.App 10.0.10 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
  Microsoft.NETCore.App 3.1.4 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
  Microsoft.NETCore.App 8.0.15 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
  Microsoft.NETCore.App 8.0.23 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
  Microsoft.NETCore.App 9.0.12 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
  Microsoft.NETCore.App 10.0.10 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
  Microsoft.WindowsDesktop.App 3.1.4 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
  Microsoft.WindowsDesktop.App 8.0.15 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
  Microsoft.WindowsDesktop.App 8.0.23 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
  Microsoft.WindowsDesktop.App 9.0.12 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
  Microsoft.WindowsDesktop.App 10.0.10 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]

Other architectures found:
  x86   [C:\Program Files (x86)\dotnet]
    registered at [HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x86\InstallLocation]

Environment variables:
  Not set

global.json file:
  Not found

Learn more:
  https://aka.ms/dotnet/info

Download .NET:
  https://aka.ms/dotnet/download
```

> **注意：** 10.0.302 是 .NET SDK 版本，不是 runtime/Host 版本。
> 请用户确认 SDK 和 runtime 版本已正确区分。

## 4. GPU 与显示

| 字段 | 值 |
|---|---|
| GPU 名称 | Todesk Virtual Display Adapter
  - NVIDIA GeForce RTX 4070 Laptop GPU |
| 驱动版本 | 16.44.2.509 (Todesk)
  - 32.0.16.1074 (NVIDIA, 2026/7/2) |
| 驱动日期 | 2023/4/24 (Todesk)
  - 2026/7/2 (NVIDIA) |
| 刷新率 | 当前刷新率 240 Hz；面板最高刷新率 240 Hz；动态刷新率范围 103–240 Hz |
| 分辨率 | 2560 × 1600 |
| 缩放 | 150% |
| HDR 状态 | ON |
| 远程桌面/远程控制 | 未连接（ToDesk 未启动） |

> **注意：** 刷新率数据来自 DxDiag 采集时的实际状态和用户确认的动态范围。

## 5. 构建配置

| 字段 | 值 |
|---|---|
| 平台目标 | x64 |
| CsWin32 版本 | 0.3.298 |
| 隐式 using | 开启 |
| Nullable | 开启 |
| 允许不安全代码 | 开启 |

## 6. 软件环境

| 字段 | 值 |
|---|---|
| PowerShell 版本 | 5.1.26100.8894 |
| 执行策略 | Bypass |
| 调试层可用 | Unknown / not collected |

## 7. 用户核对记录

| 检查项 | 用户确认 |
|---|---|
| 以上字段是否出现用户名、计算机名、产品 ID、IP、MAC、设备序列号或绝对用户目录？ | 无泄露 |
| Windows build 字段是否不再自相矛盾？ | 确认 |
| .NET SDK 与 Host 版本是否已正确区分？ | 确认 |
| 刷新率字段是否已确认当前实际值？ | 确认 |
| 不知道的项目是否写为 Unknown / not collected？ | 是 |

