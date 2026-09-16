# Purpose: verify declared CAD dependencies are checked before shipping. No APP/CAD build or installation.
# Input: fresh EvidenceDir; a previously built Core DLL for managed-assembly fixtures.
# Output: seven isolated verifier subprocess results. Keep fixtures as evidence; no recursive cleanup.
param([Parameter(Mandatory=$true)][string]$EvidenceDir)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDir)
if(Test-Path -LiteralPath $out){throw 'Use a fresh EvidenceDir.'}
$core=Join-Path $root 'src/DwgTranslator.Core/bin/Release/net8.0/DwgTranslator.Core.dll'
if(-not(Test-Path -LiteralPath $core)){throw 'Build Core tests first; this test never builds APP.'}
[IO.Directory]::CreateDirectory($out)|Out-Null
$required=@('DwgTranslator.Cad.dll','DwgTranslator.Core.dll','cad-platform.txt')
$checks=0
function Case([string]$name,[string[]]$extra,[bool]$expected,[switch]$Legacy,[switch]$OmitCore){
    $dir=Join-Path $out $name
    [IO.Directory]::CreateDirectory((Join-Path $dir 'zh'))|Out-Null
    foreach($file in @('DwgTranslator.Cad.dll','DwgTranslator.Core.dll','Private.dll','zh/Private.resources.dll')){
        Copy-Item -LiteralPath $core -Destination (Join-Path $dir $file)
    }
    [IO.File]::WriteAllText((Join-Path $dir 'cad-platform.txt'),'GstarCAD')
    if(-not $Legacy){
        $entries=@($required)+@($extra)
        if($OmitCore){$entries=@($entries|Where-Object{$_ -ne 'DwgTranslator.Core.dll'})}
        [IO.File]::WriteAllLines((Join-Path $dir 'cad-files.txt'),[string[]]$entries)
    }
    $ErrorActionPreference='Continue'
    & powershell.exe -NoProfile -File (Join-Path $root 'tools/Verify-CadPluginPackage.ps1') -PluginDir $dir *> (Join-Path $out "$name.log")
    $exitCode=$LASTEXITCODE
    $ErrorActionPreference='Stop'
    if(($exitCode -eq 0) -ne $expected){throw "Unexpected verifier result: $name"}
    $script:checks++
    "PASS $name"
}
Case 'complete' @('Private.dll','zh/Private.resources.dll') $true
Case 'missing' @('Missing.dll') $false
Case 'traversal' @('../escape.dll') $false
Case 'duplicate' @('dwgtranslator.CAD.dll') $false
Case 'host-sdk' @('AcDbMgd.dll') $false
Case 'omitted-core' @() $false -OmitCore
Case 'legacy' @() $true -Legacy
"CAD_PAYLOAD_VERIFIER=$checks/7"
