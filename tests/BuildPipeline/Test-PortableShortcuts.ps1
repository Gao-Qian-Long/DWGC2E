# Purpose: exercise the production shortcut function through Windows COM in an isolated directory.
# Usage: powershell -File tests/BuildPipeline/Test-PortableShortcuts.ps1 [-ResultDir <artifacts child>]
# Output: results.json and retained synthetic links. No real desktop/start-menu access or APP launch.
param([string]$ResultDir)
$ErrorActionPreference='Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\')
if(-not $ResultDir){$ResultDir=Join-Path $root 'artifacts/installer-safety-20260916-resumed/portable-shortcuts'}
$ResultDir=[IO.Path]::GetFullPath($ResultDir).TrimEnd('\')
if(-not $ResultDir.StartsWith($root+'\artifacts\',[StringComparison]::OrdinalIgnoreCase)){throw 'Results must stay under artifacts'}
$a=$ResultDir
while($a){if((Test-Path -LiteralPath $a) -and ((Get-Item -LiteralPath $a -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked results rejected'};$a=[IO.Path]::GetDirectoryName($a)}
if(Test-Path -LiteralPath $ResultDir){throw 'Choose a new results directory; never overwrite evidence'}
New-Item -ItemType Directory -Path $ResultDir | Out-Null
$errors=$null;$tokens=$null
$installer=Join-Path $root 'installer/Install.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile($installer,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse error'}
$fn=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'New-Shortcut'},$true)
if(-not $fn){throw 'Production shortcut function missing'}
# Execute the exact production function, never installer top-level code or its fixed user paths.
. ([scriptblock]::Create($fn.Extent.Text))
function Write-Step([string]$text){Write-Host $text}
$checks=New-Object 'System.Collections.Generic.List[string]'
function Check($value,[string]$label){if(-not $value){throw $label};$checks.Add($label);Write-Host "PASS $label"}
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function Read-Link([string]$path){
    $shell=$null;$link=$null
    try{$shell=New-Object -ComObject WScript.Shell;$link=$shell.CreateShortcut($path);return @{Target=$link.TargetPath;Arguments=$link.Arguments;Description=$link.Description}}
    finally{if($link){[Runtime.InteropServices.Marshal]::ReleaseComObject($link)|Out-Null};if($shell){[Runtime.InteropServices.Marshal]::ReleaseComObject($shell)|Out-Null}}
}
function Edit-Link([string]$path,[string]$target,[string]$arguments,[string]$description){
    $shell=$null;$link=$null
    try{$shell=New-Object -ComObject WScript.Shell;$link=$shell.CreateShortcut($path);$link.TargetPath=$target;$link.Arguments=$arguments;$link.Description=$description;$link.Save()}
    finally{if($link){[Runtime.InteropServices.Marshal]::ReleaseComObject($link)|Out-Null};if($shell){[Runtime.InteropServices.Marshal]::ReleaseComObject($shell)|Out-Null}}
}
$ownedShortcuts=New-Object 'System.Collections.Generic.List[object]'
$exe=Join-Path $ResultDir 'QLCAD.exe'
[IO.File]::WriteAllText($exe,'Synthetic file, never execute')
$linkPath=Join-Path $ResultDir 'owned.lnk'
New-Shortcut $linkPath $exe $ResultDir 'first' 'Desktop'
Check (Test-Path -LiteralPath $linkPath -PathType Leaf) 'new shortcut created only inside fixture'
$read=Read-Link $linkPath
Check ($read.Target -eq $exe -and [string]::IsNullOrWhiteSpace($read.Arguments)) 'new shortcut targets isolated executable without arguments'
Check ($ownedShortcuts.Count -eq 1 -and $ownedShortcuts[0].Sha256 -eq (Hash $linkPath)) 'new link ownership records exact hash'
New-Shortcut $linkPath $exe $ResultDir 'updated' 'Desktop'
Check ((Read-Link $linkPath).Description -eq 'updated') 'unchanged owned shortcut updated on reinstall'
Check ($ownedShortcuts.Count -eq 1 -and $ownedShortcuts[0].Sha256 -eq (Hash $linkPath)) 'reinstall replaces ownership rather than duplicating it'
Edit-Link $linkPath $exe '' 'user-custom-description'
$before=Hash $linkPath
New-Shortcut $linkPath $exe $ResultDir 'must-not-overwrite' 'Desktop'
Check ((Hash $linkPath) -eq $before) 'user-modified same-target shortcut preserved byte for byte'
Edit-Link $linkPath $exe '--user-custom-option' 'custom arguments'
$ownedShortcuts[0].Sha256=Hash $linkPath
$before=Hash $linkPath
New-Shortcut $linkPath $exe $ResultDir 'must-not-overwrite' 'Desktop'
Check ((Hash $linkPath) -eq $before) 'custom arguments preserved even when recorded hash matches'
$other=Join-Path $ResultDir 'OtherInstallation.exe'
Edit-Link $linkPath $other '' 'different installation'
$ownedShortcuts[0].Sha256=Hash $linkPath
$before=Hash $linkPath
New-Shortcut $linkPath $exe $ResultDir 'must-not-overwrite' 'Desktop'
Check ((Hash $linkPath) -eq $before) 'different-target shortcut preserved even when recorded hash matches'
$unregistered=Join-Path $ResultDir 'unregistered.lnk'
Edit-Link $unregistered $exe '' 'unregistered same target'
$before=Hash $unregistered
New-Shortcut $unregistered $exe $ResultDir 'must-not-overwrite' 'StartMenu'
Check ((Hash $unregistered) -eq $before) 'unregistered same-target shortcut preserved'
Check ($ownedShortcuts.Count -eq 1) 'preserved unregistered shortcut never claimed as owned'
@{Passed=$checks.Count;Checks=@($checks);InstallerSha256=Hash $installer;Scope='exact production New-Shortcut function; isolated COM links; not full installer/uninstaller integration'} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ResultDir 'results.json') -Encoding UTF8
Write-Host "PORTABLE_SHORTCUTS=PASS ($($checks.Count) checks)"
