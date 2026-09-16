# Purpose: run the actual portable uninstaller against synthetic StartMenu links in child-only APPDATA.
# Input: optional fresh -ResultDir beneath workspace artifacts. Output: retained fixtures, logs, results.json.
# No desktop shortcut records, real user-folder changes, APP execution, or recursive deletion.
param([string]$ResultDir)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\')
if(-not $ResultDir){$ResultDir=Join-Path $root ('artifacts/installer-safety-20260916-resumed/shortcut-uninstall-'+[guid]::NewGuid().ToString('N'))}
$ResultDir=[IO.Path]::GetFullPath($ResultDir).TrimEnd('\')
if(-not $ResultDir.StartsWith($root+'\artifacts\',[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture outside artifacts'}
$a=$ResultDir
while($a){if((Test-Path -LiteralPath $a) -and ((Get-Item -LiteralPath $a -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked fixture path'};$a=[IO.Path]::GetDirectoryName($a)}
if(Test-Path -LiteralPath $ResultDir){throw 'Fixture already exists'}
New-Item -ItemType Directory -Path $ResultDir|Out-Null
$checks=New-Object 'System.Collections.Generic.List[string]'
function Check($v,[string]$label){if(-not $v){throw $label};$checks.Add($label);Write-Host "PASS $label"}
function Hash([string]$p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}
function Snapshot([string]$dir){$h=@{};Get-ChildItem -LiteralPath $dir -File -Recurse|ForEach-Object{$h[$_.FullName]=Hash $_.FullName};return $h}
function Equal($a,$b){if($a.Count -ne $b.Count){return $false};foreach($k in $a.Keys){if($a[$k] -ne $b[$k]){return $false}};return $true}
function Make-Link([string]$path,[string]$target,[string]$arguments,[string]$description){
 $shell=$null;$link=$null
 try{$shell=New-Object -ComObject WScript.Shell;$link=$shell.CreateShortcut($path);$link.TargetPath=$target;$link.Arguments=$arguments;$link.Description=$description;$link.Save()}
 finally{if($link){[Runtime.InteropServices.Marshal]::ReleaseComObject($link)|Out-Null};if($shell){[Runtime.InteropServices.Marshal]::ReleaseComObject($shell)|Out-Null}}
}
function Run-Uninstall([string]$script,[string]$appdata,[string]$log,[bool]$preview){
 # Child-only environment override: the parent and Windows known-folder configuration stay unchanged.
 $psi=New-Object Diagnostics.ProcessStartInfo
 $psi.FileName=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
 $psi.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$script+'" -ConfirmRemoval'
 if($preview){$psi.Arguments+=' -Preview'}
 $psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
 $psi.EnvironmentVariables['APPDATA']=$appdata
 $p=New-Object Diagnostics.Process;$p.StartInfo=$psi
 try{
  [void]$p.Start();$stdout=$p.StandardOutput.ReadToEndAsync();$stderr=$p.StandardError.ReadToEndAsync()
  if(-not $p.WaitForExit(30000)){throw "Child still active PID=$($p.Id); inspect before retry"}
  [IO.File]::WriteAllText($log,$stdout.Result+"`r`n"+$stderr.Result)
  return $p.ExitCode
 }finally{$p.Dispose()}
}
$realAppdata=$env:APPDATA
foreach($case in @('owned','modified','other-target','custom-arguments','unregistered','duplicate','locked','preview')){
 $dir=Join-Path $ResultDir $case;$app=Join-Path $dir 'app';$fakeData=Join-Path $dir 'user-data'
 $menu=Join-Path $fakeData 'Microsoft/Windows/Start Menu/Programs'
 New-Item -ItemType Directory -Path $app,$menu|Out-Null
 $exe=Join-Path $app 'DwgTranslator.exe';[IO.File]::WriteAllText($exe,'fixture never execute')
 $settings=Join-Path $app 'settings.json';[IO.File]::WriteAllText($settings,'{"personal":"keep"}')
 $script=Join-Path $app 'Uninstall.ps1';Copy-Item -LiteralPath (Join-Path $root 'installer/Uninstall.ps1') -Destination $script
 $link=Join-Path $menu 'DWG Translator.lnk'
 $target=$exe;if($case -eq 'other-target'){$target=Join-Path $dir 'Other.exe'}
 $arguments='';if($case -eq 'custom-arguments'){$arguments='--custom'}
 Make-Link $link $target $arguments 'original'
 $record=@{Kind='StartMenu';Sha256=Hash $link}
 $records=@($record)
 if($case -eq 'unregistered'){$records=@()}
 if($case -eq 'duplicate'){$records=@($record,$record)}
 if($case -eq 'modified'){Make-Link $link $exe '' 'user modified'}
 @{Schema=1;Product='DWGC2E';Root=$app;Files=@(@{Path='DwgTranslator.exe';Sha256=Hash $exe});Shortcuts=$records}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $app 'installation-manifest.json') -Encoding UTF8
 $before=Snapshot $dir;$linkHash=Hash $link;$settingsHash=Hash $settings
 $lock=$null
 try{
  if($case -eq 'locked'){$lock=[IO.File]::Open($link,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)}
  $exit=Run-Uninstall $script $fakeData (Join-Path $ResultDir ($case+'.log')) ($case -eq 'preview')
 }finally{if($lock){$lock.Dispose()}}
 if($case -in @('duplicate','locked')){
  Check ($exit -ne 0) "$case rejected"
  Check (Equal $before (Snapshot $dir)) "$case leaves all fixture files unchanged"
 }elseif($case -eq 'preview'){
  Check ($exit -eq 0) 'preview succeeds'
  Check (Equal $before (Snapshot $dir)) 'preview preserves program and shortcut'
 }else{
  Check ($exit -eq 0) "$case uninstall succeeds"
  Check (-not(Test-Path -LiteralPath $exe)) "$case removes unchanged owned program"
  if($case -eq 'owned'){Check (-not(Test-Path -LiteralPath $link)) 'unchanged registered matching shortcut removed'}
  else{Check ((Hash $link) -eq $linkHash) "$case shortcut preserved byte for byte"}
 }
 Check ((Hash $settings) -eq $settingsHash) "$case preserves user settings"
}
Check ($env:APPDATA -eq $realAppdata) 'parent APPDATA unchanged'
@{Passed=$checks.Count;Checks=@($checks);UninstallerSha256=Hash (Join-Path $root 'installer/Uninstall.ps1');Scope='actual uninstaller process and COM StartMenu links; child APPDATA isolation; Desktop path mapping and junctions not covered'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $ResultDir 'results.json') -Encoding UTF8
Write-Host "SHORTCUT_UNINSTALL=PASS ($($checks.Count) checks); $ResultDir"