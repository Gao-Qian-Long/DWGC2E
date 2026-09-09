param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir
)

$ErrorActionPreference = 'Stop'
$publishDir = [IO.Path]::GetFullPath($PublishDir)
$required = @(
    'DwgTranslator.exe',
    'settings.json',
    'glossaries\mechanical_zh_en.json',
    'prompts\deepl_context.txt',
    'CadPlugin\DwgTranslator.Cad.dll',
    'CadPlugin\DwgTranslator.Core.dll'
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

$config = Get-Content -Raw -Encoding UTF8 (Join-Path $publishDir 'settings.json') | ConvertFrom-Json
if ($config.licensingEnabled -ne $false) {
    throw 'Published settings must keep licensing disabled for this release.'
}

Write-Output 'RELEASE_PACKAGE=OK'
Write-Output "PUBLISH_DIR=$publishDir"
exit 0
