# Input: verified PublishDir; optional InstallerDir, OutputDir, SkipZip. Output: installer folder and ZIP.
# Usage: powershell -NoProfile -File tools/New-ReleasePackage.ps1 -PublishDir artifacts/publish-<timestamp> -OutputDir artifacts/package-<timestamp>
# Builds the folder and zip that get sent to an end user.
#
# The publish output is the single source of truth: it is the only folder guaranteed to contain the
# executable, the CAD plugin, the glossary and a valid settings.json. Client-side AI prompts are forbidden. This script adds
# the installer bootstrap and the user documentation, normalises the encoding of the Chinese
# command files (the bootstrap selects UTF-8; the readme uses UTF-8 with BOM), and
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
if (-not $PublishDir) { throw 'Pass -PublishDir with the verified artifacts/publish-<timestamp> directory; never package local release user data.' }
if (-not $InstallerDir) { $InstallerDir = Join-Path $scriptRoot '..\installer' }
if (-not $OutputDir) { $OutputDir = Join-Path $scriptRoot '..\artifacts' }

$publish = (Resolve-Path -LiteralPath $PublishDir).Path
$installer = (Resolve-Path -LiteralPath $InstallerDir).Path
# Resolve against the caller's PowerShell location, not the process working directory.
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..\artifacts')).TrimEnd('\')
if ($output.TrimEnd('\') -ne $artifactRoot -and -not $output.StartsWith($artifactRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package output must stay under workspace artifacts; source and installed release are not output locations.'
}

# Share the publish lock: normal delivery/cleanup must not remove this candidate mid-copy.
$packageLock = & (Join-Path $scriptRoot 'Enter-DesktopPublishLock.ps1') -WorkspaceRoot (Split-Path -Parent $scriptRoot)
try {
$buildInfo = & (Join-Path $scriptRoot 'Assert-CleanPackageInput.ps1') -PublishDir $publish
$exe = Join-Path $publish 'QLCAD.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "发布产物不完整：未找到 $exe（请先运行 publish.bat 或 dotnet publish）" }

# InformationalVersion ("2.1.0") is what the product advertises; FileVersion carries the extra
# revision component ("2.1.0.0"), which only makes the download name look odd.
$version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if ($version) { $version = $version.Split("+")[0] }   # 去掉 SDK 追加的源码修订后缀
if ([string]::IsNullOrWhiteSpace($version)) { $version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion }
$packageName = "DwgTranslator-$version-win-x64"
$stage = Join-Path $output $packageName

Write-Host ""
Write-Step "发布目录：$publish"
Write-Step "版本号：$version"
Write-Step "输出目录：$stage"

$outputFull = [IO.Path]::GetFullPath($output).TrimEnd('\')
$stage = [IO.Path]::GetFullPath($stage)
$ancestor = $outputFull
while ($ancestor) {
    if (Test-Path -LiteralPath $ancestor) {
        if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked package output is not supported.' }
    }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
if ($outputFull -eq $publish -or $outputFull.StartsWith($publish + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package output cannot be inside its input candidate.' }
if (-not $stage.StartsWith($outputFull + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package output path' }
if (Test-Path -LiteralPath $stage) { throw 'Package output already exists; choose a new OutputDir to preserve existing evidence.' }
$zip = Join-Path $output "$packageName.zip"
if (-not $SkipZip -and (Test-Path -LiteralPath $zip)) { throw 'Package ZIP already exists; choose a new OutputDir.' }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# ── 1. 程序本体 ──────────────────────────────────────────────
$required = @('QLCAD.exe', 'settings.json', 'build-info.json')
foreach ($name in $required) {
    $src = Join-Path $publish $name
    if (-not (Test-Path -LiteralPath $src)) { throw "缺少必需文件：$name" }
    if ($name -eq 'settings.json' -and (Get-Item -LiteralPath $src).Length -eq 0) { throw "settings.json 为空文件，请检查发布产物" }
    Copy-Item -LiteralPath $src -Destination (Join-Path $stage $name) -Force
    Write-Step "复制 $name"
}
foreach ($sub in 'CadPlugin', 'glossaries', 'assets') {
    $src = Join-Path $publish $sub
    if (-not (Test-Path -LiteralPath $src)) { throw "缺少必需目录：$sub" }
    Copy-Item -LiteralPath $src -Destination (Join-Path $stage $sub) -Recurse -Force
    $count = (Get-ChildItem -LiteralPath (Join-Path $stage $sub) -Recurse -File | Measure-Object).Count
    Write-Step "复制 $sub\（$count 个文件）"
}

# ── 2. 安装引导 ─────────────────────────────────────────────
$cmdUtf8 = New-Object System.Text.UTF8Encoding($false) # Bootstrap selects chcp 65001; no BOM before @echo.
$utf8Bom = New-Object System.Text.UTF8Encoding($true) # 记事本友好

function Convert-File([string]$path, [System.Text.Encoding]$target) {
    $text = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    if ([IO.Path]::GetExtension($path) -eq '.cmd') { $text = $text.Replace("`r`n", "`n").Replace("`n", "`r`n") }
    [IO.File]::WriteAllText($path, $text, $target)
}

# The bootstrap files are the only content added on top of the verified runtime payload. They are
# declared once here so section 3 can assert the exact stage inventory against one allowlist instead
# of trusting the copy calls; a new file in this list must be reviewed before it can ship.
$bootstrapFiles = @('Install.ps1', 'InstallTransaction.ps1', 'Uninstall.ps1', '安装.cmd', '使用说明.txt')
foreach ($name in $bootstrapFiles) {
    $src = Join-Path $installer $name
    if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { throw "缺少安装引导文件：$name" }
    Copy-Item -LiteralPath $src -Destination (Join-Path $stage $name) -Force
    Write-Step "复制 $name"
}
Convert-File (Join-Path $stage '安装.cmd') $cmdUtf8
Write-Step "写入 安装.cmd"
Convert-File (Join-Path $stage '使用说明.txt') $utf8Bom
Write-Step "写入 使用说明.txt"

# ── 3. 校验 ─────────────────────────────────────────────────
$files = Get-ChildItem -LiteralPath $stage -Recurse -File
$sizeMb = [Math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Step ("打包内容：{0} 个文件，{1} MB" -f $files.Count, $sizeMb)

# The stage must be exactly the verified candidate plus the bootstrap allowlist. Comparing two exact
# inventories (instead of a deny list) is what keeps an unreviewed file from riding into the ZIP.
$candidateFiles = @(Get-ChildItem -LiteralPath $publish -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($publish.Length + 1).Replace('\','/') })
$expectedFiles = @($candidateFiles + $bootstrapFiles)
$stageFiles = @(Get-ChildItem -LiteralPath $stage -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($stage.Length + 1).Replace('\','/') })
$unexpected = @($stageFiles | Where-Object { $expectedFiles -notcontains $_ })
if ($unexpected.Count -gt 0) { throw "打包结果含未审查文件：$($unexpected -join ', ')" }
$missingFiles = @($expectedFiles | Where-Object { $stageFiles -notcontains $_ })
if ($missingFiles.Count -gt 0) { throw "打包结果缺少已校验文件：$($missingFiles -join ', ')" }
Write-Step ("包内清单：{0} 个文件（候选 {1} + 安装引导 {2}）" -f $stageFiles.Count, $candidateFiles.Count, $bootstrapFiles.Count)

# The bootstrap files are plain, user-readable text inside the ZIP, so a server-side credential name
# or endpoint pasted in by mistake would be published verbatim. Only names that must never appear in
# a client-side bootstrap file are listed; ordinary installation paths stay allowed.
$bootstrapForbidden = @('DEEPSEEK_API_KEY', 'AI_CONFIG_ENCRYPTION_KEY', 'EZFPY_KEY', 'api.deepseek.com')
foreach ($name in $bootstrapFiles) {
    $text = Get-Content -LiteralPath (Join-Path $stage $name) -Raw -Encoding UTF8
    foreach ($forbidden in $bootstrapForbidden) {
        if ($text -match [regex]::Escape($forbidden)) { throw "安装引导文件 $name 暴露服务端凭据引用：$forbidden" }
    }
}
Write-Step "安装引导内容检查：PASS"

$plugin = Join-Path $stage 'CadPlugin\DwgTranslator.Cad.dll'
if (-not (Test-Path -LiteralPath $plugin)) { throw "打包结果缺少 CAD 插件：$plugin" }
Write-Step ("CAD 插件：{0} （{1} B）" -f (Split-Path $plugin -Leaf), (Get-Item -LiteralPath $plugin).Length)

if ((Get-FileHash -LiteralPath (Join-Path $stage 'QLCAD.exe') -Algorithm SHA256).Hash -ne $buildInfo.sha256) {
    throw 'Packaged executable hash differs from validated candidate; do not distribute this stage.'
}

# ── 4. 压缩包 ───────────────────────────────────────────────
if (-not $SkipZip) {
    $zip = Join-Path $output "$packageName.zip"
    if (Test-Path -LiteralPath $zip) { throw 'Package ZIP already exists; refusing overwrite.' }
    # Windows PowerShell 5.1 Compress-Archive internally expands bracket paths even
    # with -LiteralPath. ZipFile uses literal filesystem paths and excludes the stage root.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Step ("已生成压缩包：{0} （{1} MB）" -f $zip, [Math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1))
    Write-Host ""
    Write-Host "  发给用户的就是这个压缩包：" -ForegroundColor Green
    Write-Host "    $zip"
    Write-Host "  用户解压后双击 安装.cmd 即可。" -ForegroundColor Green
}

Write-Host ""
Write-Host "  免安装目录（可直接运行）：$stage"
Write-Host ""

} finally { if ($packageLock) { $packageLock.Dispose() } }
