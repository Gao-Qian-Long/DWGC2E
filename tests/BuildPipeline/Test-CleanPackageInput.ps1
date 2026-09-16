# Purpose: local packager input safety regression; no build/install/network/payment actions.
param([Parameter(Mandatory=$true)][string]$PublishDir, [Parameter(Mandatory=$true)][string]$EvidenceDir)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tool=Join-Path $root 'tools/Assert-CleanPackageInput.ps1'
$packager=Join-Path $root 'tools/New-ReleasePackage.ps1'
$evidence=[IO.Path]::GetFullPath($EvidenceDir)
if(Test-Path -LiteralPath $evidence){throw 'Use a fresh evidence directory.'}
$fixture=Join-Path $evidence 'workspace'
$candidate=Join-Path $fixture 'artifacts/publish-20260916-000000'
New-Item -ItemType Directory -Path $candidate -Force|Out-Null
Get-ChildItem -LiteralPath $PublishDir|Copy-Item -Destination $candidate -Recurse
New-Item -ItemType Directory -Path (Join-Path $fixture 'assets/glossaries'),(Join-Path $fixture 'assets/prompts') -Force|Out-Null
foreach($relative in @('settings.json.example','assets/glossaries/mechanical_zh_en.json','assets/prompts/deepl_context.txt')){
 Copy-Item -LiteralPath (Join-Path $root $relative) -Destination (Join-Path $fixture $relative)
}
$results=New-Object Collections.Generic.List[object]
function Check($value,$label){if(!$value){throw "FAIL: $label"};$results.Add(@{test=$label;status='PASS'});Write-Host "PASS: $label"}
function Reject($label,[scriptblock]$action,[string]$expected){
 $errorText='';try{& $action|Out-Null}catch{$errorText=$_.Exception.Message}
 Check ($errorText -like "*$expected*") $label
}
$info=& $tool -PublishDir $candidate -WorkspaceRoot $fixture
Check ($info.sha256 -eq (Get-FileHash (Join-Path $candidate 'DwgTranslator.exe')).Hash) 'clean candidate accepted'
Reject 'installed release rejected' {& $tool -PublishDir (Join-Path $root 'release')} 'direct artifacts/publish'
$metadata=Join-Path $candidate 'build-info.json';$original=[IO.File]::ReadAllBytes($metadata)
try{
 $bad=Get-Content $metadata -Raw|ConvertFrom-Json;$bad.sha256=('0'*64);$bad|ConvertTo-Json|Set-Content $metadata
 Reject 'executable hash mismatch rejected' {& $tool -PublishDir $candidate -WorkspaceRoot $fixture} 'does not match build-info'
 $bad.sha256=$info.sha256;$bad.version='not-the-executable-version';$bad|ConvertTo-Json|Set-Content $metadata
 Reject 'version mismatch rejected' {& $tool -PublishDir $candidate -WorkspaceRoot $fixture} 'version does not match'
}finally{[IO.File]::WriteAllBytes($metadata,$original)}
foreach($relative in @('settings.json','glossaries/mechanical_zh_en.json','assets/default-glossaries/mechanical_zh_en.json','prompts/deepl_context.txt')){
 $path=Join-Path $candidate $relative;$bytes=[IO.File]::ReadAllBytes($path)
 try{[IO.File]::AppendAllText($path,' user-data');Reject "modified $relative rejected" {& $tool -PublishDir $candidate -WorkspaceRoot $fixture} 'differs from reviewed source'}finally{[IO.File]::WriteAllBytes($path,$bytes)}
}
$extra=Join-Path $candidate 'glossaries/private.json'
try{Set-Content -LiteralPath $extra 'private fixture';Reject 'additional personal glossary rejected' {& $tool -PublishDir $candidate -WorkspaceRoot $fixture} 'Unreviewed resource'}finally{Remove-Item -LiteralPath $extra}
Reject 'output inside candidate rejected' {& $packager -PublishDir $PublishDir -OutputDir (Join-Path $PublishDir 'nested-output') -SkipZip} 'inside its input candidate'
foreach ($forbidden in @((Join-Path $root 'src'),(Join-Path $root 'release'),$root)) {
 Reject "output outside artifacts rejected: $forbidden" {& $packager -PublishDir $PublishDir -OutputDir $forbidden -SkipZip} 'must stay under workspace artifacts'
}
# Regression: PowerShell Set-Location does not guarantee Environment.CurrentDirectory changes.
Push-Location -LiteralPath $fixture
try {
 $relativeInfo=& $tool -PublishDir 'artifacts/publish-20260916-000000' -WorkspaceRoot '.'
 Check ($relativeInfo.sha256 -eq $info.sha256) 'relative candidate resolves from PowerShell location'
 $relativeOutput='relative-package'
 & $packager -PublishDir $PublishDir -OutputDir $relativeOutput -SkipZip
 $relativeStage=Join-Path $fixture ('relative-package/DwgTranslator-'+$info.version.Split('+')[0]+'-win-x64')
 Check ((Test-Path -LiteralPath (Join-Path $relativeStage 'build-info.json'))) 'relative output stays in caller directory within artifacts'
} finally { Pop-Location }
$output=Join-Path $evidence 'existing-zip';New-Item -ItemType Directory $output|Out-Null
$version=$info.version.Split('+')[0];$zip=Join-Path $output "DwgTranslator-$version-win-x64.zip";Set-Content -LiteralPath $zip 'preserve existing fixture'
$before=(Get-FileHash $zip).Hash
Reject 'existing zip rejected before staging' {& $packager -PublishDir $PublishDir -OutputDir $output} 'ZIP already exists'
Check ((Get-FileHash $zip).Hash -eq $before -and !(Test-Path (Join-Path $output "DwgTranslator-$version-win-x64"))) 'existing zip untouched and no stage created'
# Actual production script, using current clean binary and real installer files, without rebuilding APP.
$validOutput=Join-Path $evidence 'valid-package'
& $packager -PublishDir $PublishDir -OutputDir $validOutput
$stage=Join-Path $validOutput "DwgTranslator-$version-win-x64"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $validOutput "DwgTranslator-$version-win-x64.zip"))
try { Check ($archive.Entries.Count -eq 15 -and @($archive.Entries | Where-Object FullName -eq 'build-info.json').Count -eq 1) 'zip contains 15 expected payload files including provenance' } finally { $archive.Dispose() }
Check ((Get-FileHash (Join-Path $stage 'DwgTranslator.exe')).Hash -eq $info.sha256) 'packaged executable matches manifest'
Check ((Get-FileHash (Join-Path $stage 'build-info.json')).Hash -eq (Get-FileHash (Join-Path $PublishDir 'build-info.json')).Hash) 'package retains build provenance'
Reject 'existing stage rejected' {& $packager -PublishDir $PublishDir -OutputDir $validOutput} 'output already exists'
# A valid Windows directory may contain brackets; never treat it as a wildcard.
$literalOutput=Join-Path $evidence '[review]'
& $packager -PublishDir $PublishDir -OutputDir $literalOutput
$literalStage=Join-Path $literalOutput "DwgTranslator-$version-win-x64"
Check ((Get-ChildItem -LiteralPath $literalStage -Recurse -File).Count -eq 15) 'bracket directory retains all payload files'
$literalZip=Join-Path $literalOutput "DwgTranslator-$version-win-x64.zip"
$literalArchive=[IO.Compression.ZipFile]::OpenRead($literalZip)
try {
 Check ($literalArchive.Entries.Count -eq 15 -and @($literalArchive.Entries | Where-Object { $_.FullName.Replace('\','/') -eq 'CadPlugin/DwgTranslator.Cad.dll' }).Count -eq 1) 'bracket directory zip has expected root and CAD payload'
} finally { $literalArchive.Dispose() }
Check ((Get-FileHash -LiteralPath (Join-Path $literalStage 'DwgTranslator.exe')).Hash -eq $info.sha256) 'bracket directory executable matches candidate'
$results|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $evidence 'results.json') -Encoding UTF8
Write-Host "CLEAN_PACKAGE_INPUT_TESTS=PASS ($($results.Count))"
