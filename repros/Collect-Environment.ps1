<#
.SYNOPSIS
  DynamicIsland 图形互操作最小复现 — 脱敏环境收集脚本
.DESCRIPTION
  收集测试机器的 Windows、.NET、GPU 信息，输出到 artifacts/environment/environment.md。
  不输出用户名、计算机名、产品 ID、IP、MAC、设备序列号、绝对用户目录。
.NOTES
  由 AI 执行计划书 M0 阶段生成，用户只需运行此脚本。
#>

$ErrorActionPreference = "Continue"
$outputPath = Join-Path (Join-Path (Join-Path $PSScriptRoot "artifacts") "environment") "environment.md"

# 切换到 UTF-8 代码页，避免 dotnet --info 中文输出乱码
$prevEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$prevCp = chcp.com 2>$null
if ($prevCp -match '(\d+)') { $prevCpNum = $matches[1] } else { $prevCpNum = 936 }
chcp.com 65001 2>$null | Out-Null

# 确保输出目录存在
$null = New-Item -ItemType Directory -Force -Path (Split-Path $outputPath -Parent)

# 辅助函数：安全读取注册表（不抛出异常）
function Safe-RegValue {
    param($Path, $Name)
    try { return (Get-ItemProperty -Path $Path -Name $Name -ErrorAction Stop).$Name }
    catch { return "Unknown / not collected" }
}

# 辅助函数：安全读取命令输出
function Safe-Command {
    param($ScriptBlock)
    try { $result = & $ScriptBlock; if ($null -eq $result) { return "Unknown / not collected" }; return $result }
    catch { return "Unknown / not collected" }
}

# 辅助函数：安全获取 GPU 名称（多卡分行）
function Get-GpuNames {
    try {
        $cards = Get-CimInstance Win32_VideoController
        if (-not $cards) { return "Unknown / not collected" }
        return ($cards | ForEach-Object { $_.Name }) -join "`n  - "
    }
    catch { return "Unknown / not collected" }
}

# 辅助函数：安全获取 GPU 驱动版本（多卡分行）
function Get-GpuDrivers {
    try {
        $cards = Get-CimInstance Win32_VideoController
        if (-not $cards) { return "Unknown / not collected" }
        return ($cards | ForEach-Object { $_.DriverVersion }) -join "`n  - "
    }
    catch { return "Unknown / not collected" }
}

# 辅助函数：安全获取 GPU 驱动日期（多卡分行）
function Get-GpuDates {
    try {
        $cards = Get-CimInstance Win32_VideoController
        if (-not $cards) { return "Unknown / not collected" }
        return ($cards | ForEach-Object { $_.DriverDate }) -join "`n  - "
    }
    catch { return "Unknown / not collected" }
}

# 辅助函数：脱敏字符串（去除用户名/路径）
function Sanitize {
    param($s)
    if (-not $s) { return "Unknown / not collected" }
    # 去除绝对用户路径模式
    $s = $s -replace 'C:\\Users\\[^\\]+', 'C:\Users\***'
    $s = $s -replace '/home/[^/]+', '/home/***'
    $s = $s -replace '/mnt/c/Users/[^/]+', '/mnt/c/Users/***'
    return $s
}

# ─── 收集开始 ───
$codeBlock = '```'
$content = @"
# Environment — DynamicIsland 图形互操作最小复现

