# Purpose: exercise data preservation and delivery fail-closed contracts without replacing release.
param([string]$ResultDir)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\')
if(-not $ResultDir){$ResultDir=Join-Path $root ('artifacts/delivery-test-'+[guid]::NewGuid().ToString('N'))}
$fixture=Join-Path $ResultDir 'workspace'
$old=Join-Path $fixture 'release';$next=Join-Path $fixture 'artifacts/release-next-20260917-000000'
if(Test-Path -LiteralPath $fixture){throw 'Fixture exists'}
New-Item -ItemType Directory -Path $old,$next -Force | Out-Null
function Put($p,$text){New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($p)) -Force|Out-Null;[IO.File]::WriteAllText($p,$text)}
$checks=New-Object 'System.Collections.Generic.List[string]'
function Check($v,$label){if(-not $v){throw $label};$checks.Add($label)}
foreach($file in Get-ChildItem (Join-Path $root 'tools') -Filter '*.ps1' | Where-Object { $_.Name -in @('Publish-Desktop.ps1','Get-DesktopPayload.ps1','Get-DesktopSourceSnapshot.ps1','New-DesktopInstaller.ps1','Copy-DesktopUserData.ps1','Clean-DesktopArtifacts.ps1') }){$tokens=$null;$errors=$null;[Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)|Out-Null;Check ($errors.Count -eq 0) ('syntax '+$file.Name)}
Put (Join-Path $old 'DwgTranslator.exe') 'old';Put (Join-Path $next 'DwgTranslator.exe') 'new'
Put (Join-Path $old 'CadPlugin/cad-files.txt') 'owned.dll'
Put (Join-Path $old 'CadPlugin/owned.dll') 'old plugin';Put (Join-Path $next 'CadPlugin/owned.dll') 'new plugin'
$personal=@('settings.json','prompts/deepl_context.txt','glossaries/personal.json','assets/personal.txt','CadPlugin/notes.txt','unknown.db','exports/result.txt')
foreach($p in $personal){Put (Join-Path $old $p) ('personal '+$p)}
Put (Join-Path $next 'settings.json') 'default'
& (Join-Path $root 'tools/Copy-DesktopUserData.ps1') -OldRelease $old -NewRelease $next -WorkspaceRoot $fixture
foreach($p in $personal){Check ((Get-FileHash (Join-Path $old $p)).Hash -eq (Get-FileHash (Join-Path $next $p)).Hash) ('preserved '+$p)}
Check ((Get-Content (Join-Path $next 'DwgTranslator.exe') -Raw) -eq 'new') 'new executable not overwritten'
Check ((Get-Content (Join-Path $next 'CadPlugin/owned.dll') -Raw) -eq 'new plugin') 'new owned plugin not overwritten'
Put (Join-Path $old 'collision.txt') 'personal';Put (Join-Path $next 'collision.txt') 'runtime'
$rejected=$false;try{& (Join-Path $root 'tools/Copy-DesktopUserData.ps1') -OldRelease $old -NewRelease $next -WorkspaceRoot $fixture}catch{$rejected=$true}
Check $rejected 'unknown runtime collision rejected'
Put (Join-Path $old 'CadPlugin/cad-files.txt') '..\escape.dll'
$rejected=$false;try{& (Join-Path $root 'tools/Copy-DesktopUserData.ps1') -OldRelease $old -NewRelease $next -WorkspaceRoot $fixture}catch{$rejected=$true}
Check $rejected 'unsafe old manifest rejected'
$publisher=Get-Content (Join-Path $root 'tools/Publish-Desktop.ps1') -Raw
Check ($publisher.IndexOf('New-DesktopInstaller.ps1') -lt $publisher.IndexOf('Move-Item -LiteralPath (Assert-WorkspacePath $release)')) 'installer validation precedes release replacement'
Check ($publisher.Contains('Build inputs changed during delivery')) 'source-mutation gate wired'
Check ($publisher.Contains('Release was started during validation')) 'late process guard wired'
Check ((Get-Content (Join-Path $root 'tools/New-DesktopInstaller.ps1') -Raw).Contains('& powershell.exe @arguments | Out-Host')) 'installer logs do not corrupt receipt'
@{passed=$true;checks=@($checks);scope='synthetic data preservation, parser checks and structural gate wiring; full publisher tested separately'} | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $ResultDir 'results.json') -Encoding UTF8
Write-Host "DESKTOP_DELIVERY_TESTS=PASS ($($checks.Count))"
