# Purpose: build and test one Setup.exe from a clean candidate. Never updates current or release.
# Inputs: PublishDir, new OutputDir under artifacts, optional PreviousPublishDir for upgrade acceptance.
# Output: public/ (Setup + guide + SHA256), private payload/acceptance receipt. No upload.
param([Parameter(Mandatory=$true)][string]$PublishDir,[Parameter(Mandatory=$true)][string]$OutputDir,
 [string]$PreviousPublishDir,[string]$Compiler,[System.IO.FileStream]$PublishLock)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$out=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir).TrimEnd('\')
if(-not $out.StartsWith($root+'\artifacts\',[StringComparison]::OrdinalIgnoreCase)){throw 'Installer output must be a new directory under artifacts.'}
if(Test-Path -LiteralPath $out){throw 'Installer output already exists; refusing to overwrite evidence.'}
for($a=$out;$a;$a=[IO.Path]::GetDirectoryName($a)){if((Test-Path -LiteralPath $a) -and ((Get-Item -LiteralPath $a -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked installer output rejected.'}}
$ownedLock=$null
try {
 if($PublishLock){if(-not $PublishLock.CanWrite -or $PublishLock.Name -ne (Join-Path $root 'artifacts/.desktop-publish.lock')){throw 'Invalid publish lock handle'}}
 else{$ownedLock=& (Join-Path $PSScriptRoot 'Enter-DesktopPublishLock.ps1') -WorkspaceRoot $root}
 $payload=@(& (Join-Path $PSScriptRoot 'Get-DesktopPayload.ps1') -PublishDir $PublishDir)
 $info=Get-Content -LiteralPath (Join-Path $PublishDir 'build-info.json') -Raw | ConvertFrom-Json
 $platform=(Get-Content -LiteralPath (Join-Path $PublishDir 'CadPlugin/cad-platform.txt') -Raw).Trim()
 if($info.version -notmatch '^(\d+\.\d+\.\d+)\+ui\.(\d{8}-\d{6})\.[a-zA-Z0-9]+$'){throw 'Invalid build ID'}
 $filename='DWGC2E-'+$Matches[1]+'-win-x64-'+$platform+'-'+$Matches[2]+'-Setup.exe'
 New-Item -ItemType Directory -Path $out | Out-Null
 $resultDir=Join-Path $out 'acceptance'
 $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $root 'tests/BuildPipeline/Test-InnoInstaller.ps1'),'-PublishDir',$PublishDir,'-ResultDir',$resultDir)
 if($PreviousPublishDir){$arguments+=@('-PreviousPublishDir',$PreviousPublishDir)}
 if($Compiler){$arguments+=@('-Compiler',$Compiler)}
 & powershell.exe @arguments | Out-Host
 if($LASTEXITCODE -ne 0){throw 'Inno install/upgrade/repair/uninstall acceptance failed; no public output generated.'}
 $result=Get-Content -LiteralPath (Join-Path $resultDir 'results.json') -Raw | ConvertFrom-Json
 if($result.Version -ne $info.version -or $result.Passed -lt 40 -or $result.ProductionInstaller -eq $result.TestInstaller){throw 'Invalid installer acceptance evidence'}
 $production=[IO.Path]::GetFullPath($result.ProductionInstaller)
 if(-not $production.StartsWith($out+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($production)) -ne 'production-build'){throw 'Isolated/unrelated installer cannot be distributed'}
 $after=@(& (Join-Path $PSScriptRoot 'Get-DesktopPayload.ps1') -PublishDir $PublishDir)
 if(($payload|ConvertTo-Json -Compress) -cne ($after|ConvertTo-Json -Compress)){throw 'Candidate changed during installer acceptance'}
 $public=Join-Path $out 'public'; New-Item -ItemType Directory -Path $public | Out-Null
 $setup=Join-Path $public $filename; Copy-Item -LiteralPath $production -Destination $setup
 Copy-Item -LiteralPath (Join-Path $root 'installer/使用说明.txt') -Destination (Join-Path $public '开始使用.txt')
 $setupHash=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
 if($setupHash -ne (Get-FileHash -LiteralPath $production -Algorithm SHA256).Hash){throw 'Installer copy hash mismatch'}
 ($setupHash+'  '+$filename) | Set-Content -LiteralPath (Join-Path $public 'SHA256SUMS.txt') -Encoding ASCII
 $receipt=[ordered]@{schemaVersion=1;status='verified-local-installer';version=$info.version;cadPlatform=$platform;candidate=(Resolve-Path -LiteralPath $PublishDir).Path;appSha256=$info.sha256;installerPath=$setup;installerSha256=$setupHash;installerBytes=(Get-Item -LiteralPath $setup).Length;publicDirectory=$public;acceptanceReport=(Join-Path $resultDir 'results.json');upgrade=$result.Upgrade;payload=$payload;publicUploadPerformed=$false;limitations=@('Isolated installer acceptance is not a clean Windows VM or all-CAD-version certification.','Installer is unsigned unless separately signed and revalidated.')}
 $receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out 'installer-manifest.json') -Encoding UTF8
 Write-Host "SETUP_VERIFIED=$setup"
 [pscustomobject]$receipt
} finally {if($ownedLock){$ownedLock.Dispose()}}
