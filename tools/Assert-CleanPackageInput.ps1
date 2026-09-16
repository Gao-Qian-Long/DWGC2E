# Usage: invoked by New-ReleasePackage.ps1; direct check: & ./tools/Assert-CleanPackageInput.ps1 -PublishDir <candidate>
# Purpose: reject installed/user-modified payloads before creating a distributable package.
# Input: a direct artifacts/publish-yyyyMMdd-HHmmss candidate; output: validated build metadata.
# This checks local packaging inputs, not code signing or authorization to deploy online.
param([Parameter(Mandatory=$true)][string]$PublishDir,
      [string]$WorkspaceRoot)
$ErrorActionPreference = 'Stop'
if (-not $WorkspaceRoot) { $WorkspaceRoot = Split-Path -Parent $PSScriptRoot }
$root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkspaceRoot).TrimEnd('\')
$publish = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublishDir).TrimEnd('\')
$artifacts = Join-Path $root 'artifacts'
if ([IO.Path]::GetDirectoryName($publish) -ne $artifacts -or
    [IO.Path]::GetFileName($publish) -notmatch '^publish-\d{8}-\d{6}$') {
    throw 'Package input must be a direct artifacts/publish-yyyyMMdd-HHmmss candidate, never release or installed data.'
}
# Inspect each directory before descending, including ancestors, so links are never followed.
function Assert-NoLink([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked package input is not supported: $Path" }
}
$ancestor = $publish
while ($ancestor) { Assert-NoLink $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor) }
function Assert-Tree([string]$Path) {
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force) {
        Assert-NoLink $item.FullName
        if ($item.PSIsContainer) { Assert-Tree $item.FullName }
    }
}
Assert-Tree $publish
$info = Get-Content -LiteralPath (Join-Path $publish 'build-info.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$exe = Join-Path $publish 'DwgTranslator.exe'
if ($info.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $info.sha256) {
    throw 'Candidate executable does not match build-info.json.'
}
if ([string]::IsNullOrWhiteSpace($info.version) -or (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -ne $info.version) {
    throw 'Candidate executable version does not match build-info.json.'
}
$defaults = @{
    'settings.json' = 'settings.json.example'
    'glossaries\mechanical_zh_en.json' = 'assets\glossaries\mechanical_zh_en.json'
    'assets\default-glossaries\mechanical_zh_en.json' = 'assets\glossaries\mechanical_zh_en.json'
    'prompts\deepl_context.txt' = 'assets\prompts\deepl_context.txt'
}
foreach ($name in $defaults.Keys) {
    $source = Join-Path $root $defaults[$name]
    Assert-NoLink $source
    if ((Get-FileHash -LiteralPath (Join-Path $publish $name)).Hash -ne (Get-FileHash -LiteralPath $source).Hash) {
        throw "Candidate default differs from reviewed source template: $name"
    }
}
# Do not silently copy personal glossaries, prompts or future unreviewed resources.
foreach ($folder in @('glossaries','assets','prompts')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $publish $folder) -File -Recurse -Force) {
        $relative = $file.FullName.Substring($publish.Length + 1)
        if (-not $defaults.ContainsKey($relative)) { throw "Unreviewed resource in package: $relative" }
    }
}
& (Join-Path $PSScriptRoot 'Verify-ReleasePackage.ps1') -PublishDir $publish | Out-Host
if (-not $?) { throw 'Release package dependency verification failed.' }
$info
