# DWG Translator — 一键安装（免管理员）
#
# 复制程序到用户目录、创建快捷方式、写入卸载脚本。程序是自包含发布，目标机器无需安装
# .NET 运行时；唯一需要在目标机器上完成的环境动作是把 CAD 插件装进 CAD，安装完成后由
# 程序内的「更多 → 环境自检与安装」一键完成。
[CmdletBinding()]
param(
    [string]$SourceDir = $PSScriptRoot,
    [string]$TargetDir = "$env:LOCALAPPDATA\Programs\DwgTranslator",
    [switch]$NoLaunch,
    [switch]$NoPrompt
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$text) { Write-Host "  $text" }
function Fail([string]$text, [int]$code) {
    Write-Host ""
    Write-Host "  [错误] $text" -ForegroundColor Red
    exit $code
}

Write-Host ""
Write-Step "安装目录：$TargetDir"

if (-not $SourceDir) { $SourceDir = (Get-Location).Path }
$exeSource = Join-Path $SourceDir 'DwgTranslator.exe'
if (-not (Test-Path $exeSource)) {
    Fail "安装包不完整：未找到 DwgTranslator.exe（当前位置 $SourceDir）。请把安装包解压后再运行。", 2
}

# ── 1. 准备目录 ─────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
foreach ($sub in 'CadPlugin', 'prompts', 'glossaries') {
    $src = Join-Path $SourceDir $sub
    if (Test-Path $src) { New-Item -ItemType Directory -Force -Path (Join-Path $TargetDir $sub) | Out-Null }
}

# ── 2. 复制文件 ─────────────────────────────────────────────
$copyList = @('DwgTranslator.exe', 'settings.json', '使用说明.txt')
foreach ($name in $copyList) {
    $src = Join-Path $SourceDir $name
    if (-not (Test-Path $src)) { continue }
    $dst = Join-Path $TargetDir $name
    try {
        Copy-Item $src $dst -Force
    } catch {
        # 目标程序正在运行时会锁定 exe。改名腾出位置再放新文件，正在运行的那份不受影响，
        # 下次启动即使用新版本。
        $old = "$dst.old"
        Remove-Item $old -Force -ErrorAction SilentlyContinue
        try {
            Move-Item $dst $old -Force
            Copy-Item $src $dst -Force
            Write-Step "已更新正在运行的程序（旧版本保留为 $(Split-Path $old -Leaf)）"
        } catch {
            Remove-Item $dst -Force -ErrorAction SilentlyContinue
            Copy-Item $src $dst -Force
        }
    }
    Write-Step "复制 $name"
}

foreach ($sub in 'CadPlugin', 'prompts', 'glossaries') {
    $src = Join-Path $SourceDir $sub
    if (Test-Path $src) {
        Copy-Item (Join-Path $src '*') (Join-Path $TargetDir $sub) -Recurse -Force
        Write-Step "复制 $sub\"
    }
}

# ── 3. 快捷方式 ─────────────────────────────────────────────
$exe = Join-Path $TargetDir 'DwgTranslator.exe'
function New-Shortcut([string]$linkPath, [string]$target, [string]$workDir, [string]$description) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($linkPath)
    $shortcut.TargetPath = $target
    $shortcut.WorkingDirectory = $workDir
    $shortcut.IconLocation = "$target,0"
    $shortcut.Description = $description
    $shortcut.Save()
    [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
}

$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$startMenuLink = Join-Path $startMenuDir 'DWG Translator.lnk'
New-Shortcut $startMenuLink $exe $TargetDir 'DWG Translator — CAD 图纸翻译'
Write-Step "创建开始菜单快捷方式"

$wantDesktop = $true
if (-not $NoPrompt) {
    $answer = Read-Host "是否在桌面创建快捷方式？(Y/n)"
    if ($answer -match '^(n|N)') { $wantDesktop = $false }
}
if ($wantDesktop) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    New-Shortcut (Join-Path $desktop 'DWG Translator.lnk') $exe $TargetDir 'DWG Translator — CAD 图纸翻译'
    Write-Step "创建桌面快捷方式"
}

# ── 4. 卸载脚本 ─────────────────────────────────────────────
$uninstall = @'
@echo off
chcp 65001 >nul
echo 正在卸载 DWG Translator ...
taskkill /IM DwgTranslator.exe /F >nul 2>&1
del "%APPDATA%\Microsoft\Windows\Start Menu\Programs\DWG Translator.lnk" >nul 2>&1
del "%USERPROFILE%\Desktop\DWG Translator.lnk" >nul 2>&1
cd /d "%TEMP%"
rmdir /s /q "%~dp0"
echo 已卸载（用户配置与翻译缓存保留在 %APPDATA%\DwgTranslator，可自行删除）。
timeout /t 5 >nul
'@
Set-Content -Path (Join-Path $TargetDir '卸载.cmd') -Value $uninstall -Encoding Default
Write-Step "写入 卸载.cmd"

# ── 5. 完成 ────────────────────────────────────────────────
Write-Host ""
Write-Host "  安装完成：$TargetDir" -ForegroundColor Green
Write-Host "  程序即将启动，并直接停在「环境自检与安装」页 ——"
Write-Host "  在那里点「安装插件」即可把 CAD 插件装进本机的 CAD（浩辰/GstarCAD/AutoCAD）。"
Write-Host ""

if (-not $NoLaunch) {
    Start-Process $exe -ArgumentList '--env-check'
}

exit 0