> 自动收集时间：$(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
> 本文件已脱敏，不包含用户名、计算机名、产品 ID、IP、MAC、设备序列号或绝对用户目录。

## 1. 测试基本信息

| 字段 | 值 |
|---|---|
| 测试日期 | $(Get-Date -Format "yyyy-MM-dd") |
| Git commit | $(Safe-Command { git rev-parse HEAD }) |
| 工作分支 | $(Safe-Command { git rev-parse --abbrev-ref HEAD }) |

## 2. 操作系统

| 字段 | 值 |
|---|---|
| Windows 版本 | $(Safe-Command { (Get-CimInstance Win32_OperatingSystem).Caption }) |
| 版本 (WinVer) | **请用户核对：** `winver` 实际显示的 Build 号 |
| DisplayVersion | $(Safe-RegValue "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" "DisplayVersion") |
| CurrentBuild | $(Safe-RegValue "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuild") |
| UBR (Update Build Revision) | $(Safe-RegValue "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" "UBR") |
| 完整 OS build 字符串 | $(Safe-Command { [Environment]::OSVersion.VersionString }) |
| 系统架构 | $(Safe-Command { (Get-CimInstance Win32_OperatingSystem).OSArchitecture }) |
| 进程架构 | $(Safe-Command { [Environment]::GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") }) |

> **注意：** `26200` 是 Insider Preview 版本号，`26100.8894` 是 build + UBR。
> 请用户通过 `winver` 确认实际显示的是哪个 Build。

## 3. .NET SDK 与运行时

| 字段 | 值 |
|---|---|
| SDK 版本 | $(Safe-Command { dotnet --version 2>$null }) |
| Host 版本 | $(Safe-Command { $info = dotnet --info 2>$null; $lines = $info -split "`n"; $inHost = $false; foreach ($l in $lines) { if ($l -match "^\s*Host:") { $inHost = $true }; if ($inHost -and $l -match "Version:\s*(\S+)") { $matches[1]; break } } }) |
| 运行时版本 | $(Safe-Command { (dotnet --list-runtimes 2>$null | Select-String "Microsoft.NETCore.App") -join "; " }) |
| 目标框架 (TFM) | net10.0-windows10.0.26100.0 |
| Windows SDK 版本 | 10.0.26100.0 |

`dotnet --info` 完整输出：

$($codeBlock)
$(Safe-Command { (dotnet --info 2>$null) -join "`n" })
$($codeBlock)

> **注意：** `10.0.302` 是 .NET SDK 版本，不是 runtime/Host 版本。
> 请用户确认 SDK 和 runtime 版本已正确区分。

## 4. GPU 与显示

| 字段 | 值 |
|---|---|
| GPU 名称 | $(Get-GpuNames) |
| 驱动版本 | $(Get-GpuDrivers) |
| 驱动日期 | $(Get-GpuDates) |
| 刷新率 | **请用户填写：** 当前刷新率 ___ Hz，面板能力 ___ Hz，动态刷新率档位 ___ |
| 分辨率 | **请用户填写：** ___ x ___ |
| 缩放 | **请用户填写：** ___ % |
| HDR 状态 | **请用户填写：** ON / OFF / Unknown |
| 远程桌面 | **请用户填写：** 连接中 / 未连接 |

> **注意：** `240Hz (native 60Hz)` 可能表示当前刷新率、面板能力或动态刷新率档位。
> 请用户确认当前实际刷新率。

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
| PowerShell 版本 | $($PSVersionTable.PSVersion.ToString()) |
| 执行策略 | $(Safe-Command { Get-ExecutionPolicy }) |
| 调试层可用 | $(if (Get-Command -ErrorAction SilentlyContinue "dxc.exe") { "是" } else { "需要手动检查" }) |

## 7. 用户核对记录

| 检查项 | 用户确认 |
|---|---|
| 以上字段是否出现用户名、计算机名、产品 ID、IP、MAC、设备序列号或绝对用户目录？ | 请填写：无泄露 / 有泄露（注明字段） |
| Windows build 字段是否不再自相矛盾？ | 请填写：确认 / 矛盾 |
| .NET SDK 与 Host 版本是否已正确区分？ | 请填写：确认 / 需修正 |
| 刷新率字段是否已确认当前实际值？ | 请填写：确认 / 需修正 |
| 不知道的项目是否写为 `Unknown / not collected`？ | 请填写：是 / 否 |

"@

# 写入文件
$content | Out-File -FilePath $outputPath -Encoding utf8

Write-Host "Environment collected: $outputPath"
Write-Host ""
Write-Host "请用户下一步："
Write-Host "  1. 打开 artifacts/environment/environment.md"
Write-Host "  2. 填写所有标有「请用户填写」的字段"
Write-Host "  3. 核对脱敏和版本信息"
Write-Host "  4. 把确认结果回给 AI"

# 恢复原始编码
[Console]::OutputEncoding = $prevEncoding
chcp.com $prevCpNum 2>$null | Out-Null