# Purpose: inject installation failures only into isolated copies and verify exact rollback.
param([string]$ResultDir)
$ErrorActionPreference='Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(-not $ResultDir){$ResultDir=Join-Path $root ('artifacts/installer-safety-20260916-resumed/installer-transaction/run-'+[guid]::NewGuid().ToString('N'))}
[IO.Directory]::CreateDirectory($ResultDir)|Out-Null
$fixture=Join-Path $ResultDir ('fixture-'+[guid]::NewGuid().ToString('N'))
$package=Join-Path $fixture 'package'
function Put($base,$rel,$text){$p=Join-Path $base $rel;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p))|Out-Null;[IO.File]::WriteAllText($p,$text,[Text.UTF8Encoding]::new($true))}
foreach($name in @('Install.ps1','InstallTransaction.ps1','Uninstall.ps1')){Put $package $name ([IO.File]::ReadAllText((Join-Path $root ('installer/'+$name))))}
$payload=@{'QLCAD.exe'='v1';'settings.json'='{}';'CadPlugin/DwgTranslator.Cad.dll'='cad-v1';'CadPlugin/DwgTranslator.Core.dll'='core-v1';'CadPlugin/cad-platform.txt'='GstarCAD';'assets/default-glossaries/mechanical_zh_en.json'='{}';'glossaries/mechanical_zh_en.json'='{}'}
foreach($name in $payload.Keys){Put $package $name $payload[$name]}
$original=[IO.File]::ReadAllText((Join-Path $package 'Install.ps1'))
function Snapshot($target){$map=@{};if(Test-Path -LiteralPath $target){Get-ChildItem -LiteralPath $target -Recurse -File -Force|ForEach-Object {$map[$_.FullName.Substring($target.Length+1)]=(Get-FileHash -LiteralPath $_.FullName).Hash}};return $map}
function RunInstall($target,$log){$saved=$ErrorActionPreference;$ErrorActionPreference='Continue'; & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'Install.ps1') -SourceDir $package -TargetDir $target -NoLaunch -NoPrompt -NoShortcuts *> $log;$code=$LASTEXITCODE;$ErrorActionPreference=$saved;return $code}
$points=@('foreach ($sub in @(''CadPlugin'',''glossaries'',''assets'')) {','# Retain previous ownership when reinstalling without creating shortcuts.','# Record program ownership only after successful copy; never claim personal data.','Write-Step "写入 卸载.cmd"')
$checks=0
foreach($upgrade in @($false,$true)){
    for($i=0;$i -lt $points.Count;$i++){
        $target=Join-Path $fixture ("target-$upgrade-$i")
        Put $package 'Install.ps1' $original
        Put $package 'QLCAD.exe' 'v1'
        if($upgrade){if((RunInstall $target (Join-Path $ResultDir "baseline-$i.log")) -ne 0){throw 'Baseline failed'};Put $target 'settings.json' '{"personal":true}';Put $target 'unknown.txt' 'keep';Put $target 'prompts/deepl_context.txt' 'personal';Put $target 'accounts/local.json' 'synthetic-private'}
        $before=Snapshot $target
        Put $package 'QLCAD.exe' 'v2'
        # Point 0 occurs twice (preflight and write loop); inject at LAST occurrence only.
        $marker=$points[$i];$at=$original.LastIndexOf($marker);if($at -lt 0){throw 'Fault marker missing'}
        $fault=$original.Insert($at,"throw 'SYNTHETIC_INSTALL_FAILURE'"+[Environment]::NewLine)
        Put $package 'Install.ps1' $fault
        $log=Join-Path $ResultDir ("fault-$upgrade-$i.log")
        $code=RunInstall $target $log
        $after=Snapshot $target
        if($code -eq 0 -or -not((Get-Content $log -Raw).Contains('INSTALL_ROLLBACK=PASS'))){throw "Fault did not roll back: $log"}
        if($before.Count -ne $after.Count -or @($before.Keys|Where-Object {$before[$_] -ne $after[$_]}).Count){throw "Rollback content mismatch: $log"}
        if(-not $upgrade -and (Test-Path -LiteralPath $target)){throw "Fresh install left target behind: $target"}
        $checks++;Write-Host "PASS rollback upgrade=$upgrade point=$i; exact file hashes preserved"
    }
}
# Abrupt exit bypasses catch/finally. The NEXT installer must restore the old version first.
foreach($upgrade in @($false,$true)) {
    $target=Join-Path $fixture ("interrupted-$upgrade")
    Put $package 'Install.ps1' $original
    Put $package 'QLCAD.exe' 'v1'
    if($upgrade){if((RunInstall $target (Join-Path $ResultDir 'interrupt-baseline.log')) -ne 0){throw 'Baseline failed'};Put $target 'settings.json' '{"personal":true}'}
    $before=Snapshot $target
    Put $package 'QLCAD.exe' 'v2'
    $at=$original.LastIndexOf('Write-Step "写入 卸载.cmd"')
    Put $package 'Install.ps1' ($original.Insert($at,'[Environment]::Exit(81)'+[Environment]::NewLine))
    if((RunInstall $target (Join-Path $ResultDir ("interrupted-$upgrade.log"))) -ne 81){throw 'Abrupt-exit fixture did not exit at checkpoint'}
    # After pending recovery, fail the new attempt. The result must be the original v1 preimage.
    $at=$original.LastIndexOf($points[0])
    Put $package 'Install.ps1' ($original.Insert($at,"throw 'SYNTHETIC_AFTER_RECOVERY'"+[Environment]::NewLine))
    $log=Join-Path $ResultDir ("recovered-$upgrade.log")
    if((RunInstall $target $log) -eq 0){throw 'Expected second install failure'}
    $messages=Get-Content $log -Raw
    if(-not $messages.Contains('INSTALL_INTERRUPTION_RECOVERY=PASS') -or -not $messages.Contains('INSTALL_ROLLBACK=PASS')){throw "Interruption not recovered: $log"}
    $after=Snapshot $target
    if($before.Count -ne $after.Count -or @($before.Keys|Where-Object {$before[$_] -ne $after[$_]}).Count){throw 'Interruption preimage mismatch'}
    $checks++;Write-Host "PASS process interruption recovered upgrade=$upgrade; exact original hashes"
}
# Test recovery-file validation and a failed restore without touching real shortcuts.
. (Join-Path $root 'installer/InstallTransaction.ps1')
$target=Join-Path $fixture 'helper-tests'
Put $target 'QLCAD.exe' 'old-program'
$tx=Start-InstallTransaction $target
Register-InstallWrite $tx (Join-Path $target 'QLCAD.exe')
Put $target 'QLCAD.exe' 'new-program'
$held=[IO.File]::Open((Join-Path $target 'QLCAD.exe'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
$failed=$false
try {try {Undo-InstallTransaction $tx} catch {$failed=$true}} finally {$held.Dispose()}
if(-not $failed -or -not(Test-Path -LiteralPath (Join-Path $tx.Backup 'journal.json'))){throw 'Failed restore did not retain recovery evidence'}
Undo-InstallTransaction (Import-InstallTransaction $tx.Backup $target)
if([IO.File]::ReadAllText((Join-Path $target 'QLCAD.exe')) -ne 'old-program'){throw 'Retry restore failed'}
$checks++;Write-Host 'PASS failed rollback retains journal and later restores exactly'

$tx=Start-InstallTransaction $target
Register-InstallWrite $tx (Join-Path $target 'QLCAD.exe')
Put $target 'QLCAD.exe' 'new-program'
$journal=Join-Path $tx.Backup 'journal.json'
$saved=[IO.File]::ReadAllText($journal)
foreach($tamper in @('escape','backup-hash')) {
    if($tamper -eq 'escape') {
        $bad=$saved|ConvertFrom-Json;$bad.Entries[0].Path=Join-Path $fixture 'outside.exe';$bad|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $journal -Encoding UTF8
    } else {Put $tx.Backup '0.bin' 'tampered-backup'}
    $rejected=$false
    try {Import-InstallTransaction $tx.Backup $target|Out-Null} catch {$rejected=$true}
    if(-not $rejected -or [IO.File]::ReadAllText((Join-Path $target 'QLCAD.exe')) -ne 'new-program'){throw 'Invalid recovery evidence modified installation'}
    $checks++;Write-Host "PASS tampered recovery rejected before restoration: $tamper"
    [IO.File]::WriteAllText($journal,$saved,[Text.UTF8Encoding]::new($true))
}
# Only repair our synthetic backup, then restore normally.
Put $tx.Backup '0.bin' 'old-program'
Undo-InstallTransaction (Import-InstallTransaction $tx.Backup $target)

$savedAppData=$env:APPDATA
try {
    $env:APPDATA=Join-Path $fixture 'isolated-appdata'
    $link=Join-Path $env:APPDATA 'Microsoft/Windows/Start Menu/Programs/QLCAD.lnk'
    Put ([IO.Path]::GetDirectoryName($link)) 'QLCAD.lnk' 'old-link-bytes'
    $tx=Start-InstallTransaction $target
    Register-InstallWrite $tx $link
    [IO.File]::WriteAllText($link,'new-link-bytes')
    Undo-InstallTransaction (Import-InstallTransaction $tx.Backup $target)
    if([IO.File]::ReadAllText($link) -ne 'old-link-bytes'){throw 'Shortcut bytes not restored'}
    $checks++;Write-Host 'PASS isolated StartMenu shortcut bytes restored'
} finally {$env:APPDATA=$savedAppData}

# A completed rollback whose snapshot cleanup was interrupted must be resumable.
$tx=Start-InstallTransaction $target
Register-InstallWrite $tx (Join-Path $target 'QLCAD.exe')
$tx.State='RolledBack';Save-InstallTransaction $tx
Remove-Item -LiteralPath (Join-Path $tx.Backup '0.bin')
Restore-PendingInstallTransaction $target
if(Test-Path -LiteralPath $tx.Backup){throw 'Completed rollback cleanup did not resume'}
$checks++;Write-Host 'PASS interrupted rollback cleanup resumes without snapshots'
# Do not remove personal configuration modified after an interrupted first installation.
$personalTarget=Join-Path $fixture 'personal-after-interruption'
$tx=Start-InstallTransaction $personalTarget
$source=Join-Path $package 'settings.json'
Register-InstallWrite $tx (Join-Path $personalTarget 'settings.json') (Get-FileHash -LiteralPath $source).Hash
Put $personalTarget 'settings.json' '{"editedAfterInterruption":true}'
$rejected=$false
try {Undo-InstallTransaction $tx} catch {$rejected=$true}
if(-not $rejected -or -not(Test-Path -LiteralPath (Join-Path $personalTarget 'settings.json'))){throw 'Modified personal settings lost'}
$checks++;Write-Host 'PASS modified personal configuration blocks destructive recovery'
# Evidence intentionally retained for this refusal, rather than deleting personal content.
$preservedRecovery=$tx.Backup
Put $package 'Install.ps1' $original
if(@(Get-ChildItem -LiteralPath $fixture -Directory -Filter '.dwgc2e-install-recovery-*' | Where-Object {$_.FullName -ne $preservedRecovery}).Count){throw 'Successful rollback left recovery snapshots'}
Write-Host "INSTALL_TRANSACTION_TESTS=PASS ($checks failure scenarios); FIXTURE=$fixture"
