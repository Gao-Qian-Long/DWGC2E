# Purpose: isolated installer regression, without shortcuts, launch, or real user data.
# Input: optional -ResultDir; output: case results; fixtures are retained for inspection.
param([string]$ResultDir)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $ResultDir) { $ResultDir = Join-Path $root ('artifacts/installer-test-' + [guid]::NewGuid().ToString('N')) }
$fixture = Join-Path $ResultDir ('fixture-' + [guid]::NewGuid().ToString('N'))
$package = Join-Path $fixture 'package'
$target = Join-Path $fixture 'installed'
New-Item -ItemType Directory -Force -Path $package | Out-Null
$checks = 0
function Check($value, [string]$message) { if (-not $value) { throw $message }; $script:checks++; Write-Host "PASS $message" }
function Put([string]$relative,[string]$value) {
    $file = Join-Path $package $relative
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($file)) | Out-Null
    [IO.File]::WriteAllText($file,$value)
}
Put 'DwgTranslator.exe' 'fake-executable-v1-never-launched'
Put 'settings.json' '{"sample":true}'
Put 'CadPlugin/DwgTranslator.Cad.dll' 'fake-plugin-v1'
Put 'CadPlugin/DwgTranslator.Core.dll' 'fake-core-v1'
Put 'CadPlugin/cad-platform.txt' 'GstarCAD'
Put 'glossaries/mechanical_zh_en.json' '{}'
Put 'assets/default-glossaries/mechanical_zh_en.json' '{}'
$installer = Join-Path $root 'installer/Install.ps1'
# Execute only the parsed pure selector, never the installer's top-level installation code.
$parseErrors=$null; $tokens=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($installer,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Installer parse failed'}
$selector=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-DefaultInstallDirectory'},$true)
if(-not $selector){throw 'Default directory selector missing'}
. ([scriptblock]::Create($selector.Extent.Text))
Check ((Get-DefaultInstallDirectory $true) -eq 'D:\DWGC2E') 'default prefers D when available'
Check ((Get-DefaultInstallDirectory $false) -eq 'C:\DWGC2E') 'default falls back to C without D'
function Install([string]$destination,[bool]$success) {
    $log = Join-Path $fixture ('run-' + [guid]::NewGuid().ToString('N') + '.log')
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -SourceDir $package -TargetDir $destination -NoLaunch -NoPrompt -NoShortcuts *> $log
    $ErrorActionPreference = $previousPreference
    if ($success -and $LASTEXITCODE -ne 0) { throw "Installer failed; see $log" }
    if (-not $success -and $LASTEXITCODE -eq 0) { throw "Expected rejection; see $log" }
}
Install $target $true
Check (Test-Path -LiteralPath (Join-Path $target 'assets/default-glossaries/mechanical_zh_en.json')) 'fresh install has default glossary'
Check (Test-Path -LiteralPath (Join-Path $target 'CadPlugin/DwgTranslator.Core.dll')) 'fresh install has plugin dependency'
Check ((Get-Content -LiteralPath (Join-Path $target 'settings.json') -Raw) -eq '{"sample":true}') 'fresh install seeds settings'
[IO.File]::WriteAllText((Join-Path $target 'settings.json'),'{"personal":"preserve"}')
New-Item -ItemType Directory -Force -Path (Join-Path $target 'prompts') | Out-Null
[IO.File]::WriteAllText((Join-Path $target 'prompts/deepl_context.txt'),'legacy-client-prompt')
[IO.File]::WriteAllText((Join-Path $target 'glossaries/mechanical_zh_en.json'),'{"personal":"term"}')
Put 'DwgTranslator.exe' 'fake-executable-v2-never-launched'
Put 'CadPlugin/DwgTranslator.Cad.dll' 'fake-plugin-v2'
Put 'glossaries/new-default.json' '{}'
Install $target $true
Check ((Get-Content -LiteralPath (Join-Path $target 'settings.json') -Raw) -eq '{"personal":"preserve"}') 'reinstall preserves settings'
Check (-not(Test-Path -LiteralPath (Join-Path $target 'prompts/deepl_context.txt'))) 'reinstall removes obsolete client-side prompt'
Check ((Get-Content -LiteralPath (Join-Path $target 'glossaries/mechanical_zh_en.json') -Raw) -eq '{"personal":"term"}') 'reinstall preserves glossary'
Check ((Get-Content -LiteralPath (Join-Path $target 'CadPlugin/DwgTranslator.Cad.dll') -Raw) -eq 'fake-plugin-v2') 'reinstall updates plugin'
Check (Test-Path -LiteralPath (Join-Path $target 'glossaries/new-default.json')) 'reinstall adds missing default'
function Snapshot { @{} + (Get-ChildItem -LiteralPath $target -Recurse -File | ForEach-Object -Begin { $h=@{} } -Process { $h[$_.FullName]=(Get-FileHash -LiteralPath $_.FullName).Hash } -End { $h }) }
$before = Snapshot
# Remove no source data: rename just this fixture resource to emulate an incomplete package.
$resource = Join-Path $package 'assets/default-glossaries/mechanical_zh_en.json'
Rename-Item -LiteralPath $resource -NewName 'missing.json'
Install $target $false
$after = Snapshot
Check (($before.Count -eq $after.Count) -and -not @($before.Keys | Where-Object { $before[$_] -ne $after[$_] }).Count) 'incomplete package leaves installation unchanged'
Rename-Item -LiteralPath (Join-Path $package 'assets/default-glossaries/missing.json') -NewName 'mechanical_zh_en.json'
Put 'settings.json' 'invalid-json'
Install $target $false
$after = Snapshot
Check (($before.Count -eq $after.Count) -and -not @($before.Keys | Where-Object { $before[$_] -ne $after[$_] }).Count) 'invalid configuration leaves installation unchanged'
Put 'settings.json' '{}'
Install $package $false
Check ((Get-Content -LiteralPath (Join-Path $package 'DwgTranslator.exe') -Raw) -eq 'fake-executable-v2-never-launched') 'overlapping source and target rejected'
$exe = Join-Path $target 'DwgTranslator.exe'
$hash = (Get-FileHash -LiteralPath $exe).Hash
$lock = [IO.File]::Open($exe,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try { Install $target $false } finally { $lock.Dispose() }
Check ((Get-FileHash -LiteralPath $exe).Hash -eq $hash) 'locked executable is not deleted or renamed'
Check (-not (Test-Path -LiteralPath ($exe + '.old'))) 'failed copy creates no old executable workaround'
# Later write failures must be detected before the executable has been replaced.
foreach($relative in @('CadPlugin/DwgTranslator.Core.dll','assets/default-glossaries/mechanical_zh_en.json','Uninstall.ps1','installation-manifest.json','卸载.cmd')) {
    Put 'DwgTranslator.exe' ('synthetic-next-version-'+$relative)
    $before=Snapshot
    $held=[IO.File]::Open((Join-Path $target $relative),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {Install $target $false} finally {$held.Dispose()}
    $after=Snapshot
    Check ($before.Count -eq $after.Count -and -not @($before.Keys | Where-Object {$before[$_] -ne $after[$_]}).Count) ('late destination lock leaves all files unchanged: '+$relative)
}
Put 'DwgTranslator.exe' 'fake-executable-v2-never-launched'
# The portable bootstrap must never kill another installation or delete user data.
$uninstallText = Get-Content -LiteralPath (Join-Path $target '卸载.cmd') -Raw
Check ($uninstallText -notmatch '(?im)^\s*(taskkill|rmdir|rd|del|erase)\b') 'uninstall bootstrap performs no automatic destructive actions'
$inno = Get-Content -LiteralPath (Join-Path $root 'installer/DwgTranslator.iss') -Raw
foreach ($resource in @('settings.json','glossaries')) {
    $line = @($inno -split "`n" | Where-Object { $_ -like 'Source:*' -and $_.Contains("\$resource") -and $_.Contains('DestDir: "{app}') })
    Check ($line.Count -eq 1 -and $line[0].Contains('onlyifdoesntexist') -and $line[0].Contains('uninsneveruninstall')) "Inno preserves portable $resource"
}
# Execute the packaged script from another working directory without SourceDir.
Copy-Item -LiteralPath $installer -Destination (Join-Path $package 'Install.ps1')
Copy-Item -LiteralPath (Join-Path $root 'installer/Uninstall.ps1') -Destination (Join-Path $package 'Uninstall.ps1')
Copy-Item -LiteralPath (Join-Path $root 'installer/InstallTransaction.ps1') -Destination (Join-Path $package 'InstallTransaction.ps1')
$defaultTarget = Join-Path $fixture 'default-source-installed'
$log = Join-Path $fixture 'default-source.log'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'Install.ps1') -TargetDir $defaultTarget -NoLaunch -NoPrompt -NoShortcuts *> $log
Check ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath (Join-Path $defaultTarget 'DwgTranslator.exe'))) 'package defaults to its own directory not caller working directory'
$emptyPackage = Join-Path $fixture 'empty-package'
New-Item -ItemType Directory -Path $emptyPackage | Out-Null
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -SourceDir $emptyPackage -TargetDir (Join-Path $fixture 'must-not-install') -NoLaunch -NoPrompt -NoShortcuts *> (Join-Path $fixture 'empty-package.log')
Check ($LASTEXITCODE -eq 2) 'missing executable returns nonzero exit code 2'
# Exercise the actual installed uninstaller, not only its bootstrap text.
$manifestPath=Join-Path $target 'installation-manifest.json'
$manifestText=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
$uninstaller=Join-Path $target 'Uninstall.ps1'
function Uninstall([bool]$success,[switch]$Preview) {
    $args=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$uninstaller,'-ConfirmRemoval')
    if($Preview){$args+='-Preview'}
    $log = Join-Path $fixture ('uninstall-'+[guid]::NewGuid().ToString('N')+'.log')
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & powershell.exe @args *> $log; $exitCode = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previousPreference }
    Check (($exitCode -eq 0) -eq $success) 'uninstall expected outcome'
}
# -NoShortcuts must preserve ownership metadata without accessing user shortcut folders.
$withShortcuts=$manifestText | ConvertFrom-Json
$withShortcuts | Add-Member -NotePropertyName Shortcuts -NotePropertyValue @(@{Kind='Desktop';Sha256=('0'*64)},@{Kind='StartMenu';Sha256=('1'*64)}) -Force
$withShortcuts | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Install $target $true
$retained=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
Check (@($retained.Shortcuts).Count -eq 2) 'NoShortcuts reinstall retains both ownership records'
Check (@($retained.Shortcuts | Where-Object {$_.Kind -eq 'Desktop' -and $_.Sha256 -eq ('0'*64)}).Count -eq 1 -and @($retained.Shortcuts | Where-Object {$_.Kind -eq 'StartMenu' -and $_.Sha256 -eq ('1'*64)}).Count -eq 1) 'NoShortcuts reinstall preserves exact ownership hashes'
# Restore an actual legacy manifest with no optional Shortcuts field.
$legacy=$manifestText | ConvertFrom-Json
$legacy.PSObject.Properties.Remove('Shortcuts')
$legacy | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
$before=Snapshot
Uninstall $true -Preview
$after=Snapshot
Check ($before.Count -eq $after.Count -and -not @($before.Keys | Where-Object {$before[$_] -ne $after[$_]}).Count) 'legacy manifest without Shortcuts remains accepted without writes'
[IO.File]::WriteAllText($manifestPath,$manifestText,[Text.UTF8Encoding]::new($true))
$before=Snapshot $target
Uninstall $true -Preview
$after=Snapshot $target
Check ($before.Count -eq $after.Count -and -not @($before.Keys | Where-Object {$before[$_] -ne $after[$_]}).Count) 'uninstall preview changes nothing'
$manifest=$manifestText | ConvertFrom-Json
$manifest.Files[0].Path='assets\..\..\outside.txt'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Uninstall $false
Check (Test-Path -LiteralPath (Join-Path $target 'DwgTranslator.exe')) 'path traversal rejected before removing executable'
[IO.File]::WriteAllText($manifestPath,$manifestText,[Text.UTF8Encoding]::new($true))
# Manifest corruption must be rejected before any owned file is removed.
foreach($case in @('wrong-root','protected-settings','duplicate-path','invalid-hash')) {
    $manifest=$manifestText | ConvertFrom-Json
    switch($case) {
        'wrong-root' {$manifest.Root=Join-Path $fixture 'other-installation'}
        'protected-settings' {$manifest.Files[0].Path='settings.json';$manifest.Files[0].Sha256=(Get-FileHash -LiteralPath (Join-Path $target 'settings.json')).Hash}
        'duplicate-path' {$manifest.Files=@($manifest.Files)+@($manifest.Files[0])}
        'invalid-hash' {$manifest.Files[0].Sha256='not-a-hash'}
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $before=Snapshot
    Uninstall $false
    $after=Snapshot
    Check ($before.Count -eq $after.Count -and -not @($before.Keys | Where-Object {$before[$_] -ne $after[$_]}).Count) ("invalid manifest leaves every file intact: "+$case)
}
[IO.File]::WriteAllText($manifestPath,$manifestText,[Text.UTF8Encoding]::new($true))
# Reject source markers before writing anything, for both install and uninstall.
foreach($marker in @('DwgTranslator.sln','.git')) {
    $markerPath=Join-Path $target $marker
    [IO.File]::WriteAllText($markerPath,'synthetic source marker')
    $before=Snapshot
    Install $target $false
    $after=Snapshot
    Check ($before.Count -eq $after.Count -and -not @($before.Keys | Where-Object {$before[$_] -ne $after[$_]}).Count) ("source install rejected without changes: "+$marker)
    Uninstall $false
    $after=Snapshot
    Check ($before.Count -eq $after.Count -and -not @($before.Keys | Where-Object {$before[$_] -ne $after[$_]}).Count) ("source uninstall rejected without changes: "+$marker)
    # Only this test-created marker is removed; never recurse or touch real source.
    Remove-Item -LiteralPath $markerPath
}
# Malformed records fail before any COM shortcut lookup or file removal.
foreach($case in @('unknown-kind','bad-shortcut-hash','too-many-shortcuts')) {
    $manifest=$manifestText | ConvertFrom-Json
    $records=switch($case) {
        'unknown-kind' {@{Kind='ArbitraryPath';Sha256=('0'*64)}}
        'bad-shortcut-hash' {@{Kind='Desktop';Sha256='invalid'}}
        'too-many-shortcuts' {@{Kind='Desktop';Sha256=('0'*64)};@{Kind='Desktop';Sha256=('0'*64)};@{Kind='StartMenu';Sha256=('0'*64)}}
    }
    $manifest | Add-Member -NotePropertyName Shortcuts -NotePropertyValue @($records) -Force
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $before=Snapshot
    Uninstall $false
    $after=Snapshot
    Check ($before.Count -eq $after.Count -and -not @($before.Keys | Where-Object {$before[$_] -ne $after[$_]}).Count) ("invalid shortcut records preserve every file: "+$case)
}
[IO.File]::WriteAllText($manifestPath,$manifestText,[Text.UTF8Encoding]::new($true))
$exeHash=(Get-FileHash -LiteralPath $exe).Hash
$locked=Join-Path $target 'CadPlugin/DwgTranslator.Core.dll'
$lock=[IO.File]::Open($locked,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{Uninstall $false}finally{$lock.Dispose()}
Check ((Get-FileHash -LiteralPath $exe).Hash -eq $exeHash) 'all-file lock preflight prevents partial uninstall'
$personal=@{}
foreach($relative in @('settings.json','prompts/user-note.txt','glossaries/mechanical_zh_en.json','exports/user.dwg','logs/user.log','unknown.txt','assets/unknown.txt','CadPlugin/user-note.txt')){
    $path=Join-Path $target $relative
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path,'personal-data')
    $personal[$path]=(Get-FileHash -LiteralPath $path).Hash
}
$modified=Join-Path $target 'assets/default-glossaries/mechanical_zh_en.json'
[IO.File]::WriteAllText($modified,'user-edited-default')
$personal[$modified]=(Get-FileHash -LiteralPath $modified).Hash
Uninstall $true
Check (-not(Test-Path -LiteralPath $exe)) 'uninstall removes unchanged executable'
Check (-not(Test-Path -LiteralPath $locked)) 'uninstall removes unchanged plugin dependency'
foreach($path in $personal.Keys){Check ((Get-FileHash -LiteralPath $path).Hash -eq $personal[$path]) ('uninstall preserves '+$path.Substring($target.Length+1))}
Uninstall $true
Check (Test-Path -LiteralPath $manifestPath) 'repeat uninstall safe; ownership evidence retained'
Write-Host "INSTALLER_TESTS=PASS ($checks cases); FIXTURE=$fixture"
