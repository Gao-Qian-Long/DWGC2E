# Purpose: verify literal/relative release paths and negative package checks in isolated PS5 processes.
# Input: clean PublishDir, fresh EvidenceDir. Output: fixtures/logs/results; never builds or installs APP.
# Gated by tools/Publish-Desktop.ps1 for every normal delivery; runnable standalone as a hotfix check.
# Note: the marker strings below are matched against real verifier messages, so they must stay in
# sync with tools/Verify-ReleasePackage.ps1 (a marker mismatch makes the case fail forever).
param([Parameter(Mandatory=$true)][string]$PublishDir,[Parameter(Mandatory=$true)][string]$EvidenceDir)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidence=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDir)
if(Test-Path -LiteralPath $evidence){throw 'Use a fresh evidence directory.'}
$fixture=Join-Path $evidence '[package]'
New-Item -ItemType Directory -Path $fixture -Force|Out-Null
Get-ChildItem -LiteralPath $PublishDir|Copy-Item -Destination $fixture -Recurse
$results=New-Object Collections.Generic.List[object]
function CheckRun($label,$relative,$success,$marker){
 $tool=Join-Path $root 'tools/Verify-ReleasePackage.ps1'
 $runner=Join-Path $evidence ($label+'.ps1')
 $location=$evidence.Replace("'","''");$argument=if($relative){'.\[package]'}else{$fixture};$argument=$argument.Replace("'","''")
 [IO.File]::WriteAllText($runner,"Set-Location -LiteralPath '$location'`r`n& '$tool' -PublishDir '$argument'`r`nexit `$LASTEXITCODE",[Text.UTF8Encoding]::new($true))
 $log=Join-Path $evidence ($label+'.log')
 $previous=$ErrorActionPreference; $ErrorActionPreference='Continue'
 try { & powershell.exe -NoProfile -File $runner *> $log } finally { $ErrorActionPreference=$previous }
 $code=$LASTEXITCODE;$text=Get-Content -LiteralPath $log -Raw
 if(($success -and $code -ne 0) -or (!$success -and $code -eq 0) -or !$text.Contains($marker)){throw "FAIL: $label ($code)"}
 $results.Add(@{test=$label;exitCode=$code;status='PASS'})
}
CheckRun 'absolute-brackets' $false $true 'RELEASE_PACKAGE=OK'
CheckRun 'relative-brackets' $true $true 'RELEASE_PACKAGE=OK'
$config=Join-Path $fixture 'settings.json';$original=[IO.File]::ReadAllBytes($config)
try {
 $data=Get-Content -LiteralPath $config -Raw|ConvertFrom-Json
 # Add-Member instead of $data.x=: the packaged config no longer carries this legacy field,
 # so a plain property assignment throws instead of injecting the fixture.
 $data|Add-Member -NotePropertyName deepSeekApiKey -NotePropertyValue 'fake-test-value' -Force
 $data|ConvertTo-Json -Depth 30|Set-Content -LiteralPath $config -Encoding UTF8
 CheckRun 'secret-rejected' $true $false 'must not expose legacy AI field'
} finally {[IO.File]::WriteAllBytes($config,$original)}
$platform=Join-Path $fixture 'CadPlugin/cad-platform.txt';$original=[IO.File]::ReadAllBytes($platform)
try {
 Set-Content -LiteralPath $platform 'unsupported-fixture'
 CheckRun 'platform-rejected' $true $false 'Unsupported CAD plugin platform'
} finally {[IO.File]::WriteAllBytes($platform,$original)}
$results|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $evidence 'results.json') -Encoding UTF8
Write-Output "RELEASE_VERIFIER_TESTS=PASS ($($results.Count))"
