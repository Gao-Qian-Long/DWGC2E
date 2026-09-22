# Purpose: remove only unchanged, manifest-owned portable program files.
# Usage: .\Uninstall.ps1 [-ConfirmRemoval] [-Preview]. No recursive directory deletion.
[CmdletBinding()]
param([switch]$ConfirmRemoval,[switch]$Preview)
$ErrorActionPreference='Stop'
function Get-UninstallFileSha256([string]$LiteralPath) {
    # Keep uninstall independent from PowerShell module discovery/autoload state.
    $stream=$null
    $sha=$null
    try {
        $stream=[IO.File]::Open($LiteralPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        $sha=[Security.Cryptography.SHA256]::Create()
        return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')
    } finally {
        if($sha){$sha.Dispose()}
        if($stream){$stream.Dispose()}
    }
}
$root=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
function Assert-NoLink([string]$path) {
    $current=$path
    while($current) {
        if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked uninstall path rejected: $current" }
        $current=[IO.Path]::GetDirectoryName($current)
    }
}
Assert-NoLink $root
if($root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\') -or (Test-Path -LiteralPath (Join-Path $root 'DwgTranslator.sln')) -or (Test-Path -LiteralPath (Join-Path $root '.git'))) { throw 'Refusing to uninstall a drive root or source workspace' }
$manifestPath=Join-Path $root 'installation-manifest.json'
Assert-NoLink $manifestPath
$manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if($manifest.Schema -ne 1 -or $manifest.Product -ne 'QLCAD' -or $manifest.Root -ne $root) { throw 'Invalid installation manifest; no files removed' }
if(@($manifest.Files).Count -gt 10000 -or @($manifest.Files).Count -eq 0) { throw 'Invalid manifest file count' }
$exe=Join-Path $root 'QLCAD.exe'
$running=Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -and ([IO.Path]::GetFullPath($_.Path) -eq $exe) } catch { $false } }
if($running){throw 'Close this installed application first; no process was stopped'}
$remove=New-Object 'System.Collections.Generic.List[object]'
$keep=New-Object 'System.Collections.Generic.List[string]'
$seen=@{}
foreach($entry in $manifest.Files) {
    $relative=[string]$entry.Path
    if($relative -notmatch '^(QLCAD\.exe|使用说明\.txt|(?:CadPlugin|assets)\\[^:]+)$' -or
       $relative -match '(^|[\\/])\.\.?([\\/]|$)' -or $relative.Contains('/') -or
       [IO.Path]::IsPathRooted($relative) -or $entry.Sha256 -notmatch '^[A-Fa-f0-9]{64}$') {throw "Unsafe manifest entry: $relative"}
    $path=[IO.Path]::GetFullPath((Join-Path $root $relative))
    if(-not $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($path)){throw 'Escaping or duplicate manifest path'}
    $seen[$path]=$true
    Assert-NoLink $path
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){continue}
    if((Get-UninstallFileSha256 $path) -ne $entry.Sha256){$keep.Add($relative);continue}
    $handle=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $handle.Dispose()
    $remove.Add(@{Path=$path;Sha256=$entry.Sha256})
}
# Optional Schema 1 extension. Old manifests without Shortcuts remain compatible.
# Never accept arbitrary external paths from the manifest: derive two exact current-user locations.
$shortcutRecords=@($manifest.Shortcuts | Where-Object { $null -ne $_ })
if($shortcutRecords.Count -gt 2){throw 'Invalid shortcut ownership count'}
$shortcutKinds=@{}
foreach($record in $shortcutRecords){
    if($record.Kind -notin @('StartMenu','Desktop') -or $record.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $shortcutKinds.ContainsKey([string]$record.Kind)){
        throw 'Invalid shortcut ownership record'
    }
    $shortcutKinds[[string]$record.Kind]=$true
    $folder=if($record.Kind -eq 'Desktop'){[Environment]::GetFolderPath('Desktop')}else{Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'}
    if([string]::IsNullOrWhiteSpace($folder)){continue}
    $path=[IO.Path]::GetFullPath((Join-Path $folder 'QLCAD.lnk'))
    Assert-NoLink $path
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){continue}
    if((Get-UninstallFileSha256 $path) -ne $record.Sha256){$keep.Add('Modified '+$record.Kind+' shortcut');continue}
    $shell=$null;$shortcut=$null
    try {
        $shell=New-Object -ComObject WScript.Shell
        $shortcut=$shell.CreateShortcut($path)
        $isOwned=($shortcut.TargetPath -eq $exe -and [string]::IsNullOrWhiteSpace($shortcut.Arguments))
    } finally {
        if($shortcut){[Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) | Out-Null}
        if($shell){[Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null}
    }
    if(-not $isOwned){$keep.Add('Different-target '+$record.Kind+' shortcut');continue}
    if($seen.ContainsKey($path)){throw 'Duplicate uninstall path'}
    $seen[$path]=$true
    $handle=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $handle.Dispose()
    $remove.Add(@{Path=$path;Sha256=$record.Sha256})
}
Write-Host ("Unchanged owned files/shortcuts eligible for removal: " + $remove.Count)
foreach($relative in $keep){Write-Host "Preserved modified file: $relative"}
Write-Host 'User settings, glossaries, output, logs, unknown files and AppData are preserved.'
if($Preview){Write-Host 'PREVIEW_ONLY';exit 0}
if(-not $ConfirmRemoval){if((Read-Host 'Type UNINSTALL to remove only unchanged program files') -cne 'UNINSTALL'){Write-Host 'Cancelled';exit 0}}
foreach($entry in $remove){
    Assert-NoLink $entry.Path
    if((Get-UninstallFileSha256 $entry.Path) -ne $entry.Sha256){throw "File changed during uninstall: $($entry.Path)"}
    Remove-Item -LiteralPath $entry.Path -Force
}
Write-Host 'UNINSTALL=PASS; user data, manifest and helpers retained. Only unchanged registered shortcuts targeting this installation were removed; unregistered shortcuts are preserved.'
