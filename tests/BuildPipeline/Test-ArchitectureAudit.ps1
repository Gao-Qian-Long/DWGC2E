# Purpose: verify the metadata/resource auditor rejects broken resource and CAD delivery contracts.
# Input: existing Release build and installed release; output: fresh isolated fixture/evidence directory.
# Usage: powershell -NoProfile -File tests/BuildPipeline/Test-ArchitectureAudit.ps1 -OutputDir artifacts/<task>/negative-cases
param([Parameter(Mandatory=$true)][string]$OutputDir)
$ErrorActionPreference='Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
if(-not $out.StartsWith((Join-Path $root 'artifacts')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $out)){throw 'Use a new evidence directory under artifacts.'}
$fixture=Join-Path $out 'fixture'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$inputs=@('src/DwgTranslator.Core/bin/Release/net8.0/DwgTranslator.Core.dll','src/DwgTranslator.App/bin/Release/net8.0-windows/win-x64/QLCAD.dll','release/CadPlugin/DwgTranslator.Core.dll','release/CadPlugin/DwgTranslator.Cad.dll','release/CadPlugin/cad-files.txt','release/CadPlugin/cad-platform.txt','assets/glossaries/mechanical_zh_en.json','release/assets/default-glossaries/mechanical_zh_en.json','src/DwgTranslator.App/Views/MainWindow.xaml')
foreach($input in $inputs){$destination=Join-Path $fixture $input;New-Item -ItemType Directory -Path (Split-Path $destination) -Force|Out-Null;Copy-Item -LiteralPath (Join-Path $root $input) -Destination $destination}
# The audit only hashes the executable. Explicit inert fixture bytes avoid duplicating the runnable APP.
$exe=Join-Path $fixture 'release/QLCAD.exe'
[IO.File]::WriteAllText($exe,'INERT ARCHITECTURE TEST FIXTURE; NOT AN EXECUTABLE')
@{version='test-fixture-not-a-delivery';sha256=(Get-FileHash -LiteralPath $exe).Hash}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $fixture 'release/build-info.json') -Encoding UTF8
$probe=Join-Path $root 'tests/ArchitectureAudit/bin/Release/net8.0/ArchitectureAudit.dll'
if(-not(Test-Path -LiteralPath $probe)){throw 'Run the formal architecture audit first to build its standalone probe.'}
function Run-Case([string]$Name,[bool]$ExpectedPass,[string]$Payload=""){
    $result=Join-Path $fixture ('artifacts/'+$Name+'.json')
    $arguments=@($fixture,$result)
    if($Payload){$arguments += $Payload}
    & dotnet $probe @arguments > (Join-Path $out ($Name+'.log')) 2>&1
    $exit=$LASTEXITCODE
    if(-not(Test-Path -LiteralPath $result)){throw "No structured report for $Name"}
    $data=Get-Content -LiteralPath $result -Raw|ConvertFrom-Json
    if(($exit -eq 0) -ne $ExpectedPass -or $data.passed -ne $ExpectedPass){throw "Unexpected audit outcome: $Name"}
    Write-Output "PASS $Name (expectedPass=$ExpectedPass)"
}
Run-Case 'valid-fixture' $true
# Prove explicit candidate selection: valid installed fixture cannot mask a broken candidate.
$candidate=Join-Path $fixture 'artifacts/candidate'
New-Item -ItemType Directory -Path (Split-Path $candidate) -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $fixture 'release') -Destination $candidate -Recurse
Run-Case 'explicit-candidate-valid' $true $candidate
[IO.File]::WriteAllText((Join-Path $candidate 'CadPlugin/cad-platform.txt'),'broken candidate only')
Run-Case 'explicit-candidate-rejected' $false $candidate
Run-Case 'installed-fixture-still-valid' $true

$platform=Join-Path $fixture 'release/CadPlugin/cad-platform.txt'
$bytes=[IO.File]::ReadAllBytes($platform)
[IO.File]::WriteAllText($platform,'tampered-platform')
Run-Case 'tampered-plugin-payload' $false
[IO.File]::WriteAllBytes($platform,$bytes)
$manifest=Join-Path $fixture 'release/CadPlugin/cad-files.txt'
$bytes=[IO.File]::ReadAllBytes($manifest)
[IO.File]::AppendAllText($manifest,"
missing-private-dependency.dll
")
Run-Case 'manifest-divergence' $false
[IO.File]::WriteAllBytes($manifest,$bytes)
$missing=Join-Path $fixture 'src/DwgTranslator.App/Views/NotCompiled.xaml'
[IO.File]::WriteAllText($missing,'<Page />')
Run-Case 'missing-compiled-xaml' $false
$candidateExe=Join-Path $candidate 'QLCAD.exe'
Copy-Item -LiteralPath (Join-Path $fixture 'release/CadPlugin/cad-platform.txt') -Destination (Join-Path $candidate 'CadPlugin/cad-platform.txt') -Force
# Remove only our known synthetic XAML from enumeration by renaming it in this fixture.
Move-Item -LiteralPath $missing -Destination ($missing+'.fixture')
[IO.File]::AppendAllText($candidateExe,'tampered')
Run-Case 'candidate-executable-hash-rejected' $false $candidate
Write-Output 'ARCHITECTURE_AUDITOR_TESTS=PASS; 3 passing cases and 5 deliberately broken fixtures; no installed files changed.'
