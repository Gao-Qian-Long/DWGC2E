param([switch]$BuildOnly)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$release = Join-Path $root 'release'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path $root "artifacts/publish-$stamp"
$backup = Join-Path $root "artifacts/release-backup-$stamp"
$next = Join-Path $root "artifacts/release-next-$stamp"
function Assert-WorkspacePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe target path: $full" }
    return $full
}
try {
    if (-not $BuildOnly) {
        $running = Get-Process DwgTranslator -ErrorAction SilentlyContinue | Where-Object { $_.Path -and [IO.Path]::GetFullPath($_.Path) -eq (Join-Path $release 'DwgTranslator.exe') }
        if ($running) { throw 'Close the release application before publishing. No processes were stopped.' }
    }
    & dotnet test (Join-Path $root 'tests/DwgTranslator.Core.Tests/DwgTranslator.Core.Tests.csproj') -c Release -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Core regression tests failed' }
    & dotnet run --project (Join-Path $root 'tests/DwgTranslator.App.UiSmoke/DwgTranslator.App.UiSmoke.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Desktop UI smoke tests failed' }
    $revision = (& git rev-parse --short HEAD).Trim()
    $version = "2.1.0+ui.$stamp.$revision"
    & dotnet publish (Join-Path $root 'src/DwgTranslator.App/DwgTranslator.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true "-p:InformationalVersion=$version" -p:IncludeSourceRevisionInInformationalVersion=false -o $stage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
    foreach ($folder in @('glossaries','prompts')) { New-Item -ItemType Directory -Path (Join-Path $stage $folder) -Force | Out-Null }
    Copy-Item -LiteralPath (Join-Path $root 'settings.json.example') -Destination (Join-Path $stage 'settings.json') -Force
    Copy-Item -LiteralPath (Join-Path $root 'glossaries/mechanical_zh_en.json') -Destination (Join-Path $stage 'glossaries/mechanical_zh_en.json') -Force
    Copy-Item -LiteralPath (Join-Path $root 'prompts/deepl_context.txt') -Destination (Join-Path $stage 'prompts/deepl_context.txt') -Force
    & (Join-Path $root 'tools/Verify-ReleasePackage.ps1') -PublishDir $stage
    if (-not $?) { throw 'Release dependency verification failed' }
    $exe = Join-Path $stage 'DwgTranslator.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'Missing executable' }
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    @{ version=$version; revision=$revision; builtAt=(Get-Date -Format o); sha256=$hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'build-info.json') -Encoding UTF8
    $platform = (Get-Content -LiteralPath (Join-Path $stage 'CadPlugin/cad-platform.txt') -Raw).Trim()
    $zip = Join-Path $root "artifacts/DwgTranslator-win-x64-$platform-$stamp.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    if (-not $BuildOnly) {
        New-Item -ItemType Directory -Path $next | Out-Null
        Get-ChildItem -LiteralPath $stage | Copy-Item -Destination $next -Recurse -Force
        # Preserve installed portable data. The distributable ZIP uses clean defaults instead.
        foreach ($name in @('settings.json','glossaries','prompts','exports','logs')) {
            $source = Join-Path $release $name
            if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $next -Recurse -Force }
        }
        $moved = $false
        try {
            if (Test-Path -LiteralPath $release) { Move-Item -LiteralPath (Assert-WorkspacePath $release) -Destination (Assert-WorkspacePath $backup); $moved = $true }
            Move-Item -LiteralPath (Assert-WorkspacePath $next) -Destination (Assert-WorkspacePath $release)
            $installedHash = (Get-FileHash -LiteralPath (Join-Path $release 'DwgTranslator.exe') -Algorithm SHA256).Hash
            if ($installedHash -ne $hash) { throw 'Installed executable hash mismatch' }
        }
        catch {
            if ($moved) {
                if (Test-Path -LiteralPath $release) { Move-Item -LiteralPath (Assert-WorkspacePath $release) -Destination (Assert-WorkspacePath (Join-Path $root "artifacts/release-failed-$stamp")) }
                Move-Item -LiteralPath (Assert-WorkspacePath $backup) -Destination (Assert-WorkspacePath $release)
            }
            throw
        }
    }
    Write-Host "SUCCESS: $version"
    Write-Host "Build: $exe"
    Write-Host "SHA256: $hash"
    Write-Host "Package: $zip"
    if (-not $BuildOnly) { Write-Host "Installed: $release/DwgTranslator.exe"; Write-Host "Previous release preserved: $backup" }
    exit 0
}
catch { Write-Error "Publish failed. No old executable will be launched. $($_.Exception.Message)"; exit 1 }
