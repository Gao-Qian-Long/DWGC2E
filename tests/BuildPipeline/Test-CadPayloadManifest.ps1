# Purpose: exercise the actual CAD payload-manifest target without building App/CAD or touching release.
# Input: fresh EvidenceDir. Output: isolated dummy payload, target logs and four assertions.
# Usage: powershell -NoProfile -File tests/BuildPipeline/Test-CadPayloadManifest.ps1 -EvidenceDir artifacts/<task>/manifest-test
param([Parameter(Mandatory=$true)][string]$EvidenceDir)
$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDir)
if (Test-Path -LiteralPath $out) { throw 'Use a fresh EvidenceDir; existing evidence is never overwritten.' }
[IO.Directory]::CreateDirectory($out) | Out-Null
$payload = Join-Path $out 'payload'
[IO.Directory]::CreateDirectory((Join-Path $payload 'zh')) | Out-Null
foreach ($name in @('DwgTranslator.Cad.dll','DwgTranslator.Core.dll','zh/Private.resources.dll','excluded.pdb')) {
    [IO.File]::WriteAllText((Join-Path $payload $name), 'fixture-not-loaded')
}
$project = Join-Path $root 'src/DwgTranslator.Cad/DwgTranslator.Cad.csproj'
function Invoke-ManifestTarget([string]$label) {
    & dotnet msbuild $project -nologo -t:WriteCadPlatformManifest "-p:OutDir=$payload\" -p:CadPlatform=GstarCAD *> (Join-Path $out "$label.log")
    if ($LASTEXITCODE -ne 0) { throw "Manifest target failed: $label" }
}
Invoke-ManifestTarget 'first'
$manifest = Join-Path $payload 'cad-files.txt'
$actual = @(Get-Content -LiteralPath $manifest)
$expected = @('DwgTranslator.Cad.dll','DwgTranslator.Core.dll','cad-platform.txt','zh\Private.resources.dll')
if (@(Compare-Object $actual $expected).Count -ne 0) { throw 'Payload names, nested resource or PDB/self exclusion mismatch.' }
'PASS complete private file list with nested resource, excluding symbols and itself'
if ((Get-Content -LiteralPath (Join-Path $payload 'cad-platform.txt') -Raw).Trim() -ne 'GstarCAD') { throw 'Platform mismatch' }
'PASS platform identity preserved'
$hash = (Get-FileHash -LiteralPath $manifest).Hash
Invoke-ManifestTarget 'second'
if ((Get-FileHash -LiteralPath $manifest).Hash -ne $hash) { throw 'Second target run changed unchanged manifest' }
'PASS repeated target execution is deterministic'
[IO.File]::WriteAllText((Join-Path $payload 'New.Private.dll'), 'fixture-not-loaded')
Invoke-ManifestTarget 'third'
$actual = @(Get-Content -LiteralPath $manifest)
if ($actual.Count -ne 5 -or $actual -notcontains 'New.Private.dll') { throw 'New private file not captured exactly once' }
'PASS new private dependency enters manifest'
'CAD_PAYLOAD_MANIFEST=4/4'
'APP_BUILD=NOT_RUN; CAD_BUILD=NOT_RUN; RELEASE=UNCHANGED'
