# Input: PublishDir, PackagePath, OutputPath. Output: local verified JSON manifest.
# Usage: powershell -NoProfile -File tools/New-DesktopReleaseManifest.ps1 -PublishDir <candidate> -PackagePath <zip> -OutputPath <local-json>
# Generates a verified LOCAL candidate. Never uploads or authorizes a public release.
param(
    [Parameter(Mandatory=$true)][string]$PublishDir,
    [Parameter(Mandatory=$true)][string]$PackagePath,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$dir=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublishDir)
$package=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PackagePath)
$output=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$info=Get-Content -LiteralPath (Join-Path $dir 'build-info.json') -Raw | ConvertFrom-Json
if([string]$info.version -notmatch '^\d+\.\d+\.\d+\+ui\.\d{8}-\d{6}\.[a-zA-Z0-9]+$'){throw 'Invalid desktop build version'}
$platform=(Get-Content -LiteralPath (Join-Path $dir 'CadPlugin/cad-platform.txt') -Raw).Trim()
if($platform -notin @('GstarCAD','AutoCAD')){throw 'Unsupported CAD platform'}
$names=@('QLCAD.exe','CadPlugin/DwgTranslator.Cad.dll','CadPlugin/DwgTranslator.Core.dll')
$files=@($names | ForEach-Object {
    $file=Get-Item -LiteralPath (Join-Path $dir $_)
    [ordered]@{name=$_;size=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
})
if($files[0].sha256 -ne $info.sha256){throw 'Executable does not match build-info hash'}
$archive=[IO.Compression.ZipFile]::OpenRead($package)
function Read-UniqueEntry([string]$name) {
    $matches=@($archive.Entries | Where-Object { $_.FullName.Replace('\','/') -ceq $name })
    if($matches.Count -ne 1){throw "Missing or duplicate archive entry: $name"}
    return $matches[0]
}
function Read-EntryText([string]$name) {
    $reader=[IO.StreamReader]::new((Read-UniqueEntry $name).Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
try {
    $packedInfo=Read-EntryText 'build-info.json' | ConvertFrom-Json
    if($packedInfo.version -cne $info.version -or $packedInfo.sha256 -ne $info.sha256 -or $packedInfo.revision -cne $info.revision){throw 'Package build metadata does not match installed candidate'}
    if((Read-EntryText 'CadPlugin/cad-platform.txt').Trim() -cne $platform){throw 'Package CAD platform mismatch'}
    foreach($file in $files){
        $entry=Read-UniqueEntry $file.name
        $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
        try { $hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','') }
        finally { $stream.Dispose();$sha.Dispose() }
        if($hash -ne $file.sha256 -or $entry.Length -ne $file.size){throw "Package content mismatch: $($file.name)"}
    }
} finally { $archive.Dispose() }
$result=[ordered]@{
    schema_version=1;status='local_candidate';public_download_authorized=$false
    latest_version=$info.version;revision=$info.revision;built_at=$info.builtAt
    platform='win-x64';cad_platform=$platform;download_url=$null;backup_download_url=$null;mandatory=$false
    package=[ordered]@{name=[IO.Path]::GetFileName($package);size=(Get-Item -LiteralPath $package).Length;sha256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash}
    files=$files
}
# Output is new only: never overwrite an existing manifest or source artifact by mistake.
$bytes=[Text.Encoding]::UTF8.GetBytes(($result | ConvertTo-Json -Depth 8))
$stream=[IO.FileStream]::new($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try { $stream.Write($bytes,0,$bytes.Length);$stream.Flush($true) } finally { $stream.Dispose() }
Write-Output "LOCAL_RELEASE_MANIFEST=VERIFIED: $output"