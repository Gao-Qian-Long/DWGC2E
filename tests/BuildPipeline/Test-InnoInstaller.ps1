# Purpose: compile and exercise Inno in a unique workspace sandbox, never the real installation.
# Usage: powershell -File tests/BuildPipeline/Test-InnoInstaller.ps1 -PublishDir <verified candidate>
# Output: retained fixtures, logs and results under artifacts; requires installed Inno 6 + Chinese ISL.
param([Parameter(Mandatory=$true)][string]$PublishDir,[string]$Compiler,[string]$ResultDir,[string]$PreviousPublishDir)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\')
if(-not $Compiler){$Compiler=Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'}
if(-not (Test-Path -LiteralPath $Compiler -PathType Leaf)){throw 'Inno compiler not found'}
$publish=(Resolve-Path -LiteralPath $PublishDir).Path
$payload=@(& (Join-Path $root 'tools/Get-DesktopPayload.ps1') -PublishDir $publish)
$info=Get-Content -LiteralPath (Join-Path $publish 'build-info.json') -Raw | ConvertFrom-Json
if((Get-FileHash -LiteralPath (Join-Path $publish 'DwgTranslator.exe')).Hash -ne $info.sha256){throw 'Unverified candidate executable'}
if(-not $ResultDir){$ResultDir=Join-Path $root ('artifacts/inno-test-'+[guid]::NewGuid().ToString('N'))}
$ResultDir=[IO.Path]::GetFullPath($ResultDir).TrimEnd('\')
if(-not $ResultDir.StartsWith($root+'\artifacts\',[StringComparison]::OrdinalIgnoreCase)){throw 'Results must be under workspace artifacts'}
$ancestor=$ResultDir
while($ancestor){if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked results path rejected'};$ancestor=[IO.Path]::GetDirectoryName($ancestor)}
New-Item -ItemType Directory -Force -Path $ResultDir | Out-Null
$sandbox=Join-Path $ResultDir ('fixture-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
$install=Join-Path $sandbox 'app'
$data=Join-Path $sandbox 'user-data'
$checks=New-Object 'System.Collections.Generic.List[string]'
function Check($value,[string]$label){if(-not $value){throw $label};$checks.Add($label);Write-Host "PASS $label"}
function Hash([string]$p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}
function Put([string]$p,[string]$value){New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($p)) | Out-Null;[IO.File]::WriteAllText($p,$value)}
function Snapshot([string]$dir){$result=@{};if(Test-Path -LiteralPath $dir){Get-ChildItem -LiteralPath $dir -File -Recurse -Force | ForEach-Object {$result[$_.FullName]=Hash $_.FullName}};return $result}
function EqualSnapshot($left,$right){if($left.Count -ne $right.Count){return $false};foreach($key in $left.Keys){if($left[$key] -ne $right[$key]){return $false}};return $true}
function Compile([string]$name,[bool]$isolated,[string]$source=$publish){$out=Join-Path $sandbox $name;New-Item -ItemType Directory -Path $out | Out-Null;$args=@("/DPublishDir=$source","/O$out");if($isolated){$args+="/DTestRoot=$sandbox"};$args+=Join-Path $root 'installer/DwgTranslator.iss';& $Compiler @args *> (Join-Path $ResultDir ($name+'.log'));if($LASTEXITCODE -ne 0){throw "Compile failed: $name"};$built=(Get-ChildItem -LiteralPath $out -Filter '*.exe' | Select-Object -First 1).FullName; & (Join-Path $root 'tools/Verify-ExecutableIcon.ps1') -ExecutablePath $built | Out-Host; return $built}
function Run([string]$exe,[string]$label,[string[]]$extra=@(),[bool]$success=$true){
 $resolved=[IO.Path]::GetFullPath($exe)
 if(-not $resolved.StartsWith($sandbox+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Refusing executable outside fixture'}
 # Check every target ancestor before invoking an uninstaller or installer.
 $a=[IO.Path]::GetDirectoryName($resolved)
 while($a){if((Get-Item -LiteralPath $a -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked target rejected'};if($a -eq $sandbox){break};$a=[IO.Path]::GetDirectoryName($a)}
 if(Test-Path -LiteralPath $install){if(Get-ChildItem -LiteralPath $install -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint} | Select-Object -First 1){throw 'Linked installation rejected'}}
 $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+$ResultDir+'\'+$label+'.log"'))+$extra
 $p=Start-Process -FilePath $resolved -ArgumentList $arguments -WindowStyle Hidden -PassThru
 if(-not $p.WaitForExit(120000)){throw "Timed out: $label; inspect PID $($p.Id) before proceeding"}
 if($success -and $p.ExitCode -ne 0){throw "$label failed with $($p.ExitCode)"}
 if(-not $success -and $p.ExitCode -eq 0){throw "$label unexpectedly succeeded"}
}
$realData=Join-Path $env:APPDATA 'DwgTranslator'
# The actual application may be running and writing logs/tasks. Only inspect the
# production seed destinations; the isolated build never targets real appdata.
function RealSeedSnapshot {
 $h=Snapshot (Join-Path $realData 'glossaries')
 $settings=Join-Path $realData 'settings.json'
 if(Test-Path -LiteralPath $settings){$h[$settings]=Hash $settings}
 return $h
}
$realBefore=RealSeedSnapshot
$releaseBefore=Hash (Join-Path $root 'release/DwgTranslator.exe')
$registryKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}_is1'
$registryBefore=if(Test-Path $registryKey){Get-ItemProperty $registryKey | ConvertTo-Json -Depth 4}else{'ABSENT'}
try {
 $normal=Compile 'production-build' $false
 Check (Test-Path -LiteralPath $normal) 'normal installer compiles (never executed)'
 $setup=Compile 'isolated-build' $true
 Check (Test-Path -LiteralPath $setup) 'isolated installer compiles'
 $escaped=Join-Path $sandbox 'rejected-destination'
 Run $setup 'reject-override' @('/CURRENTUSER',('/DIR="'+$escaped+'"')) $false
 Check (-not(Test-Path -LiteralPath (Join-Path $escaped 'DwgTranslator.exe'))) 'destination override rejected'
 # Exercise this exact candidate from an empty installation before the upgrade path.
 Run $setup 'clean-install' @('/CURRENTUSER','/TASKS="desktopicon,startmenu"','/LANG=chinesesimplified')
 foreach($item in $payload){Check ((Hash (Join-Path $install $item.path)) -eq $item.sha256) ('clean install payload: '+$item.path)}
 Check ((Hash (Join-Path $data 'settings.json')) -eq (Hash (Join-Path $publish 'settings.json'))) 'clean user defaults need no manual configuration'
 Run (Join-Path $install 'unins000.exe') 'clean-uninstall'
 $deadline=(Get-Date).AddSeconds(10)
 while((Test-Path -LiteralPath (Join-Path $install 'DwgTranslator.exe')) -and (Get-Date) -lt $deadline){Start-Sleep -Milliseconds 200}
 Check (-not(Test-Path -LiteralPath (Join-Path $install 'DwgTranslator.exe'))) 'clean uninstall removes executable'
 # Reset only verified seeded fixture defaults; no production/user data is touched.
 foreach($relative in @('app/settings.json','app/glossaries/mechanical_zh_en.json','user-data/settings.json','user-data/glossaries/mechanical_zh_en.json')){
  $seed=Join-Path $sandbox $relative
  $sourceRelative=$relative.Substring($relative.IndexOf('/')+1)
  if(Test-Path -LiteralPath $seed){Check ((Hash $seed) -eq (Hash (Join-Path $publish $sourceRelative))) ('clean fixture seed unchanged: '+$relative);Remove-Item -LiteralPath $seed}
 }
 $upgradeScope = 'not tested'
 $upgradeKeep = @{}
 if ($PreviousPublishDir) {
   $previous = (Resolve-Path -LiteralPath $PreviousPublishDir).Path
   $oldInfo = Get-Content -LiteralPath (Join-Path $previous 'build-info.json') -Raw | ConvertFrom-Json
   Check ((Hash (Join-Path $previous 'DwgTranslator.exe')) -eq $oldInfo.sha256) 'previous build executable matches metadata'
   Check ($oldInfo.sha256 -ne $info.sha256 -and $oldInfo.version -ne $info.version) 'upgrade uses two genuinely different builds'
   $oldSetup = Compile 'previous-isolated-build' $true $previous
   Run $oldSetup 'previous-install' @('/CURRENTUSER','/TASKS="desktopicon,startmenu"','/LANG=chinesesimplified')
   Check ((Hash (Join-Path $install 'DwgTranslator.exe')) -eq $oldInfo.sha256) 'previous build installed before upgrade'
   foreach ($relative in @('user-data/exports/upgrade.txt','app/exports/upgrade.txt','app/unknown-upgrade.txt')) {
     $path = Join-Path $sandbox $relative
     Put $path 'unique previous-build user content'
     $upgradeKeep[$path] = Hash $path
   }
   $upgradeScope = [string]$oldInfo.version + ' -> ' + [string]$info.version
 }
 Run $setup 'fresh-install' @('/CURRENTUSER','/TASKS="desktopicon,startmenu"','/LANG=chinesesimplified')
 foreach($path in $upgradeKeep.Keys){Check ((Hash $path) -eq $upgradeKeep[$path]) 'cross-build upgrade preserves user file'}
 Check ((Hash (Join-Path $install 'DwgTranslator.exe')) -eq $info.sha256) 'installed executable matches verified candidate'
 foreach($relative in @('settings.json','glossaries/mechanical_zh_en.json','assets/default-glossaries/mechanical_zh_en.json','CadPlugin/DwgTranslator.Cad.dll','CadPlugin/DwgTranslator.Core.dll')){$expectedSource=$publish;if($PreviousPublishDir -and $relative -in @('settings.json','glossaries/mechanical_zh_en.json')){$expectedSource=$previous};Check ((Hash (Join-Path $install $relative)) -eq (Hash (Join-Path $expectedSource $relative))) "installed resource obeys ownership: $relative"}
 foreach($item in $payload){
  $expected=$item.sha256
  if($PreviousPublishDir -and $item.path -in @('settings.json','glossaries/mechanical_zh_en.json')){$expected=Hash (Join-Path $previous $item.path)}
  Check ((Hash (Join-Path $install $item.path)) -eq $expected) ('full payload hash: '+$item.path)
 }
 foreach($file in Get-ChildItem -LiteralPath $install -File -Recurse){
  $relative=$file.FullName.Substring($install.Length+1).Replace('\','/')
  $allowed=$relative -in @($payload.path) -or $relative -in @('使用说明.txt','unins000.exe','unins000.dat','exports/upgrade.txt','unknown-upgrade.txt')
  Check $allowed ('installed file allowlist: '+$relative)
 }
 Check (Test-Path -LiteralPath (Join-Path $data 'settings.json')) 'isolated appdata seeded'
 $links=@(Get-ChildItem -LiteralPath (Join-Path $sandbox 'shortcuts') -Recurse -Filter '*.lnk')
 Check ($links.Count -eq 5) 'five shortcuts created inside sandbox'
 Check ((Get-Content -LiteralPath (Join-Path $ResultDir 'fresh-install.log') -Raw) -match 'Drive root guard branches passed') 'compiled drive-root predicate passes drive roots and normal directories'
 foreach($marker in @('DwgTranslator.sln','.git')) {
   $markerPath=Join-Path $install $marker
   if(Test-Path -LiteralPath $markerPath){throw 'Synthetic marker already exists'}
   Put $markerPath 'synthetic source marker'
   $snapshotBefore=Snapshot $install
   $label=if($marker -eq '.git'){'reject-git-source'}else{'reject-solution-source'}
   Run $setup $label @('/CURRENTUSER') $false
   Check ((Get-Content -LiteralPath (Join-Path $ResultDir ($label+'.log')) -Raw) -match 'Cannot install into a source workspace') ("source guard reports exact reason: "+$marker)
   Check (EqualSnapshot $snapshotBefore (Snapshot $install)) ("source marker rejection preserves entire installation: "+$marker)
   Remove-Item -LiteralPath $markerPath
 }
 $personal=@{ 'app/settings.json'='{"personal":"keep"}';'app/prompts/user-note.txt'='personal note';'app/glossaries/mechanical_zh_en.json'='{"personal":"term"}';'user-data/settings.json'='{"account":"isolated"}';'user-data/glossaries/mechanical_zh_en.json'='{"custom":"keep"}';'app/exports/sample.txt'='output';'app/logs/user.log'='log';'app/unknown-user-file.txt'='unknown';'user-data/exports/sample.txt'='appdata output' }
 $keep=@{};foreach($relative in $personal.Keys){$path=Join-Path $sandbox $relative;Put $path $personal[$relative];$keep[$path]=Hash $path}
 # A real earlier UI-version candidate is not required: this is same-version repair/overwrite acceptance.
 Put (Join-Path $install 'CadPlugin/DwgTranslator.Cad.dll') 'corrupt fixture plugin'
 Run $setup 'repair-install' @('/CURRENTUSER','/TASKS="desktopicon,startmenu"','/LANG=chinesesimplified')
 foreach($path in $keep.Keys){Check ((Hash $path) -eq $keep[$path]) ('reinstall preserves '+$path.Substring($sandbox.Length+1))}
 Check ((Hash (Join-Path $install 'CadPlugin/DwgTranslator.Cad.dll')) -eq (Hash (Join-Path $publish 'CadPlugin/DwgTranslator.Cad.dll'))) 'reinstall repairs owned plugin'
 $uninstaller=Join-Path $install 'unins000.exe'
 Check (Test-Path -LiteralPath $uninstaller) 'uninstaller exists inside verified fixture'
 Run $uninstaller 'uninstall'
 # Uninstaller may finish its self-cleanup in a child process.
 $deadline=(Get-Date).AddSeconds(10)
 while((Test-Path -LiteralPath (Join-Path $install 'DwgTranslator.exe')) -and (Get-Date) -lt $deadline){Start-Sleep -Milliseconds 200}
 foreach($relative in @('DwgTranslator.exe','CadPlugin/DwgTranslator.Cad.dll','CadPlugin/DwgTranslator.Core.dll','assets/default-glossaries/mechanical_zh_en.json')){Check (-not(Test-Path -LiteralPath (Join-Path $install $relative))) "uninstall removes owned $relative"}
 foreach($path in $keep.Keys){Check ((Hash $path) -eq $keep[$path]) ('uninstall preserves '+$path.Substring($sandbox.Length+1))}
 Check (@(Get-ChildItem -LiteralPath (Join-Path $sandbox 'shortcuts') -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue).Count -eq 0) 'uninstall removes sandbox shortcuts'
 $registryAfter=if(Test-Path $registryKey){Get-ItemProperty $registryKey | ConvertTo-Json -Depth 4}else{'ABSENT'}
 Check ($registryBefore -eq $registryAfter) 'production uninstall registry unchanged'
 Check (EqualSnapshot $realBefore (RealSeedSnapshot)) 'real appdata settings and glossaries unchanged'
 Check ($releaseBefore -eq (Hash (Join-Path $root 'release/DwgTranslator.exe'))) 'canonical release unchanged'
 @{Passed=$checks.Count;Checks=@($checks);Sandbox=$sandbox;ProductionInstaller=$normal;TestInstaller=$setup;Version=$info.version;Upgrade=$upgradeScope;Scope='isolated install, optional real cross-build upgrade, same-version repair, uninstall; not semantic-version migration or transactional failure recovery'} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ResultDir 'results.json') -Encoding UTF8
 Write-Host "INNO_ACCEPTANCE=PASS ($($checks.Count) checks); evidence=$ResultDir"
} catch {
 $_ | Out-String | Set-Content -LiteralPath (Join-Path $ResultDir 'failure.txt')
 throw
}