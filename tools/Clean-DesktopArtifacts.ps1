# Purpose: bounded cleanup of recognized desktop candidates/packages, retaining rollback and unique portable data.
# Input: WorkspaceRoot (optional). Output: cleanup summary; may DELETE obsolete artifacts.
# Usage: powershell -NoProfile -File tools/Clean-DesktopArtifacts.ps1 -WhatIf (preview first).
[CmdletBinding(SupportsShouldProcess)]
param([string]$WorkspaceRoot = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($WorkspaceRoot)
$artifacts = Join-Path $root 'artifacts'
$release = Join-Path $root 'release'
if (!(Test-Path -LiteralPath $artifacts -PathType Container)) { return }
# Refuse links before enumeration/deletion; never traverse outside the actual workspace.
foreach ($path in @($root, $artifacts, $release)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked cleanup root is not allowed: $path" }
}
function Assert-SafeTarget([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($full) -ne $artifacts) { throw "Not a direct artifact child: $full" }
    $item = Get-Item -LiteralPath $full -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked artifact: $full" }
    if ($item.PSIsContainer) {
        if (Get-ChildItem -LiteralPath $full -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1) { throw "Artifact contains links: $full" }
    }
    return $full
}
$items = @(Get-ChildItem -LiteralPath $artifacts -Force)
# A valid successful build requires both its verified executable and distributable package.
$builds = @(foreach ($dir in $items | Where-Object { $_.PSIsContainer -and $_.Name -match '^publish-\d{8}-\d{6}$' } | Sort-Object Name -Descending) {
    $safe = Assert-SafeTarget $dir.FullName
    $infoPath = Join-Path $safe 'build-info.json'
    $exe = Join-Path $safe 'DwgTranslator.exe'
    if (!(Test-Path -LiteralPath $infoPath) -or !(Test-Path -LiteralPath $exe)) { continue }
    try { $info = Get-Content -LiteralPath $infoPath -Raw | ConvertFrom-Json } catch { continue }
    if (!$info.sha256 -or (Get-FileHash -LiteralPath $exe).Hash -ne $info.sha256) { continue }
    $stamp = $dir.Name.Substring(8)
    $packages = @($items | Where-Object { !$_.PSIsContainer -and $_.Name -match '^DwgTranslator-win-x64-[A-Za-z0-9]+-\d{8}-\d{6}\.zip$' -and $_.Name.EndsWith("-$stamp.zip") })
    foreach ($zip in $packages) { [pscustomobject]@{Dir=$dir; Zip=$zip; Platform=($zip.Name -replace '^DwgTranslator-win-x64-(.+)-\d{8}-\d{6}\.zip$', '$1')} }
})
if (!$builds.Count) { Write-Warning 'No verified candidate: cleanup skipped.'; return }
$latest = @($builds | Group-Object Platform | ForEach-Object { $_.Group | Select-Object -First 1 })
$keep = @($latest | ForEach-Object { $_.Dir.FullName; $_.Zip.FullName })
$backup = $items | Where-Object { $_.PSIsContainer -and $_.Name -match '^release-backup-\d{8}-\d{6}$' } | Sort-Object Name -Descending | Select-Object -First 1
if ($backup) { $keep += $backup.FullName }
$releaseInfo = Join-Path $release 'build-info.json'
if (Test-Path -LiteralPath $releaseInfo) {
    $version = (Get-Content -LiteralPath $releaseInfo -Raw | ConvertFrom-Json).version
    if ($version -match '\+ui\.(\d{8}-\d{6})\.') {
        $stamp = $Matches[1]
        $keep += @($items | Where-Object { !$_.PSIsContainer -and $_.Name -like "DwgTranslator-win-x64-*-$stamp.zip" } | ForEach-Object { $_.FullName })
    }
}
$running = @(Get-Process DwgTranslator -ErrorAction SilentlyContinue | Where-Object Path | ForEach-Object { $_.Path })
$references = @($release) + @($latest | ForEach-Object { $_.Dir.FullName })
$deleted = @(); [long]$bytes = 0
foreach ($item in $items) {
    $recognized = if ($item.PSIsContainer) { $item.Name -match '^(publish|review-publish|publish-\d{8}-\d{6}|release-backup-\d{8}-\d{6})$' } else { $item.Name -match '^DwgTranslator-win-x64-[A-Za-z0-9]+(-\d{8}-\d{6})?\.zip$' }
    if (!$recognized -or $item.FullName -in $keep) { continue }
    $target = Assert-SafeTarget $item.FullName
    if ($running | Where-Object { $_.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase) }) { Write-Warning "Running app retained: $target"; continue }
    if ($item.PSIsContainer) {
        # Old portable builds can contain user data. Retain any unique data, rather than deleting it.
        $unique = $false
        # Only declared program files are disposable. Unknown DBs/files, including files
        # under assets or CadPlugin, must have an identical live copy before deletion.
        $owned=@('DwgTranslator.exe','DwgTranslator.pdb','DwgTranslator.Core.pdb','build-info.json','architecture-audit.json','assets\default-glossaries\mechanical_zh_en.json','CadPlugin\cad-files.txt')
        $manifest=Join-Path $target 'CadPlugin/cad-files.txt'
        if(Test-Path -LiteralPath $manifest){foreach($entry in Get-Content -LiteralPath $manifest){
            if($entry.Replace('\','/') -match '(^/|:|(^|/)\.\.(/|$))'){throw 'Unsafe old CAD manifest'}
            $owned+='CadPlugin\'+$entry.Replace('/','\')
        }}
        foreach ($file in Get-ChildItem -LiteralPath $target -Recurse -File -Force) {
            $relative=$file.FullName.Substring($target.TrimEnd('\').Length+1)
            if($relative -in $owned){continue}
            $hash=(Get-FileHash -LiteralPath $file.FullName).Hash
            $matched=$false
            foreach($reference in $references){
                $other=Join-Path $reference $relative
                if((Test-Path -LiteralPath $other -PathType Leaf) -and (Get-FileHash -LiteralPath $other).Hash -eq $hash){$matched=$true;break}
            }
            if(-not $matched){$unique=$true;break}
        }
        if ($unique) { Write-Warning "Unique portable data retained: $target"; continue }
    }
    $size = if ($item.PSIsContainer) { (Get-ChildItem -LiteralPath $target -Recurse -File -Force | Measure-Object Length -Sum).Sum } else { $item.Length }
    if ($PSCmdlet.ShouldProcess($target, 'Delete obsolete desktop build')) {
        Remove-Item -LiteralPath (Assert-SafeTarget $target) -Recurse -Force
        $deleted += $item.Name; $bytes += $size
    }
}
# Remove only completed, explicitly inventoried old delivery trees. Failed/incomplete
# deliveries and anything altered after acceptance are reported for separate review.
$currentFile=Join-Path $artifacts 'current-release.json'
if(Test-Path -LiteralPath $currentFile){
    $current=Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json
    foreach($dir in $items | Where-Object {$_.PSIsContainer -and $_.Name -match '^delivery-\d{8}-\d{6}$'}){
        if($dir.FullName -eq $current.deliveryDirectory){continue}
        $target=Assert-SafeTarget $dir.FullName
        $ownership=Join-Path $target 'delivery-files.json'
        if(-not(Test-Path -LiteralPath $ownership)){Write-Warning "Unfinished delivery retained: $target";continue}
        $receipt=Get-Content -LiteralPath $ownership -Raw | ConvertFrom-Json
        $expected=@{};foreach($entry in $receipt.files){$expected[$entry.path]=$entry.sha256}
        $actual=@(Get-ChildItem -LiteralPath $target -Recurse -File -Force | Where-Object {$_.FullName -ne $ownership})
        $safe=$actual.Count -eq $expected.Count
        foreach($file in $actual){$relative=$file.FullName.Substring($target.Length+1);if(-not $expected.ContainsKey($relative) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $expected[$relative]){$safe=$false;break}}
        if(-not $safe){Write-Warning "Changed delivery data retained: $target";continue}
        if($running | Where-Object {$_.StartsWith($target+'\',[StringComparison]::OrdinalIgnoreCase)}){continue}
        if($PSCmdlet.ShouldProcess($target,'Delete inventoried obsolete delivery')){
            $size=(Get-ChildItem -LiteralPath $target -File -Recurse -Force | Measure-Object Length -Sum).Sum
            Remove-Item -LiteralPath (Assert-SafeTarget $target) -Recurse -Force
            $deleted+=$target;$bytes+=[long]$size
        }
    }
}
[pscustomobject]@{Removed=$deleted.Count; BytesFreed=$bytes; Deleted=$deleted; Retained=$keep}
