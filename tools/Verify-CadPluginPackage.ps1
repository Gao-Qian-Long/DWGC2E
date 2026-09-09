param(
    [Parameter(Mandatory = $true)]
    [string]$PluginDir
)

$ErrorActionPreference = 'Stop'
$pluginDir = [IO.Path]::GetFullPath($PluginDir)
$plugin = Join-Path $pluginDir 'DwgTranslator.Cad.dll'

if (-not (Test-Path -LiteralPath $plugin -PathType Leaf)) {
    throw "Missing CAD plugin: $plugin"
}

$required = @(
    'DwgTranslator.Core.dll'
)

$missing = @($required | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $pluginDir $_) -PathType Leaf)
})

if ($missing.Count -gt 0) {
    throw "Missing CAD plugin dependencies: $($missing -join ', ')"
}

$invalid = @()
Get-ChildItem -LiteralPath $pluginDir -Filter '*.dll' -File | ForEach-Object {
    try {
        [void][Reflection.AssemblyName]::GetAssemblyName($_.FullName)
    }
    catch {
        $invalid += $_.Name
    }
}

if ($invalid.Count -gt 0) {
    throw "Invalid managed assemblies: $($invalid -join ', ')"
}

if (Get-ChildItem -LiteralPath $pluginDir -Filter 'Gc*Mgd.dll' -File -ErrorAction SilentlyContinue) {
    throw 'GstarCAD host assemblies must not be redistributed in CadPlugin.'
}
if (Get-ChildItem -LiteralPath $pluginDir -Filter 'Ac*Mgd.dll' -File -ErrorAction SilentlyContinue) {
    throw 'AutoCAD host assemblies must not be redistributed in CadPlugin.'
}

$count = (Get-ChildItem -LiteralPath $pluginDir -Filter '*.dll' -File).Count
Write-Output "CAD_PLUGIN_PACKAGE=OK"
Write-Output "PLUGIN=$plugin"
Write-Output "MANAGED_DLL_COUNT=$count"
exit 0
