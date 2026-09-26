param([string]$EvidenceDir)
$ErrorActionPreference='Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$parent=Join-Path $root 'artifacts/cross-end-completion-20260916/release-manifest'
if ($EvidenceDir) { $parent=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDir) }
New-Item -ItemType Directory -Force $parent | Out-Null
$fixture=Join-Path $parent ('fixture-'+[guid]::NewGuid().ToString('N'))
$dir=Join-Path $fixture 'publish';$zip=Join-Path $fixture 'package.zip'
New-Item -ItemType Directory -Force (Join-Path $dir 'CadPlugin') | Out-Null
$tool=Join-Path $root 'tools/New-DesktopReleaseManifest.ps1'
function Check($value,$label){if(!$value){throw $label};Write-Host "PASS $label"}
function Pack { if(Test-Path -LiteralPath $zip){Remove-Item -LiteralPath $zip};Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $zip }
function Reject($label){
    $out=Join-Path $fixture ($label+'.json');$rejected=$false
    try { & $tool -PublishDir $dir -PackagePath $zip -OutputPath $out | Out-Null }catch{$rejected=$true}
    Check ($rejected -and !(Test-Path -LiteralPath $out)) $label
}
try {
    foreach($name in @('QLCAD.exe','CadPlugin/DwgTranslator.Cad.dll','CadPlugin/DwgTranslator.Core.dll')){Set-Content -LiteralPath (Join-Path $dir $name) "fixture $name"}
    Set-Content -LiteralPath (Join-Path $dir 'CadPlugin/cad-platform.txt') 'GstarCAD'
    $info=@{version='2.1.1+ui.20260916-120000.test';revision='test';builtAt='2026-09-16T12:00:00+08:00';sha256=(Get-FileHash (Join-Path $dir 'QLCAD.exe')).Hash}
    $info|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $dir 'build-info.json');Pack
    $out=Join-Path $fixture 'valid.json';& $tool -PublishDir $dir -PackagePath $zip -OutputPath $out | Out-Null
    $m=Get-Content -LiteralPath $out -Raw|ConvertFrom-Json
    Check ($m.latest_version -eq $info.version -and $m.files.Count -eq 3 -and $m.package.sha256 -eq (Get-FileHash $zip).Hash) 'valid version platform and package hashes'
    Check ($m.status -eq 'local_candidate' -and !$m.public_download_authorized -and !$m.download_url -and !$m.mandatory) 'candidate never enables public download or forced update'
    Push-Location -LiteralPath $fixture
    try {
        & $tool -PublishDir 'publish' -PackagePath 'package.zip' -OutputPath '[relative].json' | Out-Null
        $relative=Get-Content -LiteralPath (Join-Path $fixture '[relative].json') -Raw|ConvertFrom-Json
        Check ($relative.latest_version -eq $m.latest_version -and $relative.package.sha256 -eq $m.package.sha256) 'relative input paths use caller location'
        Check ($relative.status -eq 'local_candidate' -and !$relative.public_download_authorized -and !$relative.mandatory) 'relative output stays local and preserves delivery restrictions'
    } finally { Pop-Location }
    $before=(Get-FileHash $out).Hash;$blocked=$false
    try{& $tool -PublishDir $dir -PackagePath $zip -OutputPath $out|Out-Null}catch{$blocked=$true}
    Check ($blocked -and (Get-FileHash $out).Hash -eq $before) 'existing output cannot be overwritten'
    $exe=Get-Content -LiteralPath (Join-Path $dir 'QLCAD.exe') -Raw
    Set-Content -LiteralPath (Join-Path $dir 'QLCAD.exe') 'tampered';Reject 'installed-executable-tampering'
    [IO.File]::WriteAllText((Join-Path $dir 'QLCAD.exe'),$exe)
    Set-Content -LiteralPath (Join-Path $dir 'CadPlugin/DwgTranslator.Cad.dll') 'different';Reject 'plugin-package-mismatch'
    Pack
    $info.version='2.1.1+ui.20260916-130000.test';$info|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $dir 'build-info.json');Reject 'package-version-mismatch'
    Pack;Set-Content -LiteralPath (Join-Path $dir 'CadPlugin/cad-platform.txt') 'AutoCAD';Reject 'package-platform-mismatch'
    Set-Content -LiteralPath (Join-Path $dir 'CadPlugin/cad-platform.txt') 'Unknown';Reject 'unsupported-platform'
    Set-Content -LiteralPath (Join-Path $dir 'CadPlugin/cad-platform.txt') 'AutoCAD';Pack
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $a=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Update)
    try{$entry=$a.CreateEntry('QLCAD.exe');$w=[IO.StreamWriter]::new($entry.Open());try{$w.Write('duplicate')}finally{$w.Dispose()}}finally{$a.Dispose()}
    Reject 'duplicate-executable-entry'
    Write-Host 'RELEASE_MANIFEST_TESTS=PASS (11 cases)'
}finally{
    $full=[IO.Path]::GetFullPath($fixture)
    if([IO.Path]::GetDirectoryName($full) -ne $parent -or [IO.Path]::GetFileName($full) -notmatch '^fixture-[a-f0-9]{32}$'){throw 'Unsafe fixture cleanup'}
    Remove-Item -LiteralPath $full -Recurse -Force
}
