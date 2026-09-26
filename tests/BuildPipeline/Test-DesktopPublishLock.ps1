# Purpose: actual publish-lock regression in independent, isolated child processes.
# Input: new ResultDir below artifacts. Output: JSON and child logs; no APP build.
param([string]$ResultDir)
$ErrorActionPreference='Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(!$ResultDir){$ResultDir=Join-Path $root ('artifacts/publish-lock-test-'+[guid]::NewGuid().ToString('N'))}
$ResultDir=[IO.Path]::GetFullPath($ResultDir)
if(!$ResultDir.StartsWith((Join-Path $root 'artifacts')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Results must stay under artifacts.'}
if(Test-Path -LiteralPath $ResultDir){throw 'Use a new results directory.'}
$fixture=Join-Path $ResultDir 'workspace'
$tools=Join-Path $fixture 'tools'
$tests=Join-Path $fixture 'tests/BuildPipeline'
New-Item -ItemType Directory -Path $tools,$tests -Force | Out-Null
$sourceFiles=@('Publish-Desktop.ps1','Enter-DesktopPublishLock.ps1')
foreach($name in $sourceFiles){Copy-Item -LiteralPath (Join-Path $root "tools/$name") -Destination $tools}
$checks=New-Object 'System.Collections.Generic.List[string]'
function Check($condition,[string]$name){if(!$condition){throw $name};$checks.Add($name);Write-Host "PASS $name"}
# Only the FIRST validation gate is a sentinel; reaching it stops before dotnet.
@"
Set-Content -LiteralPath '$fixture\gate-entered.txt' -Value 'entered'
exit 73
"@ | Set-Content (Join-Path $tests 'Test-Installer.ps1') -Encoding UTF8
$holderPath=Join-Path $ResultDir 'hold-lock.ps1'
$holderScript=@"
param([string]`$Fixture,[string]`$Evidence)
`$ErrorActionPreference='Stop'
`$handle=& (Join-Path `$Fixture 'tools/Enter-DesktopPublishLock.ps1') -WorkspaceRoot `$Fixture
try{
    Set-Content -LiteralPath (Join-Path `$Evidence 'ready.txt') -Value 'ready'
    `$deadline=[DateTime]::UtcNow.AddSeconds(60)
    while(!(Test-Path -LiteralPath (Join-Path `$Evidence 'release-holder.txt'))){
        if([DateTime]::UtcNow -gt `$deadline){throw 'Holder timed out'}
        Start-Sleep -Milliseconds 50
    }
}finally{`$handle.Dispose()}
"@
$holderScript | Set-Content $holderPath -Encoding UTF8
function Invoke-Publish([string]$label, [string]$options=''){
    $stdout=Join-Path $ResultDir "$label.stdout.log";$stderr=Join-Path $ResultDir "$label.stderr.log"
    $args='-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $tools 'Publish-Desktop.ps1')+'"'
    $args += ' ' + $options
    $process=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList $args -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $null=$process.Handle # Cache the Windows handle before the short-lived child exits.
    if(!$process.WaitForExit(20000)){throw 'Isolated publisher did not stop at the gate.'}
    $process.Refresh()
    return [pscustomobject]@{ExitCode=$process.ExitCode;Text=((Get-Content $stdout -Raw -ErrorAction SilentlyContinue)+(Get-Content $stderr -Raw -ErrorAction SilentlyContinue))}
}
# Both normal and Clean delivery reject a skipped-gate request before any build,
# lock, stage, or installed-directory operation. The production entry is unchanged.
foreach ($case in @(@{Label='skip-tests';Options='-SkipTests'},@{Label='clean-skip-tests';Options='-Clean -SkipTests'})) {
    $rejected=Invoke-Publish $case.Label $case.Options
    Check ($rejected.ExitCode -ne 0 -and $rejected.Text.Contains('RELEASE_REQUIRES_TESTS')) ($case.Label + ' rejected before delivery')
    Check (!(Test-Path (Join-Path $fixture 'gate-entered.txt'))) ($case.Label + ' executes no build or validation gate')
    Check (!(Test-Path (Join-Path $fixture 'artifacts')) -and !(Test-Path (Join-Path $fixture 'release'))) ($case.Label + ' creates no lock, candidate, backup or release')
}
$holder=$null;$independent=$null
try{
    $args='-NoProfile -ExecutionPolicy Bypass -File "'+$holderPath+'" -Fixture "'+$fixture+'" -Evidence "'+$ResultDir+'"'
    $holder=Start-Process powershell.exe -WindowStyle Hidden -ArgumentList $args -PassThru -RedirectStandardOutput (Join-Path $ResultDir 'holder.stdout.log') -RedirectStandardError (Join-Path $ResultDir 'holder.stderr.log')
    $null=$holder.Handle
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    while(!(Test-Path -LiteralPath (Join-Path $ResultDir 'ready.txt'))){
        if($holder.HasExited -or [DateTime]::UtcNow -gt $deadline){throw 'Holder did not become ready.'}
        Start-Sleep -Milliseconds 50
    }
    Check (!$holder.HasExited) 'real independent process holds the lock'
    $diagnostic=Invoke-Publish 'isolated-diagnostic-locked' '-BuildOnly -SkipTests'
    Check ($diagnostic.ExitCode -ne 0 -and $diagnostic.Text.Contains('DESKTOP_PUBLISH_LOCK_UNAVAILABLE') -and !$diagnostic.Text.Contains('RELEASE_REQUIRES_TESTS')) 'explicit isolated diagnostic remains supported but cannot bypass publish lock'
    $blocked=Invoke-Publish 'blocked'
    Check ($blocked.ExitCode -ne 0 -and $blocked.Text.Contains('DESKTOP_PUBLISH_LOCK_UNAVAILABLE')) 'second actual publish entry refuses a held lock'
    Check (!(Test-Path (Join-Path $fixture 'gate-entered.txt'))) 'refusal precedes all build and validation gates'
    Check (!(Test-Path (Join-Path $fixture 'release'))) 'refusal creates no release directory'
    Check (@(Get-ChildItem (Join-Path $fixture 'artifacts') -Force).Count -eq 1) 'refusal creates no candidate or backup'
    $independent=& (Join-Path $tools 'Enter-DesktopPublishLock.ps1') -WorkspaceRoot (Join-Path $ResultDir 'independent-workspace')
    Check ($null -ne $independent) 'different workspace acquires its own lock'
    $independent.Dispose();$independent=$null
    Set-Content -LiteralPath (Join-Path $ResultDir 'release-holder.txt') -Value 'release'
    Check ($holder.WaitForExit(10000)) 'holder exits normally without termination'
    $holder.Refresh();Check ($holder.ExitCode -eq 0) 'holder exits successfully'
    Check (Test-Path (Join-Path $fixture 'artifacts/.desktop-publish.lock')) 'marker remains after handle disposal'
    $allowed=Invoke-Publish 'after-release'
    Check ($allowed.ExitCode -ne 0 -and $allowed.Text.Contains('Installer preservation regression failed')) 'stale marker does not block the next publisher'
    Check (Test-Path (Join-Path $fixture 'gate-entered.txt')) 'unlocked publish reaches controlled validation gate'
    $retry=Invoke-Publish 'after-failure'
    Check ($retry.Text.Contains('Installer preservation regression failed') -and !$retry.Text.Contains('DESKTOP_PUBLISH_LOCK_UNAVAILABLE')) 'failed publish releases lock for subsequent invocation'
    $origins=@(foreach($name in $sourceFiles){[pscustomobject]@{File="tools/$name";Sha256=(Get-FileHash (Join-Path $root "tools/$name")).Hash;FixtureMatches=((Get-FileHash (Join-Path $root "tools/$name")).Hash -eq (Get-FileHash (Join-Path $tools $name)).Hash)}})
    Check (@($origins | Where-Object {!$_.FixtureMatches}).Count -eq 0) 'tested scripts match production byte for byte'
    [ordered]@{Passed=$checks.Count;Checks=$checks;Sources=$origins;Scope='Real child processes; isolated workspace; first gate intentionally fails before APP build.'} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $ResultDir 'results.json') -Encoding UTF8
    Write-Host "PUBLISH_LOCK_TESTS=PASS ($($checks.Count) checks)"
}finally{
    if($independent){$independent.Dispose()}
    if($holder -and !$holder.HasExited){Set-Content -LiteralPath (Join-Path $ResultDir 'release-holder.txt') -Value 'release';$holder.WaitForExit(10000) | Out-Null}
}
