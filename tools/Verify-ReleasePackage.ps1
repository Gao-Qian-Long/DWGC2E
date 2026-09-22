# Purpose: verify required release resources, default configuration and CAD payload.
# Input: PublishDir. Output: pass/fail diagnostics; does not install or publish online.
# Usage: powershell -NoProfile -File tools/Verify-ReleasePackage.ps1 -PublishDir artifacts/publish-<timestamp>
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,
    # Report-only scan of the published binaries. The defaults are strings that must never appear in
    # a client build: the retired client-side prompt file name and the server-side credential
    # variable. api.deepseek.com is deliberately not here: the client keeps a direct-connection mode
    # and an environment-check hint that legitimately mention it, so a hit there is evidence to
    # review, not automatically a leak.
    [string[]]$ForbiddenBinaryStrings = @('deepl_context', 'DEEPSEEK_API_KEY'),
    # Additional strings to look for and report without blocking, e.g.
    # -ObservedBinaryStrings 'api.deepseek.com','Translate the following CAD drawing text'.
    [string[]]$ObservedBinaryStrings = @(),
    # Opt-in escalation for the forbidden set only; off by default so a hit stays evidence.
    [switch]$FailOnForbiddenBinaryStrings
)

$ErrorActionPreference = 'Stop'
$publishDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublishDir)
$required = @(
    'QLCAD.exe',
    'settings.json',
    'assets\default-glossaries\mechanical_zh_en.json',
    'glossaries\mechanical_zh_en.json',
    'CadPlugin\DwgTranslator.Cad.dll',
    'CadPlugin\DwgTranslator.Core.dll',
    'CadPlugin\cad-platform.txt'
)

$missing = @($required | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $publishDir $_) -PathType Leaf)
})
if ($missing.Count -gt 0) {
    throw "Missing release files: $($missing -join ', ')"
}

& (Join-Path $PSScriptRoot 'Verify-CadPluginPackage.ps1') `
    -PluginDir (Join-Path $publishDir 'CadPlugin')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$config = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $publishDir 'settings.json') | ConvertFrom-Json
if ($config.licensingEnabled -ne $false) {
    throw 'Published settings must keep licensing disabled for this release.'
}
foreach ($forbidden in @('deepSeekApiKey','deepSeekBaseUrl','deepSeekModel','apiMode')) {
    if ($config.PSObject.Properties.Name -contains $forbidden) { throw "Published settings must not expose legacy AI field: $forbidden" }
}
if (Test-Path -LiteralPath (Join-Path $publishDir 'prompts')) { throw 'Published package must not contain client-side AI prompts.' }

# -- Compiled binary scan (report only) --------------------------------------
# String extraction from the published binaries is a coarse but cheap regression net for prompt or
# credential plumbing that is supposed to live on the server only. Two encodings are searched (UTF-8
# for resources and text payloads, UTF-16LE for .NET string literals). Scope matters: the single-file
# executable keeps its bundled assemblies compressed, so a plain byte scan of it only sees the
# apphost, while the assemblies shipped next to it (CadPlugin\*.dll) are plain text and readable.
$latin1 = [Text.Encoding]::GetEncoding(28591)   # byte <-> char 1:1, so a string IndexOf is a byte search
function Test-BinaryString([string]$Haystack, [string]$Needle) {
    foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
        $encoded = $latin1.GetString($encoding.GetBytes($Needle))
        if ($Haystack.IndexOf($encoded, [StringComparison]::Ordinal) -ge 0) { return $true }
    }
    return $false
}
$scanTargets = @()
foreach ($needle in @($ForbiddenBinaryStrings | Where-Object { $_ })) { $scanTargets += [pscustomobject]@{ text = $needle; blocking = $true } }
foreach ($needle in @($ObservedBinaryStrings | Where-Object { $_ })) { $scanTargets += [pscustomobject]@{ text = $needle; blocking = $false } }
$scanFiles = @(Get-ChildItem -LiteralPath $publishDir -Filter '*.exe' -File)
$cadPluginDir = Join-Path $publishDir 'CadPlugin'
if (Test-Path -LiteralPath $cadPluginDir -PathType Container) { $scanFiles += @(Get-ChildItem -LiteralPath $cadPluginDir -Filter '*.dll' -File) }
$binaryHits = New-Object 'System.Collections.Generic.List[string]'
$binaryBlockers = New-Object 'System.Collections.Generic.List[string]'
$binaryChecked = 0
foreach ($scanFile in $scanFiles) {
    $relativeName = $scanFile.FullName.Substring($publishDir.Length + 1).Replace('\','/')
    $scanBytes = [IO.File]::ReadAllBytes($scanFile.FullName)
    $scanHaystack = $latin1.GetString($scanBytes)
    $scanBytes = $null
    foreach ($target in $scanTargets) {
        $binaryChecked++
        if (Test-BinaryString $scanHaystack $target.text) {
            $binaryHits.Add("$relativeName!$($target.text)") | Out-Null
            if ($target.blocking) { $binaryBlockers.Add("$relativeName!$($target.text)") | Out-Null }
        }
    }
    $scanHaystack = $null
}
if ($binaryHits.Count -gt 0) {
    Write-Output "BINARY_STRING_SCAN=FOUND;HITS=$($binaryHits -join ',')"
    Write-Warning "Published binaries contain string(s) listed for review: $($binaryHits -join ', '). Reported as evidence only; the client legitimately keeps its own user-message template, so this does not block the release by default."
    if ($FailOnForbiddenBinaryStrings -and $binaryBlockers.Count -gt 0) {
        throw "Published binaries expose server-side string(s): $($binaryBlockers -join ', ')"
    }
} else {
    Write-Output "BINARY_STRING_SCAN=CLEAN;FILES=$($scanFiles.Count);CHECKED=$binaryChecked"
}

$platform = (Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $publishDir 'CadPlugin\cad-platform.txt')).Trim()
if ($platform -notin @('GstarCAD', 'AutoCAD')) {
    throw "Unsupported CAD plugin platform manifest: $platform"
}

Write-Output 'RELEASE_PACKAGE=OK'
Write-Output "PUBLISH_DIR=$publishDir"
exit 0
