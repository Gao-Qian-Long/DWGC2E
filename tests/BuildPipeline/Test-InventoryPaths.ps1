# Purpose: test inventory output location and preservation against an isolated synthetic repository.
# Usage: powershell -NoProfile -File tests/BuildPipeline/Test-InventoryPaths.ps1 -EvidenceDir <new-directory>
param([Parameter(Mandatory=$true)][string]$EvidenceDir)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDir)
if(Test-Path -LiteralPath $out){throw 'Use a new evidence directory'}
[void][IO.Directory]::CreateDirectory($out)
$fixture=Join-Path $out 'fixture'
[void][IO.Directory]::CreateDirectory((Join-Path $fixture 'tools'))
[void][IO.Directory]::CreateDirectory((Join-Path $fixture 'selected [path]'))
Copy-Item -LiteralPath (Join-Path $root 'tools/Get-ProjectInventory.ps1') -Destination (Join-Path $fixture 'tools/Get-ProjectInventory.ps1')
[IO.File]::WriteAllText((Join-Path $fixture 'README.md'),'fixture only')
# Metadata-only classification fixtures. No SQL/JS is executed and no config contents are exported.
$cases=[ordered]@{
 'cf-worker/package.json'='BACKEND_CONFIGURATION'; 'cf-worker/package-lock.json'='BACKEND_CONFIGURATION'
 'cf-worker/wrangler.toml'='BACKEND_CONFIGURATION'; 'cf-worker/schema.sql'='BACKEND_SCHEMA_OR_UPGRADE'
 'cf-worker/upgrades/0003_payments_from_legacy.sql'='BACKEND_SCHEMA_OR_UPGRADE'
 'cf-worker/CF_BACKEND_CONTRACT.md'='DOCUMENTATION'; 'cf-worker/ops/RELEASE_20260915.md'='DOCUMENTATION'
 'cf-worker/ops/retention-preview.sql'='OPERATIONS_TOOL'
 'cf-worker/tools/schema-preflight.mjs'='DEVELOPMENT_OR_BUILD_TOOL'
 'cf-worker/tools/verify-remote-schema.mjs'='DEVELOPMENT_OR_BUILD_TOOL'
 'settings.json'='UNKNOWN'; 'cf-worker/tools/mystery.mjs'='UNKNOWN'
 'cf-worker/ops/mystery.sql'='UNKNOWN'; 'mystery-test.dll'='UNKNOWN'
 'release/user.json'='LOCAL_DELIVERY_OR_USER_DATA'; 'tests/fixture.json'='TEST'
}
foreach($relative in $cases.Keys){
 $target=Join-Path $fixture $relative
 [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
 [IO.File]::WriteAllText($target,'synthetic-content-marker-not-for-inventory')
}
# Build declaration fixtures: preserve conditional and property-based references without executing tasks.
[IO.File]::WriteAllText((Join-Path $fixture 'Directory.Build.props'),'<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><ItemGroup Condition="parent"><ProjectReference Include="$(Shared)/Core.csproj" Condition="child" /></ItemGroup><Import Project="shared.props" /></Project>')
[IO.File]::WriteAllText((Join-Path $fixture 'Build.targets'),'<Project><Target Name="NeverRun"><MSBuild Projects="other.csproj" /><Exec Command="must-not-execute" /></Target></Project>')
[IO.File]::WriteAllText((Join-Path $fixture 'App.csproj'),'<Project Sdk="Microsoft.NET.Sdk"><ProjectReference Include="Core.csproj" /></Project>')
[IO.File]::WriteAllText((Join-Path $fixture 'App.sln'),@'
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App.csproj", "{11111111-1111-1111-1111-111111111111}"
    ProjectSection(ProjectDependencies) = postProject
        {33333333-3333-3333-3333-333333333333} = {33333333-3333-3333-3333-333333333333}
    EndProjectSection
EndProject
Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Folder", "Folder", "{22222222-2222-2222-2222-222222222222}"
EndProject
'@)
[void][IO.Directory]::CreateDirectory((Join-Path $fixture 'obj'))
[IO.File]::WriteAllText((Join-Path $fixture 'obj/generated.targets'),'<Project><Import Project="ignore-generated.props" /></Project>')
# Mimic a host whose process working directory differs from PowerShell Push-Location.
$oldProcess=[Environment]::CurrentDirectory
$processDir=Join-Path $out 'process-location'
[void][IO.Directory]::CreateDirectory($processDir)
$checks=@()
try {
 [Environment]::CurrentDirectory=$processDir
 Push-Location -LiteralPath (Join-Path $fixture 'selected [path]')
 try {
  & (Join-Path $fixture 'tools/Get-ProjectInventory.ps1') -OutputDir 'report [one]'
  $expected=Join-Path $fixture 'selected [path]/report [one]'
  if(!(Test-Path -LiteralPath (Join-Path $expected 'scope.json'))){throw 'Output did not follow the PowerShell current location'}
  $checks+='relative output follows PowerShell location, including bracket names'
  if(Test-Path -LiteralPath (Join-Path $processDir 'report [one]')){throw 'Output polluted the process directory'}
  $checks+='process working directory remains untouched'
  $before=(Get-FileHash -LiteralPath (Join-Path $expected 'scope.json')).Hash
  $rejected=$false
  try { & (Join-Path $fixture 'tools/Get-ProjectInventory.ps1') -OutputDir 'report [one]' } catch { if($_.Exception.Message -notlike 'Choose a new output directory*'){throw}; $rejected=$true }
  if(!$rejected -or (Get-FileHash -LiteralPath (Join-Path $expected 'scope.json')).Hash -ne $before){throw 'Existing evidence was not protected'}
  $checks+='existing report rejected and unchanged'
  $absolute=Join-Path $fixture 'absolute [report]'
  & (Join-Path $fixture 'tools/Get-ProjectInventory.ps1') -OutputDir $absolute
  if(!(Test-Path -LiteralPath (Join-Path $absolute 'files.csv'))){throw 'Absolute output missing'}
  $checks+='absolute output still supported'
  $references=@(Import-Csv -LiteralPath (Join-Path $absolute 'project-references.csv'))
  if($references.Count -ne 6){throw 'Unexpected number of explicit build declarations'}
  $checks+='six explicit csproj/props/targets/solution references captured'
  $conditional=@($references|Where-Object { $_.Project -eq 'Directory.Build.props' -and $_.Kind -eq 'ProjectReference' })
  if($conditional.Count -ne 1 -or $conditional[0].Reference -cne '$(Shared)/Core.csproj' -or $conditional[0].Condition -ne 'child AND parent'){throw 'Properties or inherited conditions lost'}
  $checks+='XML namespace, unevaluated properties and ancestor conditions retained'
  if(@($references|Where-Object { $_.Kind -eq 'Import' -and $_.Reference -eq 'shared.props' }).Count -ne 1){throw 'Import declaration missing'}
  $checks+='props import captured without resolution'
  if(@($references|Where-Object { $_.Kind -eq 'MSBuild' -and $_.Reference -eq 'other.csproj' }).Count -ne 1){throw 'Target project dependency missing'}
  $checks+='target task project declaration captured without execution'
  if(@($references|Where-Object { $_.Kind -eq 'SolutionProject' -and $_.Reference -eq 'App.csproj' }).Count -ne 1){throw 'Solution project missing'}
  $checks+='solution project captured, virtual folder and generated import excluded'
  $dependency=@($references|Where-Object Kind -eq 'SolutionDependency')
  if($dependency.Count -ne 1 -or $dependency[0].Condition -ne 'Owner=App.csproj' -or $dependency[0].Reference -ne '{33333333-3333-3333-3333-333333333333}'){throw 'Solution build order or unresolved GUID lost'}
  $checks+='solution build-order owner resolved; unknown dependency GUID retained'
  $invalid=Join-Path $fixture 'External.props'
  [IO.File]::WriteAllText($invalid,'<!DOCTYPE Project [<!ENTITY unsafe SYSTEM "file:///not-to-be-read">]><Project>&unsafe;</Project>')
  $dtdRejected=$false
  try { & (Join-Path $fixture 'tools/Get-ProjectInventory.ps1') -OutputDir (Join-Path $fixture 'dtd-report') } catch { if($_.Exception.ToString() -notmatch 'DTD|DtdProcessing'){throw}; $dtdRejected=$true }
  if(!$dtdRejected){throw 'External-entity declaration was not rejected'}
  $checks+='DTD/external entity rejected without evaluation'
  $rows=@(Import-Csv -LiteralPath (Join-Path $absolute 'files.csv'))
  foreach($relative in $cases.Keys){
   $row=@($rows|Where-Object Path -eq $relative)
   if($row.Count -ne 1 -or $row[0].Category -ne $cases[$relative]){throw ('Classification mismatch: '+$relative)}
   $checks+=('conservative classification: '+$relative)
  }
  if(($rows|Where-Object Path -eq 'mystery-test.dll').BinaryReview -ne 'BINARY_REQUIRES_RUNTIME_REVIEW'){throw 'Unknown DLL lost runtime-review flag'}
  $checks+='unknown DLL remains explicitly subject to runtime review'
  if((Get-Content -LiteralPath (Join-Path $absolute 'files.csv') -Raw).Contains('synthetic-content-marker-not-for-inventory')){throw 'Inventory exported file contents'}
  $checks+='metadata export does not copy fixture contents'
  foreach($relative in $cases.Keys){if([IO.File]::ReadAllText((Join-Path $fixture $relative)) -ne 'synthetic-content-marker-not-for-inventory'){throw ('Fixture mutated: '+$relative)}}
  $checks+='all classification inputs remain unchanged'

 } finally { Pop-Location }
} finally { [Environment]::CurrentDirectory=$oldProcess }
$checks | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'results.json') -Encoding UTF8
Write-Host "INVENTORY_PATHS=PASS ($($checks.Count) checks; synthetic repository only)"
