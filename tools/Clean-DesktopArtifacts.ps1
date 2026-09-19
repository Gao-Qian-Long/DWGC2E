# Purpose: bounded cleanup of recognized desktop candidates/packages, retaining rollback and unique portable data.
# Input: WorkspaceRoot (optional). Output: cleanup summary; may DELETE obsolete artifacts.
# Usage: powershell -NoProfile -File tools/Clean-DesktopArtifacts.ps1 -WhatIf (preview first).
[CmdletBinding(SupportsShouldProcess)]
param([string]$WorkspaceRoot)
if (!$WorkspaceRoot) { $WorkspaceRoot = Join-Path $PSScriptRoot '..' }
# Keep -WhatIf out of the read-only inventory phase below: under Windows PowerShell 5.1 the
# preference propagates into Get-Item/Get-FileHash there, hash comparison then fails, and the
# preview always reported "No verified candidate". Deletion stays gated by
# $PSCmdlet.ShouldProcess, which honours -WhatIf directly and does not read this preference.
$WhatIfPreference = $false
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
# Only candidates whose executable matches their own build-info.json are proven build products.
# Everything else (hand-made or tampered directories) keeps the strict identical-copy rule.
$verifiedCandidates = @($builds | ForEach-Object { $_.Dir.FullName } | Select-Object -Unique)
$latest = @($builds | Group-Object Platform | ForEach-Object { $_.Group | Select-Object -First 1 })
$keep = @($latest | ForEach-Object { $_.Dir.FullName; $_.Zip.FullName })
$backup = $items | Where-Object { $_.PSIsContainer -and $_.Name -match '^release-backup-\d{8}-\d{6}$' } | Sort-Object Name -Descending | Select-Object -First 1
if ($backup) { $keep += $backup.FullName }
# The installed release records the exact candidate and rollback directory it was built from.
# The next delivery refuses to build an upgrade installer without that candidate, so both recorded
# paths are protected unconditionally. An unreadable record is fail-closed: nothing is deleted.
$current = $null
$currentFile = Join-Path $artifacts 'current-release.json'
if (Test-Path -LiteralPath $currentFile) {
    try { $current = Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json }
    catch { throw "current-release.json is unreadable; refusing to delete artifacts because its recorded candidate and rollback directory cannot be protected. $($_.Exception.Message)" }
    if (!$current) { throw 'current-release.json parsed to nothing; refusing to delete artifacts because its recorded candidate and rollback directory cannot be protected.' }
    foreach ($recorded in @($current.candidate, $current.rollbackDirectory)) {
        $text = [string]$recorded
        if ([string]::IsNullOrWhiteSpace($text)) { continue }
        try { $keep += [IO.Path]::GetFullPath($text) } catch { $keep += $text }
    }
}
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
    # release-next-*/release-failed-* are interrupted or failed swap directories. They hold a copy of
    # the installed release plus portable user data, so they are recognized for cleanup but always
    # pass through the identical-copy rule below.
    $recognized = if ($item.PSIsContainer) { $item.Name -match '^(publish|review-publish|publish-\d{8}-\d{6}|release-backup-\d{8}-\d{6}|release-next-\d{8}-\d{6}|release-failed-\d{8}-\d{6})$' } else { $item.Name -match '^DwgTranslator-win-x64-[A-Za-z0-9]+(-\d{8}-\d{6})?\.zip$' }
    if (!$recognized -or $item.FullName -in $keep) { continue }
    $target = Assert-SafeTarget $item.FullName
    if ($running | Where-Object { $_.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase) }) { Write-Warning "Running app retained: $target"; continue }
    if ($item.PSIsContainer) {
        # Old portable builds can contain user data. Retain any unique data, rather than deleting it.
        $unique = $false
        # Only declared program files are disposable. Unknown DBs/files, including files
        # under assets or CadPlugin, must have an identical live copy before deletion.
        $owned=@('DwgTranslator.exe','DwgTranslator.pdb','DwgTranslator.Core.pdb','build-info.json','architecture-audit.json','assets\default-glossaries\mechanical_zh_en.json','glossaries\mechanical_zh_en.json','CadPlugin\cad-files.txt')
        # settings.json in a verified publish-* candidate is a byte copy of the reviewed
        # settings.json.example (Assert-CleanPackageInput.ps1 enforces that at publish time), and the
        # publish flow states it must never carry personal settings. It therefore cannot be unique
        # user data, unlike the same file inside release-backup-*/release-next-*/release-failed-*,
        # which is an installed release and keeps the identical-copy requirement.
        if ($target -in $verifiedCandidates) { $owned += 'settings.json' }
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
# $current (parsed above, fail-closed) supplies the protected delivery directory.
if($current){
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
