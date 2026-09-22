# Input: verified package; output: installation directory and optional shortcuts.
# Isolated validation: -TargetDir <sandbox> -NoLaunch -NoPrompt -NoShortcuts.
# QLCAD — 一键安装（免管理员）
#
# 复制程序到选择的安装目录、创建快捷方式、写入卸载脚本。程序是自包含发布，目标机器无需安装
# .NET 运行时；唯一需要在目标机器上完成的环境动作是把 CAD 插件装进 CAD，安装完成后由
# 程序内的「更多 → 环境自检与安装」一键完成。
[CmdletBinding()]
param(
    [string]$SourceDir,
    [string]$TargetDir,
    [switch]$NoLaunch,
    [switch]$NoPrompt,
    [switch]$NoShortcuts
)

$ErrorActionPreference = 'Stop'

# First installation prefers D; explicit -TargetDir always wins. Never migrate existing data.
function Get-DefaultInstallDirectory([bool]$HasDDrive) {
    if ($HasDDrive) { return 'D:\QLCAD' }
    return 'C:\QLCAD'
}
if (-not $TargetDir) { $TargetDir = Get-DefaultInstallDirectory (Test-Path -LiteralPath 'D:\' -PathType Container) }

function Write-Step([string]$text) { Write-Host "  $text" }
function Fail([string]$text, [int]$code) {
    Write-Host ""
    Write-Host "  [错误] $text" -ForegroundColor Red
    exit $code
}

Write-Host ""
# Keep localized destination guidance in PowerShell, not the UTF-8 CMD parser.
Write-Step "安装默认使用 D:\QLCAD，没有 D 盘时使用 C:\QLCAD。"
Write-Step "安装目录需有写入权限；源码目录会被拒绝，请另选安装位置。"
Write-Step "安装目录：$TargetDir"

if (-not $SourceDir) { $SourceDir = $PSScriptRoot }
$exeSource = Join-Path $SourceDir 'QLCAD.exe'
if (-not (Test-Path $exeSource)) {
    Fail "安装包不完整：未找到 QLCAD.exe（当前位置 $SourceDir）。请把安装包解压后再运行。" 2
}

# Validate before touching an existing installation. Never replace a running application.
$SourceDir = [IO.Path]::GetFullPath($SourceDir).TrimEnd('\')
$TargetDir = [IO.Path]::GetFullPath($TargetDir).TrimEnd('\')
if ($SourceDir -eq $TargetDir -or $SourceDir.StartsWith($TargetDir + '\', [StringComparison]::OrdinalIgnoreCase) -or $TargetDir.StartsWith($SourceDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Source and target installation directories must not overlap.'
}
# The requested default may be a developer checkout on this machine; never install over source.
if ($TargetDir -eq [IO.Path]::GetPathRoot($TargetDir).TrimEnd('\') -or
    (Test-Path -LiteralPath (Join-Path $TargetDir 'DwgTranslator.sln')) -or
    (Test-Path -LiteralPath (Join-Path $TargetDir '.git'))) {
    throw 'Refusing to install into a drive root or source workspace. Choose a separate -TargetDir.'
}
$required = @('QLCAD.exe','settings.json','CadPlugin\DwgTranslator.Cad.dll','CadPlugin\DwgTranslator.Core.dll','CadPlugin\cad-platform.txt','glossaries\mechanical_zh_en.json','assets\default-glossaries\mechanical_zh_en.json')
foreach ($name in $required) {
    $file = Join-Path $SourceDir $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) { throw "Missing or empty package resource: $name" }
}
Get-Content -LiteralPath (Join-Path $SourceDir 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json | Out-Null
# 改名迁移（2026-09-23）：旧版可执行文件叫 DwgTranslator.exe，它已不属于本安装的写入集。
# 升级时显式删掉，避免同一目录里新旧两个 exe 并存、点错就启动旧版本。
$legacyExe = Join-Path $TargetDir 'DwgTranslator.exe'
if (Test-Path -LiteralPath $legacyExe -PathType Leaf) { Remove-Item -LiteralPath $legacyExe -Force }

$installedExe = Join-Path $TargetDir 'QLCAD.exe'
$running = Get-Process QLCAD -ErrorAction SilentlyContinue | Where-Object { $_.Path -and [IO.Path]::GetFullPath($_.Path) -eq $installedExe }
if ($running) { throw 'Close the installed application before updating. No processes were stopped.' }
# Refuse reparse points before recursive copying; do not traverse user-created links.
foreach ($dir in @($SourceDir, $TargetDir)) {
    $ancestor = $dir
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked install path: $ancestor" }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    if (Test-Path -LiteralPath $dir) {
        if (Get-ChildItem -LiteralPath $dir -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1) { throw "Linked package or installation: $dir" }
    }
}
$uninstallerSource=Join-Path $PSScriptRoot 'Uninstall.ps1'
if(-not(Test-Path -LiteralPath $uninstallerSource -PathType Leaf)){throw 'Missing safe uninstall helper'}
. (Join-Path $PSScriptRoot 'InstallTransaction.ps1')
Restore-PendingInstallTransaction $TargetDir
# Check every existing destination before the first copy, not only the executable.
# This detects current locks/read-only targets; it is not transaction rollback or a race guarantee.
$writeTargets=New-Object 'System.Collections.Generic.List[string]'
foreach($name in @('QLCAD.exe','使用说明.txt')) {
    if(Test-Path -LiteralPath (Join-Path $SourceDir $name) -PathType Leaf){$writeTargets.Add((Join-Path $TargetDir $name))}
}
foreach($sub in @('CadPlugin','glossaries','assets')) {
    $sourceFolder=Join-Path $SourceDir $sub
    foreach($file in Get-ChildItem -LiteralPath $sourceFolder -File -Recurse) {
        $relative=$file.FullName.Substring($sourceFolder.TrimEnd('\').Length+1)
        $destination=Join-Path (Join-Path $TargetDir $sub) $relative
        if($sub -eq 'glossaries' -and (Test-Path -LiteralPath $destination)){continue}
        $writeTargets.Add($destination)
    }
}
foreach($name in @('Uninstall.ps1','installation-manifest.json','卸载.cmd')){$writeTargets.Add((Join-Path $TargetDir $name))}
# Remove the one known legacy client-side system prompt. The exact file is snapshotted
# by the transaction so any later installation failure restores it byte-for-byte.
$obsoleteClientPrompt=Join-Path $TargetDir 'prompts\deepl_context.txt'
if(Test-Path -LiteralPath $obsoleteClientPrompt -PathType Leaf){$writeTargets.Add($obsoleteClientPrompt)}
foreach($destination in $writeTargets) {
    if(Test-Path -LiteralPath $destination) {
        if(-not(Test-Path -LiteralPath $destination -PathType Leaf)){throw "Destination is not a file: $destination"}
        if((Get-Item -LiteralPath $destination -Force).IsReadOnly){throw "Read-only installation file: $destination"}
        $probe=$null
        try {$probe=[IO.File]::Open($destination,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
        finally {if($probe){$probe.Dispose()}}
    }
}

$script:InstallTransaction=Start-InstallTransaction $TargetDir
try {
# Snapshot every planned file before any target mutation, including fresh configuration.
foreach($destination in $writeTargets){
    $relative=$destination.Substring($TargetDir.Length+1)
    $packageFile=Join-Path $SourceDir $relative
    $expectedHash=$null
    if(Test-Path -LiteralPath $packageFile -PathType Leaf){$expectedHash=(Get-InstallFileSha256 $packageFile)}
    Register-InstallWrite $script:InstallTransaction $destination $expectedHash
}
if(-not(Test-Path -LiteralPath (Join-Path $TargetDir 'settings.json'))){Register-InstallWrite $script:InstallTransaction (Join-Path $TargetDir 'settings.json') (Get-InstallFileSha256 (Join-Path $SourceDir 'settings.json'))}
New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
foreach ($name in @('QLCAD.exe','settings.json','使用说明.txt')) {
    $src = Join-Path $SourceDir $name
    $dst = Join-Path $TargetDir $name
    if (-not (Test-Path -LiteralPath $src)) { continue }
    if ($name -eq 'settings.json' -and (Test-Path -LiteralPath $dst)) { Write-Step 'Preserved existing settings.json'; continue }
    # On copy failure stop, rather than deleting the existing file and retrying.
    Copy-Item -LiteralPath $src -Destination $dst -Force
    Write-Step "复制 $name"
}
foreach ($sub in @('CadPlugin','glossaries','assets')) {
    $sourceFolder = Join-Path $SourceDir $sub
    $prefix = $sourceFolder.TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -LiteralPath $sourceFolder -File -Recurse) {
        $relative = $file.FullName.Substring($prefix.Length)
        $dst = Join-Path (Join-Path $TargetDir $sub) $relative
        if ($sub -eq 'glossaries' -and (Test-Path -LiteralPath $dst)) { continue }
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($dst)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $dst -Force
    }
    Write-Step "复制 $sub（保留已有用户词库）"
}
if(Test-Path -LiteralPath $obsoleteClientPrompt -PathType Leaf){
    Remove-Item -LiteralPath $obsoleteClientPrompt -Force
    Write-Step 'Removed obsolete client-side AI prompt'
}


# Retain previous ownership when reinstalling without creating shortcuts.
$ownedShortcuts=New-Object 'System.Collections.Generic.List[object]'
$previousManifest=Join-Path $TargetDir 'installation-manifest.json'
if(Test-Path -LiteralPath $previousManifest -PathType Leaf){
    try {
        $previous=Get-Content -LiteralPath $previousManifest -Raw -Encoding UTF8 | ConvertFrom-Json
        if($previous.Schema -eq 1 -and $previous.Product -eq 'QLCAD' -and $previous.Root -eq $TargetDir){
            foreach($record in @($previous.Shortcuts)){
                if($record.Kind -in @('StartMenu','Desktop') -and $record.Sha256 -match '^[a-fA-F0-9]{64}$'){
                    $ownedShortcuts.Add(@{Kind=$record.Kind;Sha256=$record.Sha256})
                }
            }
        }
    } catch { Write-Step 'Previous shortcut ownership unavailable; unregistered shortcuts will be preserved.' }
}
# ── 3. 快捷方式 ─────────────────────────────────────────────
$exe = Join-Path $TargetDir 'QLCAD.exe'
function New-Shortcut([string]$linkPath, [string]$target, [string]$workDir, [string]$description, [string]$kind) {
    # Never overwrite a different installation's link or traverse redirected paths.
    $ancestor=[IO.Path]::GetFullPath($linkPath)
    while($ancestor){
        if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){
            Write-Step 'Preserved linked shortcut location';return
        }
        $ancestor=[IO.Path]::GetDirectoryName($ancestor)
    }
    $shell=$null;$shortcut=$null
    try {
        $shell=New-Object -ComObject WScript.Shell
        $shortcut=$shell.CreateShortcut($linkPath)
        if((Test-Path -LiteralPath $linkPath) -and
           ($shortcut.TargetPath -ne $target -or -not [string]::IsNullOrWhiteSpace($shortcut.Arguments))){
            Write-Step 'Preserved shortcut belonging to another target or custom command';return
        }
        if(Test-Path -LiteralPath $linkPath){
            $currentHash=(Get-InstallFileSha256 $linkPath)
            $previousOwnership=@($ownedShortcuts | Where-Object {$_.Kind -eq $kind -and $_.Sha256 -eq $currentHash})
            if($previousOwnership.Count -ne 1){
                Write-Step 'Preserved unregistered or user-modified shortcut';return
            }
        }
        $shortcut.TargetPath=$target
        $shortcut.WorkingDirectory=$workDir
        $shortcut.IconLocation="$target,0"
        $shortcut.Description=$description
        if($script:InstallTransaction){Register-InstallWrite $script:InstallTransaction $linkPath}
        $shortcut.Save()
        for($i=$ownedShortcuts.Count-1;$i -ge 0;$i--){if($ownedShortcuts[$i].Kind -eq $kind){$ownedShortcuts.RemoveAt($i)}}
        $ownedShortcuts.Add(@{Kind=$kind;Sha256=(Get-InstallFileSha256 $linkPath)})
        Write-Step "Created owned shortcut: $kind"
    } finally {
        if($shortcut){[Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) | Out-Null}
        if($shell){[Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null}
    }
}

if (-not $NoShortcuts) {
$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
New-Item -ItemType Directory -Force -Path $startMenuDir | Out-Null
$startMenuLink = Join-Path $startMenuDir 'QLCAD.lnk'
New-Shortcut $startMenuLink $exe $TargetDir 'QLCAD — CAD 图纸翻译' 'StartMenu'


$wantDesktop = $true
if (-not $NoPrompt) {
    $answer = Read-Host "是否在桌面创建快捷方式？(Y/n)"
    if ($answer -match '^(n|N)') { $wantDesktop = $false }
}
if ($wantDesktop) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    New-Shortcut (Join-Path $desktop 'QLCAD.lnk') $exe $TargetDir 'QLCAD — CAD 图纸翻译' 'Desktop'

}

}

# Record program ownership only after successful copy; never claim personal data.
$owned=New-Object 'System.Collections.Generic.List[object]'
foreach($name in @('QLCAD.exe','使用说明.txt')) {
    $src=Join-Path $SourceDir $name
    $dst=Join-Path $TargetDir $name
    if((Test-Path -LiteralPath $src -PathType Leaf) -and (Test-Path -LiteralPath $dst -PathType Leaf)) {
        $owned.Add(@{Path=$name;Sha256=(Get-InstallFileSha256 $dst)})
    }
}
foreach($sub in @('CadPlugin','assets')) {
    $sourceFolder=Join-Path $SourceDir $sub
    foreach($file in Get-ChildItem -LiteralPath $sourceFolder -File -Recurse) {
        $relative=$sub+'\'+$file.FullName.Substring($sourceFolder.TrimEnd('\').Length+1)
        $owned.Add(@{Path=$relative;Sha256=(Get-InstallFileSha256 (Join-Path $TargetDir $relative))})
    }
}
Copy-Item -LiteralPath $uninstallerSource -Destination (Join-Path $TargetDir 'Uninstall.ps1') -Force
@{Schema=1;Product='QLCAD';Root=$TargetDir;Files=$owned.ToArray();Shortcuts=$ownedShortcuts.ToArray()} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $TargetDir 'installation-manifest.json') -Encoding UTF8

# ── 4. 卸载脚本 ─────────────────────────────────────────────
$uninstall = @'
@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1"
pause
'@
Set-Content -Path (Join-Path $TargetDir '卸载.cmd') -Value $uninstall -Encoding Default
Write-Step "写入 卸载.cmd"

} catch {
    $installationError=$_
    try {Undo-InstallTransaction $script:InstallTransaction; Write-Step 'INSTALL_ROLLBACK=PASS'}
    catch {Write-Warning ("INSTALL_ROLLBACK=INCOMPLETE; preserve recovery evidence: "+$script:InstallTransaction.Backup+"; "+$_.Exception.Message)}
    throw $installationError
}
# Completion is after the manifest and both uninstall entry points have been written.
$script:InstallTransaction.State='Committed'
Save-InstallTransaction $script:InstallTransaction
try {Clear-InstallTransaction $script:InstallTransaction}
catch {Write-Warning ("Installed successfully; recovery evidence retained: "+$script:InstallTransaction.Backup)}
$script:InstallTransaction=$null

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
