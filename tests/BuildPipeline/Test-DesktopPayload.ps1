# Purpose: reject accidental secrets, extra DLLs and developer evidence in the public runtime.
param([Parameter(Mandatory=$true)][string]$PublishDir,[Parameter(Mandatory=$true)][string]$ResultDir)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\')
$fixture=Join-Path $ResultDir 'workspace';$candidate=Join-Path $fixture 'artifacts/publish-20260917-000000'
if(Test-Path -LiteralPath $fixture){throw 'Fixture already exists'}
New-Item -ItemType Directory -Path $candidate -Force|Out-Null
Get-ChildItem -LiteralPath $PublishDir | Copy-Item -Destination $candidate -Recurse
foreach($relative in @('settings.json.example','assets/glossaries/mechanical_zh_en.json','assets/prompts/deepl_context.txt')){$target=Join-Path $fixture $relative;New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force|Out-Null;Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $target}
$tool=Join-Path $root 'tools/Get-DesktopPayload.ps1'
$payload=@(& $tool -PublishDir $candidate -WorkspaceRoot $fixture)
if($payload.Count -lt 9 -or @($payload | Where-Object {-not $_.sha256}).Count){throw 'Invalid runtime receipt'}
$checks=@('clean runtime accepted')
foreach($relative in @('.env','private-account.json','CadPlugin/extra.dll','debug.log','AGENTS.md','glossaries/personal.json')){
 $p=Join-Path $candidate $relative;[IO.File]::WriteAllText($p,'synthetic private data')
 try{
  $message='';try{& $tool -PublishDir $candidate -WorkspaceRoot $fixture|Out-Null}catch{$message=$_.Exception.Message}
  if($message -notmatch 'Unreviewed (file|resource)|Invalid managed assemblies|Unexpected.*DLL'){throw "Did not reject extra $relative : $message"}
  $checks+='reject '+$relative
 }finally{Remove-Item -LiteralPath $p}
}
@{passed=$true;checks=$checks;scope='copied candidate; no installed data touched'} | ConvertTo-Json | Set-Content (Join-Path $ResultDir 'results.json') -Encoding UTF8
Write-Host "DESKTOP_PAYLOAD_TESTS=PASS ($($checks.Count))"
