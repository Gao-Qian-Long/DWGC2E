# Verify every installer entry point references the canonical APP branding.
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$iss=[IO.File]::ReadAllText((Join-Path $root 'installer/DwgTranslator.iss'))
$match=[regex]::Match($iss,'(?m)^SetupIconFile=(.+)\r?$')
if(-not $match.Success){throw 'Installer executable lacks canonical icon'}
$icon=[IO.Path]::GetFullPath((Join-Path (Join-Path $root 'installer') $match.Groups[1].Value.Trim()))
$canonical=Join-Path $root 'assets/icons/icon.ico'
if($icon -ne $canonical -or -not (Test-Path -LiteralPath $icon -PathType Leaf)){throw 'Installer must use canonical icon.ico'}
if($iss -notmatch '(?m)^UninstallDisplayIcon=\{app\}\\\{#MyAppExeName\}\r?$'){throw 'Installed program listing icon differs'}
$uninstall=($iss -split '\r?\n' | Where-Object {$_ -match '^Name:.*Filename: "\{uninstallexe\}"'})
if(@($uninstall).Count -ne 1 -or $uninstall -notmatch 'IconFilename: "\{app\}\\\{#MyAppExeName\}"'){throw 'Uninstall shortcut must use APP icon'}
$project=[IO.File]::ReadAllText((Join-Path $root 'src/DwgTranslator.App/DwgTranslator.App.csproj'))
if($project -notmatch '<ApplicationIcon>\.\.\\\.\.\\assets\\icons\\icon.ico</ApplicationIcon>'){throw 'APP executable icon differs'}
Write-Output 'INSTALLER_BRAND_TESTS=PASS; setup, uninstall listing, uninstall shortcut and APP reference canonical branding. Static configuration check only; packaged PE verification is separate.'
