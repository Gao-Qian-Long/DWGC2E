# Purpose: exercise the production Desktop shortcut branch without changing existing user shortcuts.
# Requires explicit -AllowDesktopFixture and an absent Desktop/QLCAD.lnk.
# Creates only its own link using no-overwrite copy, never launches the synthetic executable.
# Cleanup removes only the exact newly created link with an unchanged SHA256; no recursive deletion.
param([switch]$AllowDesktopFixture,[string]$ResultDir)
$ErrorActionPreference='Stop'
if(-not $AllowDesktopFixture){throw 'Explicit -AllowDesktopFixture is required; this test briefly uses the real Desktop known folder.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\')
if(-not $ResultDir){$ResultDir=Join-Path $root ('artifacts/installer-safety-20260916-resumed/desktop-shortcuts-'+[guid]::NewGuid().ToString('N'))}
$ResultDir=[IO.Path]::GetFullPath($ResultDir).TrimEnd('\')
if(-not $ResultDir.StartsWith($root+'\artifacts\',[StringComparison]::OrdinalIgnoreCase)){throw 'Results must stay under workspace artifacts'}
function No-Link([string]$p){while($p){if((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked test path refused'};$p=[IO.Path]::GetDirectoryName($p)}}
No-Link $ResultDir
if(Test-Path -LiteralPath $ResultDir){throw 'Use a fresh result directory'}
$desktop=[Environment]::GetFolderPath('Desktop')
if([string]::IsNullOrWhiteSpace($desktop) -or -not(Test-Path -LiteralPath $desktop -PathType Container)){throw 'Desktop is unavailable'}
$link=Join-Path $desktop 'QLCAD.lnk';No-Link $link
if(Test-Path -LiteralPath $link){throw 'Existing desktop shortcut preserved; this test must not run here'}
New-Item -ItemType Directory -Path $ResultDir|Out-Null
$checks=New-Object 'System.Collections.Generic.List[string]'
function Check($v,[string]$label){if(-not $v){throw $label};$checks.Add($label);Write-Host "PASS $label"}
function Hash([string]$p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}
function Make-Link([string]$p,[string]$target,[string]$arguments){
 $shell=$null;$lnk=$null
 try{$shell=New-Object -ComObject WScript.Shell;$lnk=$shell.CreateShortcut($p);$lnk.TargetPath=$target;$lnk.Arguments=$arguments;$lnk.Description='QLCAD temporary regression fixture - do not open';$lnk.Save()}
 finally{if($lnk){[Runtime.InteropServices.Marshal]::ReleaseComObject($lnk)|Out-Null};if($shell){[Runtime.InteropServices.Marshal]::ReleaseComObject($shell)|Out-Null}}
}
function Snapshot([string]$dir){$h=@{};Get-ChildItem -LiteralPath $dir -Recurse -File|ForEach-Object{$h[$_.FullName]=Hash $_.FullName};return $h}
function Equal($a,$b){if($a.Count -ne $b.Count){return $false};foreach($k in $a.Keys){if($a[$k] -ne $b[$k]){return $false}};return $true}
foreach($case in @('owned','modified','other-target','custom-arguments','unregistered','duplicate','locked','preview')){
 $dir=Join-Path $ResultDir $case;$app=Join-Path $dir 'app';New-Item -ItemType Directory -Path $app|Out-Null
 $exe=Join-Path $app 'QLCAD.exe';[IO.File]::WriteAllText($exe,'synthetic never execute')
 $settings=Join-Path $app 'settings.json';[IO.File]::WriteAllText($settings,'{"personal":"preserve"}')
 $script=Join-Path $app 'Uninstall.ps1';Copy-Item -LiteralPath (Join-Path $root 'installer/Uninstall.ps1') -Destination $script
 $stagedLink=Join-Path $dir 'fixture.lnk';$target=$exe;if($case -eq 'other-target'){$target=Join-Path $dir 'other.exe'}
 $arguments='';if($case -eq 'custom-arguments'){$arguments='--custom'}
 Make-Link $stagedLink $target $arguments
 $expectedHash=Hash $stagedLink;$registeredHash=$expectedHash;if($case -eq 'modified'){$registeredHash='0'*64}
 $record=@{Kind='Desktop';Sha256=$registeredHash};$records=@($record)
 if($case -eq 'unregistered'){$records=@()};if($case -eq 'duplicate'){$records=@($record,$record)}
 @{Schema=1;Product='QLCAD';Root=$app;Files=@(@{Path='QLCAD.exe';Sha256=Hash $exe});Shortcuts=$records}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $app 'installation-manifest.json') -Encoding UTF8
 $before=Snapshot $app;$settingsHash=Hash $settings;$created=$false;$lock=$null;$childLive=$false
 try{
  No-Link $link
  [IO.File]::Copy($stagedLink,$link,$false);$created=$true
  if($case -eq 'locked'){$lock=[IO.File]::Open($link,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)}
  $psi=New-Object Diagnostics.ProcessStartInfo
  $psi.FileName=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
  $psi.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$script+'" -ConfirmRemoval'
  if($case -eq 'preview'){$psi.Arguments+=' -Preview'}
  $psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
  $process=New-Object Diagnostics.Process;$process.StartInfo=$psi
  try{
   [void]$process.Start();$childLive=$true;$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
   if(-not $process.WaitForExit(30000)){throw "Uninstaller still running PID=$($process.Id); fixture retained, do not restart"}
   $childLive=$false;$exit=$process.ExitCode
   [IO.File]::WriteAllText((Join-Path $ResultDir ($case+'.log')),$stdout.Result+"`r`n"+$stderr.Result)
  }finally{$process.Dispose()}
  if($case -in @('duplicate','locked')){
   Check ($exit -ne 0) "$case rejected"
   Check (Equal $before (Snapshot $app)) "$case preserves all installation files"
  }elseif($case -eq 'preview'){
   Check ($exit -eq 0 -and (Equal $before (Snapshot $app))) 'preview preserves all installation files'
  }else{
   Check ($exit -eq 0 -and -not(Test-Path -LiteralPath $exe)) "$case removes owned synthetic executable"
  }
  if($case -eq 'owned'){Check (-not(Test-Path -LiteralPath $link)) 'owned Desktop shortcut removed by production uninstaller'}
  else{Check ((Hash $link) -eq $expectedHash) "$case Desktop shortcut preserved byte for byte"}
  Check ((Hash $settings) -eq $settingsHash) "$case preserves settings"
 }finally{
  if($lock){$lock.Dispose()}
  if($created -and -not $childLive -and (Test-Path -LiteralPath $link)){
   No-Link $link
   if((Hash $link) -ne $expectedHash){throw 'Desktop shortcut changed outside this test; retained for review'}
   Remove-Item -LiteralPath $link
  }
 }
}
Check (-not(Test-Path -LiteralPath $link)) 'Desktop returned to its initial absent-shortcut state'
@{Passed=$checks.Count;Checks=@($checks);UninstallerSha256=Hash (Join-Path $root 'installer/Uninstall.ps1');Desktop=$desktop;Scope='actual Desktop known folder and production uninstaller process; eight synthetic link scenarios; no pre-existing shortcut touched; no APP execution; no registry override'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $ResultDir 'results.json') -Encoding UTF8
Write-Host "DESKTOP_SHORTCUT_UNINSTALL=PASS ($($checks.Count) checks); $ResultDir"
