# Purpose: verify the CAD plugin package and required private dependencies.
# Input: PluginDir. Output: pass/fail diagnostics; does not load CAD or install plugins.
# Usage: powershell -NoProfile -File tools/Verify-CadPluginPackage.ps1 -PluginDir artifacts/publish-<timestamp>/CadPlugin
param(
    [Parameter(Mandatory = $true)]
    [string]$PluginDir
)

$ErrorActionPreference = 'Stop'
$pluginDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PluginDir)
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

# New payloads declare their private files explicitly. Legacy three-file bundles remain readable.
$manifest = Join-Path $pluginDir 'cad-files.txt'
if (Test-Path -LiteralPath $manifest -PathType Leaf) {
    $names = @(Get-Content -LiteralPath $manifest -Encoding UTF8)
    if ($names.Count -eq 0 -or $names.Count -gt 2048) { throw 'Invalid CAD payload manifest size.' }
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $names) {
        $parts = @($name.Replace('\','/').Split('/'))
        if ([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::IsPathRooted($name)) { throw 'Unsafe CAD payload path.' }
        foreach ($part in $parts) {
            if (-not $part -or $part -in @('.','..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or
                $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Unsafe CAD payload path.' }
        }
        if ($name -in @('cad-files.txt','dwgc2e-installed-files.json') -or -not $seen.Add($name.Replace('\','/'))) {
            throw 'Duplicate or reserved CAD payload path.'
        }
        if ($parts[-1] -in @('AcCoreMgd.dll','AcDbMgd.dll','AcMgd.dll','AcCui.dll','GcCoreMgd.dll','GcDbMgd.dll','GcMgd.dll')) {
            throw 'CAD host SDK must not be included in private payload.'
        }
        $path = Join-Path $pluginDir $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing declared CAD dependency: $name" }
        $current = [IO.Path]::GetFullPath($path)
        while ($current) {
            if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'CAD payload cannot contain redirected paths.'
            }
            $current = [IO.Path]::GetDirectoryName($current)
        }
    }
    foreach ($name in @('DwgTranslator.Cad.dll','DwgTranslator.Core.dll','cad-platform.txt')) {
        if (-not $seen.Contains($name)) { throw "CAD manifest omits required file: $name" }
    }
    Write-Output "CAD_PAYLOAD_MANIFEST=OK; FILES=$($seen.Count)"
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
