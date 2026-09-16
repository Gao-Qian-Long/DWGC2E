# Historical one-time migration ONLY. Not a routine cleanup entry; do not remove completion/version guards.
# Input: fixed first-release baseline. Output: irreversible historical artifact deletion and audit.
# Usage: retained for audit, do not rerun; use Clean-DesktopArtifacts.ps1 -WhatIf for routine preview.
# One-time first-release cleanup. Subsequent releases use Clean-DesktopArtifacts.ps1.
[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot '../docs/artifacts-cleanup-20260916.json')){throw 'First-release cleanup already completed; use the normal bounded cleanup workflow.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
if($root -ne 'D:\DWGC2E\artifacts'){throw 'Unexpected workspace'}
if((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked root'}
$release=Join-Path $PSScriptRoot '../release'
$info=Get-Content (Join-Path $release 'build-info.json') -Raw|ConvertFrom-Json
if($info.version -ne '2.1.1+ui.20260916-080934.a3f6f1f'){throw 'This cleanup only applies to the confirmed first release'}
if((Get-FileHash (Join-Path $release 'DwgTranslator.exe')).Hash -ne $info.sha256){throw 'Installed hash mismatch'}
$keep=@('publish-20260916-080934','DwgTranslator-win-x64-GstarCAD-20260916-080934.zip','云端同步修复审核-20260916','ui-smoke','cloud-sync-publish.log','governance-final-20260916-072248','governance-resolution-20260916-080055','inno-acceptance-20260916-082013','inno-toolchain-20260916-081055','governance-current.txt','governance-resolution-current.txt','inno-acceptance-current.txt','inno-toolchain-current.txt','README.md','website-page-review-fixed-20260915')
$items=@(Get-ChildItem -LiteralPath $root -Force)
$targets=@($items|Where-Object {$_.Name -notin $keep -and $_.Name -notlike 'review-*' -and $_.Name -notmatch '^payment-.*(backup|before|settings|predeploy).*\.(sql|json)$'})
$running=@(Get-CimInstance Win32_Process|Where-Object {$_.ProcessId -ne $PID})
$deleted=@();$retained=@();[long]$bytes=0
foreach($item in $targets){
 $full=[IO.Path]::GetFullPath($item.FullName)
 if([IO.Path]::GetDirectoryName($full) -ne $root){throw "Outside artifacts: $full"}
 if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Linked item: $full"}
 $files=if($item.PSIsContainer){@(Get-ChildItem -LiteralPath $full -Recurse -Force)}else{@($item)}
 if($files|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw "Nested link: $full"}
 if($running|Where-Object {($_.ExecutablePath -and $_.ExecutablePath.StartsWith($full+'\',[StringComparison]::OrdinalIgnoreCase)) -or ($_.CommandLine -and $_.CommandLine.Contains($item.Name))}){$retained+=@{Name=$item.Name;Reason='Referenced by running process'};continue}
 # Do not remove portable settings, terms or user outputs unique to an old build.
 $unique=$false
 if($item.Name -like 'release-*'){
  foreach($name in @('settings.json','glossaries','prompts','exports','translation_cache.json')){
   $data=Join-Path $full $name
   if(!(Test-Path -LiteralPath $data)){continue}
   foreach($f in @(Get-ChildItem -LiteralPath $data -Recurse -File -Force)){
    $relative=$f.FullName.Substring($full.Length+1)
    $refs=@((Join-Path $release $relative),(Join-Path (Join-Path $root 'publish-20260916-080934') $relative))
    $hash=(Get-FileHash -LiteralPath $f.FullName).Hash
    $matched=@($refs|Where-Object {(Test-Path -LiteralPath $_ -PathType Leaf) -and (Get-FileHash -LiteralPath $_).Hash -eq $hash})
    if(!$matched.Count){$unique=$true}
   }
  }
 }
 if($unique){$retained+=@{Name=$item.Name;Reason='Unique portable data'};continue}
 $size=($files|Where-Object {!$_.PSIsContainer}|Measure-Object Length -Sum).Sum
 if($PSCmdlet.ShouldProcess($full,'Delete obsolete first-release artifacts')){
  Remove-Item -LiteralPath $full -Recurse -Force
  $deleted+=$item.Name;$bytes+=$size
 }
}
$report=[pscustomobject]@{At=(Get-Date -Format o);Release=$info.version;Removed=$deleted.Count;BytesFreed=$bytes;Deleted=$deleted;Protected=$retained;Remaining=@(Get-ChildItem -LiteralPath $root -Force|Select-Object -ExpandProperty Name)}
if(!$WhatIfPreference){$report|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $PSScriptRoot '../docs/artifacts-cleanup-20260916.json') -Encoding UTF8}
$report
