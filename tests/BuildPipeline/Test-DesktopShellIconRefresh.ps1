param([string]$ExecutablePath)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$script=Join-Path $root 'tools/Refresh-DesktopShellIcon.ps1'
if(-not $ExecutablePath){$ExecutablePath=Join-Path $root 'release/QLCAD.exe'}
$exe=(Get-Item -LiteralPath $ExecutablePath).FullName
$before=(Get-FileHash -LiteralPath $exe).Hash
foreach($invalid in @((Split-Path -Parent $exe),$script,(Join-Path (Split-Path -Parent $exe) 'nonexistent-icon-test.exe'))) {
 $rejected=$false
 try { & $script -ExecutablePath $invalid | Out-Null } catch { $rejected=$true }
 if(-not $rejected){throw "Unsafe path accepted: $invalid"}
}
# Repeated invocation in one PowerShell process must not redefine the native type.
1..2 | ForEach-Object { $result=& $script -ExecutablePath $exe; if($result -notlike 'SHELL_ICON_REFRESH=NOTIFIED;*'){throw 'Missing notification result'} }
if((Get-FileHash -LiteralPath $exe).Hash -ne $before){throw 'Icon refresh changed executable'}
$tokens=$null;$errors=$null
[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'tools/Publish-Desktop.ps1'),[ref]$tokens,[ref]$errors)|Out-Null
if($errors.Count){throw 'Publisher syntax invalid'}
Write-Output 'SHELL_ICON_REFRESH_TESTS=PASS; 3 invalid targets rejected; repeated notification; EXE unchanged; publisher syntax valid.'
