# Builds the folder and zip that get sent to an end user.
#
# The publish output is the single source of truth: it is the only folder guaranteed to contain the
# executable, the CAD plugin, the prompts, the glossary and a valid settings.json. This script adds
# the installer bootstrap and the user documentation, normalises the encoding of the Chinese
# command files (cmd.exe reads them as ANSI/GBK, Notepad wants UTF-8 with BOM for the readme), and
# zips the result.
[CmdletBinding()]
param(
    [string]$PublishDir,
    [string]$InstallerDir,
    [string]$OutputDir,
    [switch]$SkipZip
)

$ErrorActionPreference = 'Stop'
function Write-Step([string]$t) { Write-Host "  $t" }

# $PSScriptRoot is not populated while parameter defaults are evaluated under Windows PowerShell 5.1,
# so the paths are resolved in the body instead of in the param block.
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $PublishDir) { $PublishDir = Join-Path $scriptRoot '..\artifacts\publish' }
if (-not $InstallerDir) { $InstallerDir = Join-Path $scriptRoot '..\installer' }
if (-not $OutputDir) { $OutputDir = Join-Path $scriptRoot '..\artifacts' }

$publish = (Resolve-Path $PublishDir).Path
$installer = (Resolve-Path $InstallerDir).Path
$output = $OutputDir
New-Item -ItemType Directory -Force -Path $output | Out-Null

$exe = Join-Path $publish 'DwgTranslator.exe'
if (-not (Test-Path $exe)) { throw "发布产物不完整：未找到 $exe（请先运行 publish.bat 或 dotnet publish）" }

# InformationalVersion ("2.1.0") is what the product advertises; FileVersion carries the extra
# revision component ("2.1.0.0"), which only makes the download name look odd.
$version = (Get-Item $exe).VersionInfo.ProductVersion
if ($version) { $version = $version.Split("+")[0] }   # 去掉 SDK 追加的源码修订后缀
if ([string]::IsNullOrWhiteSpace($version)) { $version = (Get-Item $exe).VersionInfo.FileVersion }
$packageName = "DwgTranslator-$version-win-x64"
$stage = Join-Path $output $packageName

Write-Host ""
Write-Step "发布目录：$publish"
Write-Step "版本号：$version"
Write-Step "输出目录：$stage"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# ── 1. 程序本体 ──────────────────────────────────────────────
$required = @('DwgTranslator.exe', 'settings.json')
foreach ($name in $required) {
    $src = Join-Path $publish $name
    if (-not (Test-Path $src)) { throw "缺少必需文件：$name" }
    if ($name -eq 'settings.json' -and (Get-Item $src).Length -eq 0) { throw "settings.json 为空文件，请检查发布产物" }
    Copy-Item $src (Join-Path $stage $name) -Force
    Write-Step "复制 $name"
}
foreach ($sub in 'CadPlugin', 'prompts', 'glossaries') {
    $src = Join-Path $publish $sub
    if (-not (Test-Path $src)) { throw "缺少必需目录：$sub" }
    Copy-Item $src (Join-Path $stage $sub) -Recurse -Force
    $count = (Get-ChildItem (Join-Path $stage $sub) -Recurse -File | Measure-Object).Count
    Write-Step "复制 $sub\（$count 个文件）"
}

# ── 2. 安装引导 ─────────────────────────────────────────────
$ansi = [System.Text.Encoding]::GetEncoding(936)     # GBK：cmd.exe 按 ANSI 读取批处理
$utf8Bom = New-Object System.Text.UTF8Encoding($true) # 记事本友好

function Convert-File([string]$path, [System.Text.Encoding]$target) {
    $text = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText($path, $text, $target)
}

Copy-Item (Join-Path $installer 'Install.ps1') (Join-Path $stage 'Install.ps1') -Force
Write-Step "复制 Install.ps1"

Copy-Item (Join-Path $installer '安装.cmd') (Join-Path $stage '安装.cmd') -Force
Convert-File (Join-Path $stage '安装.cmd') $ansi
Write-Step "写入 安装.cmd"

Copy-Item (Join-Path $installer '使用说明.txt') (Join-Path $stage '使用说明.txt') -Force
Convert-File (Join-Path $stage '使用说明.txt') $utf8Bom
Write-Step "写入 使用说明.txt"

# ── 3. 校验 ─────────────────────────────────────────────────
$files = Get-ChildItem $stage -Recurse -File
$sizeMb = [Math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Step ("打包内容：{0} 个文件，{1} MB" -f $files.Count, $sizeMb)
$plugin = Join-Path $stage 'CadPlugin\DwgTranslator.Cad.dll'
if (-not (Test-Path $plugin)) { throw "打包结果缺少 CAD 插件：$plugin" }
Write-Step ("CAD 插件：{0} （{1} B）" -f (Split-Path $plugin -Leaf), (Get-Item $plugin).Length)

# ── 4. 压缩包 ───────────────────────────────────────────────
if (-not $SkipZip) {
    $zip = Join-Path $output "$packageName.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Step ("已生成压缩包：{0} （{1} MB）" -f $zip, [Math]::Round((Get-Item $zip).Length / 1MB, 1))
    Write-Host ""
    Write-Host "  发给用户的就是这个压缩包：" -ForegroundColor Green
    Write-Host "    $zip"
    Write-Host "  用户解压后双击 安装.cmd 即可。" -ForegroundColor Green
}

Write-Host ""
Write-Host "  免安装目录（可直接运行）：$stage"
Write-Host ""
