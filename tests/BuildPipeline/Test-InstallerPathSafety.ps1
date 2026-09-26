# Purpose: verify running-instance and junction guards against isolated synthetic installations.
# Usage: powershell -File tests/BuildPipeline/Test-InstallerPathSafety.ps1 -NodeExe <node.exe> [-ResultDir <fresh artifacts child>]
# Output: JSON, logs, retained synthetic fixtures/junctions. No real APP/CAD execution or recursive deletion.
param([Parameter(Mandatory=$true)][string]$NodeExe,[string]$ResultDir)
$ErrorActionPreference='Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\')
if(-not $ResultDir){$ResultDir=Join-Path $root ('artifacts/installer-safety-20260916-resumed/path-safety-'+[guid]::NewGuid().ToString('N'))}
$ResultDir=[IO.Path]::GetFullPath($ResultDir).TrimEnd('\')
if(-not $ResultDir.StartsWith($root+'\artifacts\',[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture must be beneath artifacts'}
$a=$ResultDir
while($a){if((Test-Path -LiteralPath $a) -and ((Get-Item -LiteralPath $a -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Preexisting linked fixture path'};$a=[IO.Path]::GetDirectoryName($a)}
if(Test-Path -LiteralPath $ResultDir){throw 'Choose a fresh result directory'}
if(-not(Test-Path -LiteralPath $NodeExe -PathType Leaf)){throw 'Node executable required for benign child process'}
New-Item -ItemType Directory -Path $ResultDir|Out-Null
$checks=New-Object 'System.Collections.Generic.List[string]'
function Check($v,[string]$label){if(-not $v){throw $label};$checks.Add($label);Write-Host "PASS $label"}
function Hash([string]$p){(Get-FileHash -LiteralPath $p).Hash}
function Put([string]$p,[string]$v){New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($p))|Out-Null;[IO.File]::WriteAllText($p,$v)}
function Snapshot([string]$dir){$h=@{};Get-ChildItem -LiteralPath $dir -Recurse -File|ForEach-Object{$h[$_.FullName]=Hash $_.FullName};return $h}
function Equal($a,$b){if($a.Count -ne $b.Count){return $false};foreach($k in $a.Keys){if($a[$k] -ne $b[$k]){return $false}};return $true}
function Run([string]$script,[string[]]$extra,[string]$name){
 $previous=$ErrorActionPreference;$ErrorActionPreference='Continue'
 try{& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script @extra *> (Join-Path $ResultDir ($name+'.log'));return $LASTEXITCODE}
 finally{$ErrorActionPreference=$previous}
}
$installer=Join-Path $root 'installer/Install.ps1';$package=Join-Path $ResultDir 'package';$app=Join-Path $ResultDir 'app'
foreach($name in @('QLCAD.exe','CadPlugin/DwgTranslator.Cad.dll','CadPlugin/DwgTranslator.Core.dll','CadPlugin/cad-platform.txt')){Put (Join-Path $package $name) 'synthetic never execute'}
foreach($name in @('settings.json','glossaries/mechanical_zh_en.json','assets/default-glossaries/mechanical_zh_en.json')){Put (Join-Path $package $name) '{}'}
$installArgs=@('-SourceDir',$package,'-TargetDir',$app,'-NoLaunch','-NoPrompt','-NoShortcuts')
Check ((Run $installer $installArgs 'initial') -eq 0) 'synthetic baseline installed'
$exe=Join-Path $app 'QLCAD.exe'
Copy-Item -LiteralPath $NodeExe -Destination $exe -Force
$ready=Join-Path $ResultDir 'ready.txt';$childScript=Join-Path $ResultDir 'wait.cjs'
Put $childScript "require('fs').writeFileSync(process.argv[2], 'ready'); setTimeout(() => {}, 15000);"
$p=Start-Process -FilePath $exe -ArgumentList @(('"'+$childScript+'"'),('"'+$ready+'"')) -WindowStyle Hidden -PassThru
try{
 $deadline=(Get-Date).AddSeconds(5)
 while(-not(Test-Path -LiteralPath $ready) -and -not $p.HasExited -and (Get-Date) -lt $deadline){Start-Sleep -Milliseconds 100}
 Check ((Test-Path -LiteralPath $ready) -and -not $p.HasExited) 'benign test process confirmed live'
 Check ((Get-Process -Id $p.Id).Path -eq $exe) 'live executable path is exact fixture target'
 $before=Snapshot $app
 Check ((Run $installer $installArgs 'running-install') -ne 0) 'install rejects same installed running process'
 Check ((Get-Content (Join-Path $ResultDir 'running-install.log') -Raw) -match 'Close the installed application') 'install rejection is process guard rather than copy failure'
 Check ((Run (Join-Path $app 'Uninstall.ps1') @('-ConfirmRemoval') 'running-uninstall') -ne 0) 'uninstall rejects same installed running process'
 Check ((Get-Content (Join-Path $ResultDir 'running-uninstall.log') -Raw) -match 'Close this installed application') 'uninstall rejection is process guard'
 Check (Equal $before (Snapshot $app)) 'running-instance rejection leaves installation unchanged'
 Check (-not $p.HasExited) 'neither installer nor uninstaller stopped the child'
}finally{
 if(-not $p.HasExited -and -not $p.WaitForExit(20000)){throw "Test process still live: PID $($p.Id); inspect rather than restarting"}
 $p.Dispose()
}
Check ((Run $installer $installArgs 'after-exit') -eq 0) 'install succeeds after benign process naturally exits'
# All junction targets stay inside this fresh task fixture, never at user data or source.
$alias=Join-Path $ResultDir 'app-junction'
New-Item -ItemType Junction -Path $alias -Target $app|Out-Null
Check ([bool]((Get-Item -LiteralPath $alias -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) 'fixture junction really is a reparse point'
$before=Snapshot $app
Check ((Run $installer @('-SourceDir',$package,'-TargetDir',$alias,'-NoLaunch','-NoPrompt','-NoShortcuts') 'junction-install') -ne 0) 'install rejects linked target'
Check ((Get-Content (Join-Path $ResultDir 'junction-install.log') -Raw) -match 'Linked install path') 'linked-target install rejection comes from path guard'
Check ((Run (Join-Path $alias 'Uninstall.ps1') @('-ConfirmRemoval') 'junction-uninstall') -ne 0) 'uninstall rejects linked script directory'
Check ((Get-Content (Join-Path $ResultDir 'junction-uninstall.log') -Raw) -match 'Linked uninstall path') 'linked-script uninstall rejection comes from path guard'
Check (Equal $before (Snapshot $app)) 'linked target rejection leaves physical installation unchanged'
$sourceAlias=Join-Path $ResultDir 'package-junction'
New-Item -ItemType Junction -Path $sourceAlias -Target $package|Out-Null
Check ((Run $installer @('-SourceDir',$sourceAlias,'-TargetDir',$app,'-NoLaunch','-NoPrompt','-NoShortcuts') 'junction-source') -ne 0) 'install rejects linked package root'
Check (Equal $before (Snapshot $app)) 'linked package rejection leaves installation unchanged'
@{Passed=$checks.Count;Checks=@($checks);InstallerSha256=Hash $installer;UninstallerSha256=Hash (Join-Path $root 'installer/Uninstall.ps1');Junctions=@(@{Path=$alias;Target=$app},@{Path=$sourceAlias;Target=$package});Scope='actual running process path guards and root junction guards; not concurrent path swaps or arbitrary crash recovery'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $ResultDir 'results.json') -Encoding UTF8
Write-Host "INSTALLER_PATH_SAFETY=PASS ($($checks.Count) checks); $ResultDir"
