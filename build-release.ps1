<#
.SYNOPSIS
    DynamicIsland 一键发布脚本
.DESCRIPTION
    构建 + 发布 + 生成安装包 (Inno Setup)
    需要 Inno Setup 6 安装后在 PATH 中存在 ISCC.exe
#>

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Join-Path $RootDir "DynamicIsland"
$OutputDir = Join-Path $RootDir "publish"
$Version = "1.0.0"

Write-Host "=== DynamicIsland 构建发布 ===" -ForegroundColor Cyan
Write-Host "版本: $Version" -ForegroundColor Gray

# ─── 1. 清理 ────────────────────────────────────────────────
Write-Host "`n[1/4] 清理旧构建..." -ForegroundColor Yellow
if (Test-Path $OutputDir) {
    Remove-Item -Recurse -Force $OutputDir
}

# ─── 2. 构建 ────────────────────────────────────────────────
Write-Host "[2/4] 构建 Release..." -ForegroundColor Yellow
dotnet build "$ProjectDir\DynamicIsland.csproj" -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

# ─── 3. 发布 ────────────────────────────────────────────────
Write-Host "[3/4] 发布到 $OutputDir ..." -ForegroundColor Yellow
dotnet publish "$ProjectDir\DynamicIsland.csproj" -c Release -o $OutputDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "发布失败" }

# 清理发布目录中不必要的文件
Get-ChildItem $OutputDir -Include "*.pdb", "*.xml" -Recurse | Remove-Item -Force

Write-Host "      发布文件数: $((Get-ChildItem $OutputDir -File).Count)" -ForegroundColor Gray
$size = "{0:N2} MB" -f ((Get-ChildItem $OutputDir -Recurse | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "      总大小: $size" -ForegroundColor Gray

# ─── 4. 安装包 ──────────────────────────────────────────────
$IsccPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (Test-Path $IsccPath) {
    Write-Host "[4/4] 生成安装包..." -ForegroundColor Yellow
    & $IsccPath "/dMyAppVersion=$Version" "/dMyAppOutputDir=$OutputDir" "$RootDir\setup.iss"
    if ($LASTEXITCODE -ne 0) { throw "安装包生成失败" }
    Write-Host "      安装包: $OutputDir\DynamicIsland-Setup-$Version.exe" -ForegroundColor Green
} else {
    Write-Host "[4/4] 跳过安装包 — 未检测到 Inno Setup" -ForegroundColor DarkYellow
    Write-Host "      安装 Inno Setup 6 后重新运行此脚本" -ForegroundColor DarkYellow
    Write-Host "      下载: https://jrsoftware.org/isdl.php" -ForegroundColor DarkYellow
}

Write-Host "`n=== 完成 ===" -ForegroundColor Green
