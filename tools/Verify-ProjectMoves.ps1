# Purpose: verify the documented governance moves against their recorded Git baseline.
# Input: optional manifest/output path. Output: JSON evidence; no build, index, or source writes.
# Run: powershell -NoProfile -File tools/Verify-ProjectMoves.ps1 -ResultPath artifacts/<task>/moves.json
[CmdletBinding()]
param([string]$ManifestPath,[string]$ResultPath)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if(-not $ManifestPath){$ManifestPath=Join-Path $root 'tools/project-moves.json'}
$manifest=Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8|ConvertFrom-Json
function GitValue([string[]]$Arguments){
    $value=& git -C $root @Arguments
    if($LASTEXITCODE -ne 0){throw "Git lookup failed: $($Arguments -join ' ')"}
    return ($value -join "`n").Trim()
}
function Resolve-Source([string]$Relative){
    if([IO.Path]::IsPathRooted($Relative)){throw 'Manifest paths must be relative'}
    $full=[IO.Path]::GetFullPath((Join-Path $root $Relative))
    if(-not $full.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest path escapes repository'}
    return $full
}
$rows=@();$problems=New-Object 'System.Collections.Generic.List[string]'
foreach($entry in $manifest.entries){
    $old=Resolve-Source $entry.from;$new=Resolve-Source $entry.to
    $row=[ordered]@{From=$entry.from;To=$entry.to;Kind=$entry.kind;Verified=$false;Detail=''}
    try{
        if(-not(Test-Path -LiteralPath $new -PathType Leaf)){throw 'Destination is missing'}
        if($entry.kind -eq 'compatibility-entry'){
            if(-not(Test-Path -LiteralPath $old -PathType Leaf)){throw 'Compatibility entry missing'}
            $wrapper=Get-Content -LiteralPath $old -Raw
            if(-not $wrapper.Contains('../'+$entry.to)){throw 'Compatibility entry does not point at formal test'}
            $row.Detail='Wrapper retained and points at formal test; no baseline content claim'
        }else{
            if(Test-Path -LiteralPath $old){throw 'Old file still exists; duplicate requires review'}
            $baseline=GitValue @('rev-parse',($manifest.baselineCommit+':'+$entry.from))
            $current=GitValue @('hash-object',('--path='+$entry.from),'--',$entry.to)
            $row.BaselineBlob=$baseline;$row.CurrentBlob=$current
            if($entry.kind -eq 'move-with-reviewed-change'){
                if($current -ne $entry.reviewedBlob){throw 'Content changed after recorded review'}
                $row.Detail=$entry.review
            }elseif($entry.kind -in @('move','deduplicate')){
                if($baseline -ne $current){throw 'Unexpected non-move content change'}
                $row.Detail='Matches baseline through Git clean filters'
            }else{throw 'Unknown manifest kind'}
        }
        $row.Verified=$true
    }catch{$row.Detail=$_.Exception.Message;$problems.Add($entry.from+': '+$row.Detail)}
    $rows+=[pscustomobject]$row
}
$mapped=@($manifest.entries|ForEach-Object{$_.from})
$deleted=@(& git -C $root -c core.quotePath=false ls-files --deleted)
if($LASTEXITCODE -ne 0){throw 'Cannot enumerate deleted tracked files'}
foreach($path in $deleted){if($path -notin $mapped){$problems.Add('Unmapped tracked deletion: '+$path)}}
$result=[ordered]@{BaselineCommit=$manifest.baselineCommit;Verified=($problems.Count -eq 0);Scope=$manifest.scope;Entries=$rows;Problems=@($problems)}
if($ResultPath){
    $full=Resolve-Source $ResultPath
    if(-not $full.StartsWith($root+'\artifacts\',[StringComparison]::OrdinalIgnoreCase)){throw 'Result must stay under artifacts'}
    if(Test-Path -LiteralPath $full){throw 'Result exists; use a new path'}
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))|Out-Null
    $result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $full -Encoding UTF8
}
if($problems.Count){throw ($problems -join "`n")}
Write-Host "PROJECT_MOVES=PASS; $($rows.Count) manifest entries; no unmapped tracked deletions."
