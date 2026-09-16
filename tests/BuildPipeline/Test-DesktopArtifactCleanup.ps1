$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture=Join-Path $repo ('artifacts/cleanup-test-'+[guid]::NewGuid().ToString('N'))
$art=Join-Path $fixture 'artifacts'
New-Item -ItemType Directory -Path $art -Force|Out-Null
function Check($condition,$message){if(!$condition){throw $message}}
try {
 foreach($stamp in @('20260914-100000','20260915-100000')){
  $dir=Join-Path $art "publish-$stamp";New-Item -ItemType Directory -Path $dir|Out-Null
  Set-Content -LiteralPath (Join-Path $dir 'DwgTranslator.exe') 'fixture'
  @{sha256=(Get-FileHash -LiteralPath (Join-Path $dir 'DwgTranslator.exe')).Hash;version="2.1.1+ui.$stamp.test"}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $dir 'build-info.json')
  Set-Content -LiteralPath (Join-Path $art "DwgTranslator-win-x64-GstarCAD-$stamp.zip") 'fixture zip'
 }
 $release=Join-Path $fixture 'release';New-Item -ItemType Directory -Path $release|Out-Null
 Copy-Item -LiteralPath (Join-Path $art 'publish-20260914-100000/build-info.json') -Destination $release
 foreach($name in @('release-backup-20260913-100000','release-backup-20260914-100000','publish-20260913-100000')){New-Item -ItemType Directory -Path (Join-Path $art $name)|Out-Null}
 Set-Content -LiteralPath (Join-Path $art 'publish-20260913-100000/settings.json') 'unique user data'
 Set-Content -LiteralPath (Join-Path $art 'payment-backup.sql') 'do not remove'
 & (Join-Path $repo 'tools/Clean-DesktopArtifacts.ps1') -WorkspaceRoot $fixture -WhatIf|Out-Null
 Check (Test-Path -LiteralPath (Join-Path $art 'publish-20260914-100000')) 'WhatIf deleted files'
 $result=& (Join-Path $repo 'tools/Clean-DesktopArtifacts.ps1') -WorkspaceRoot $fixture
 Check ($result.Removed -eq 2) 'Expected only obsolete directory and oldest backup deletion'
 foreach($name in @('publish-20260915-100000','publish-20260913-100000','release-backup-20260914-100000','DwgTranslator-win-x64-GstarCAD-20260914-100000.zip','DwgTranslator-win-x64-GstarCAD-20260915-100000.zip','payment-backup.sql')){Check (Test-Path -LiteralPath (Join-Path $art $name)) "Protected artifact missing: $name"}
 Check ((& (Join-Path $repo 'tools/Clean-DesktopArtifacts.ps1') -WorkspaceRoot $fixture).Removed -eq 0) 'Cleanup not idempotent'
 Write-Host 'CLEANUP_TESTS=PASS (preview, current release, latest candidate, rollback, unique data, unrelated files, repeat run)'
}finally{
 $full=[IO.Path]::GetFullPath($fixture)
 if([IO.Path]::GetDirectoryName($full) -ne (Join-Path $repo 'artifacts') -or [IO.Path]::GetFileName($full) -notmatch '^cleanup-test-[a-f0-9]{32}$'){throw 'Unsafe fixture cleanup'}
 Remove-Item -LiteralPath $full -Recurse -Force
}
