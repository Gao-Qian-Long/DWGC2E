# Purpose: test, publish and safely install the desktop release.
# Input: local source + CAD SDK; output: release and bounded artifacts.
# Usage: .\tools\Publish-Desktop.ps1 (normal delivery includes regression tests).
param([switch]$BuildOnly, [switch]$SkipTests, [switch]$Clean)
$ErrorActionPreference = 'Stop'
# Canonical delivery must always pass regression/UI gates. BuildOnly is an explicit
# isolated diagnostic mode, never a substitute for a verified release update.
if ($SkipTests -and -not $BuildOnly) {
    throw 'RELEASE_REQUIRES_TESTS: Skipping tests cannot update release. Use the normal verified delivery; BuildOnly is for explicitly requested isolated diagnostics only.'
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$release = Join-Path $root 'release'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path $root "artifacts/publish-$stamp"
$backup = Join-Path $root "artifacts/release-backup-$stamp"
$next = Join-Path $root "artifacts/release-next-$stamp"
$deliveryDir = Join-Path $root "artifacts/delivery-$stamp"
$currentPath = Join-Path $root 'artifacts/current-release.json'
function Assert-WorkspacePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe target path: $full" }
    for ($ancestor=$full; $ancestor; $ancestor=[IO.Path]::GetDirectoryName($ancestor)) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked delivery path rejected: $ancestor" }
    }
    return $full
}
$publishLock = $null
try {
    $publishLock = & (Join-Path $PSScriptRoot 'Enter-DesktopPublishLock.ps1') -WorkspaceRoot $root
    if (-not $BuildOnly) {
        $running = Get-Process DwgTranslator -ErrorAction SilentlyContinue | Where-Object { $_.Path -and [IO.Path]::GetFullPath($_.Path) -eq (Join-Path $release 'DwgTranslator.exe') }
        if ($running) { throw 'Close the release application before publishing. No processes were stopped.' }
    }
    # A runtime-only machine-wide dotnet must not hide a usable per-user SDK.
    $sdkHost=$null
    $hosts=@(Get-Command dotnet -All -ErrorAction SilentlyContinue | ForEach-Object Source)
    $hosts+=Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe'
    foreach($hostPath in $hosts | Select-Object -Unique){
        if(-not(Test-Path -LiteralPath $hostPath -PathType Leaf)){continue}
        $sdks=@(& $hostPath --list-sdks)
        if($LASTEXITCODE -eq 0 -and ($sdks | Where-Object {$_ -match '^8\.'})){$sdkHost=$hostPath;break}
    }
    if(-not $sdkHost){throw '.NET 8 SDK missing on build machine. End users do not need the SDK.'}
    $env:PATH=([IO.Path]::GetDirectoryName($sdkHost))+';'+$env:PATH
    $env:DOTNET_ROOT=[IO.Path]::GetDirectoryName($sdkHost)
    Write-Host "BUILD_SDK=$sdkHost"
    Assert-WorkspacePath $deliveryDir | Out-Null
    Assert-WorkspacePath $currentPath | Out-Null
    New-Item -ItemType Directory -Path $deliveryDir | Out-Null
    $sourceBefore = @(& (Join-Path $PSScriptRoot 'Get-DesktopSourceSnapshot.ps1'))
    $sourceBefore | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $deliveryDir 'source-snapshot.json') -Encoding UTF8
    if (-not $BuildOnly) {
        $compiler = Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'
        if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Inno Setup compiler missing; normal delivery requires a verified Setup.exe. Installed release unchanged.' }
    }
    if (-not $SkipTests) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests/BuildPipeline/Test-InstallerBrand.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Brand configuration regression failed' }
    }
    if ($Clean) {
        # All stages share the same publish lock; a clean APP build is never delivered alone.
        $solution = Join-Path $root 'DwgTranslator.sln'
        & dotnet clean $solution -c Release -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Solution clean failed' }
        Write-Host 'SOLUTION_CLEAN=PASS'
        & dotnet restore $solution -p:Configuration=Release -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Solution restore failed' }
        Write-Host 'SOLUTION_RESTORE=PASS'
        & dotnet build $solution -c Release --no-restore -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Solution build failed' }
        Write-Host 'SOLUTION_BUILD=PASS'
    }
    if (-not $SkipTests) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests/BuildPipeline/Test-Installer.ps1') -ResultDir (Join-Path $deliveryDir 'installer-preservation')
    if ($LASTEXITCODE -ne 0) { throw 'Installer preservation regression failed' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests/BuildPipeline/Test-InstallTransaction.ps1') -ResultDir (Join-Path $deliveryDir 'installer-transaction')
    if ($LASTEXITCODE -ne 0) { throw 'Installer transaction/interruption regression failed' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests/BuildPipeline/Test-InstallerBootstrap.ps1') -ResultDir (Join-Path $deliveryDir 'installer-bootstrap')
    if ($LASTEXITCODE -ne 0) { throw 'Installer bootstrap/package regression failed' }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests/BuildPipeline/Test-DesktopDelivery.ps1') -ResultDir (Join-Path $deliveryDir 'delivery-regression')
    if ($LASTEXITCODE -ne 0) { throw 'Desktop delivery safety regression failed' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests/BuildPipeline/Test-DesktopArtifactCleanup.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Artifact retention regression failed' }
    & dotnet test (Join-Path $root 'tests/DwgTranslator.Core.Tests/DwgTranslator.Core.Tests.csproj') -c Release -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Core regression tests failed' }
    & dotnet run --project (Join-Path $root 'tests/TaskStoreCrashProbe/TaskStoreCrashProbe.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Task persistence process-interruption regression failed' }
    & dotnet run --project (Join-Path $root 'tests/DwgTranslator.LayoutRegression/LayoutRegression.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Drawing layout regression failed' }
    & dotnet fsi (Join-Path $root 'tests/CadIntegration/Test-CadPluginInstaller.fsx')
    if ($LASTEXITCODE -ne 0) { throw 'CAD deployment regression failed' }
    $env:DWGC2E_UI_SMOKE_OUTPUT=Join-Path $deliveryDir 'ui-smoke'
    & dotnet run --project (Join-Path $root 'tests/DwgTranslator.App.UiSmoke/DwgTranslator.App.UiSmoke.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Desktop UI smoke tests failed' }
    } else { Write-Host 'TESTS_SKIPPED: explicitly requested; build and package checks still run.' }
    $revision = (& git rev-parse --short HEAD).Trim()
    $version = "2.1.1+ui.$stamp.$revision"
    & dotnet publish (Join-Path $root 'src/DwgTranslator.App/DwgTranslator.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true "-p:InformationalVersion=$version" -p:IncludeSourceRevisionInInformationalVersion=false -o $stage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
    foreach ($folder in @('glossaries','prompts')) { New-Item -ItemType Directory -Path (Join-Path $stage $folder) -Force | Out-Null }
    Copy-Item -LiteralPath (Join-Path $root 'settings.json.example') -Destination (Join-Path $stage 'settings.json') -Force
    Copy-Item -LiteralPath (Join-Path $root 'assets/glossaries/mechanical_zh_en.json') -Destination (Join-Path $stage 'glossaries/mechanical_zh_en.json') -Force
    Copy-Item -LiteralPath (Join-Path $root 'assets/prompts/deepl_context.txt') -Destination (Join-Path $stage 'prompts/deepl_context.txt') -Force
    & (Join-Path $root 'tools/Verify-ReleasePackage.ps1') -PublishDir $stage
    if (-not $?) { throw 'Release dependency verification failed' }
    $exe = Join-Path $stage 'DwgTranslator.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'Missing executable' }
    # Check candidate resources before packaging or touching the installed release.
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools/Verify-ExecutableIcon.ps1') -ExecutablePath $exe
    if ($LASTEXITCODE -ne 0) { throw 'Candidate executable icon verification failed' }
    if (-not $SkipTests) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests/BuildPipeline/Test-DesktopShellIconRefresh.ps1') -ExecutablePath $exe
        if ($LASTEXITCODE -ne 0) { throw 'Shell icon refresh regression failed' }
    }
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    @{ version=$version; revision=$revision; builtAt=(Get-Date -Format o); sha256=$hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'build-info.json') -Encoding UTF8
    # Audit the freshly published candidate, never the previous installed release.
    # A failed dependency/resource/hash check stops before ZIP creation or release replacement.
    $auditReport = Join-Path $deliveryDir 'architecture-audit.json'
    & dotnet run --project (Join-Path $root 'tests/ArchitectureAudit/ArchitectureAudit.csproj') -c Release -- $root $auditReport $stage
    if ($LASTEXITCODE -ne 0) { throw 'Candidate architecture/resource audit failed; installed release preserved' }
    Write-Host 'CANDIDATE_ARCHITECTURE_AUDIT=PASS'
    # Symbols are private diagnostics, never part of runtime or public payload.
    $symbols = @(Get-ChildItem -LiteralPath $stage -Filter '*.pdb' -File)
    if ($symbols.Count) {
        $symbolDir = Join-Path $deliveryDir 'symbols'
        New-Item -ItemType Directory -Path $symbolDir | Out-Null
        foreach ($symbol in $symbols) { Move-Item -LiteralPath (Assert-WorkspacePath $symbol.FullName) -Destination (Assert-WorkspacePath (Join-Path $symbolDir $symbol.Name)) }
    }
    & (Join-Path $PSScriptRoot 'Get-DesktopPayload.ps1') -PublishDir $stage | Out-Null
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests/BuildPipeline/Test-DesktopPayload.ps1') -PublishDir $stage -ResultDir (Join-Path $deliveryDir 'payload-regression')
    if ($LASTEXITCODE -ne 0) { throw 'Public payload privacy regression failed' }
    $platform = (Get-Content -LiteralPath (Join-Path $stage 'CadPlugin/cad-platform.txt') -Raw).Trim()
    $zip = Join-Path $root "artifacts/DwgTranslator-win-x64-$platform-$stamp.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    if (-not $BuildOnly) {
        $installerArgs = @{ PublishDir=$stage; OutputDir=(Join-Path $deliveryDir 'installer'); Compiler=$compiler; PublishLock=$publishLock }
        if (Test-Path -LiteralPath (Join-Path $release 'build-info.json')) {
            $previousInfo=Get-Content -LiteralPath (Join-Path $release 'build-info.json') -Raw | ConvertFrom-Json
            if($previousInfo.version -notmatch '\+ui\.(\d{8}-\d{6})\.') { throw 'Previous release lacks candidate provenance' }
            $previousCandidate=Join-Path $root ('artifacts/publish-'+$Matches[1])
            # Never compile a test installer from personal settings in the installed release.
            if(-not(Test-Path -LiteralPath $previousCandidate)){throw 'Previous clean candidate missing; restore it before cross-build upgrade acceptance.'}
            if((Get-FileHash -LiteralPath (Join-Path $previousCandidate 'DwgTranslator.exe')).Hash -ne $previousInfo.sha256){throw 'Previous candidate does not match installed build'}
            $installerArgs.PreviousPublishDir=$previousCandidate
        }
        $installer = & (Join-Path $PSScriptRoot 'New-DesktopInstaller.ps1') @installerArgs
        if (-not $installer.installerSha256) { throw 'Missing verified installer receipt' }
        $sourceAfter = @(& (Join-Path $PSScriptRoot 'Get-DesktopSourceSnapshot.ps1'))
        if (($sourceBefore | ConvertTo-Json -Compress) -cne ($sourceAfter | ConvertTo-Json -Compress)) { throw 'Build inputs changed during delivery; installed release preserved.' }
        if (Get-Process DwgTranslator -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $release 'DwgTranslator.exe') }) { throw 'Release was started during validation; installed release preserved.' }
        New-Item -ItemType Directory -Path $next | Out-Null
        Get-ChildItem -LiteralPath $stage | Copy-Item -Destination $next -Recurse -Force
        # Preserve portable config and all unknown user files, not just a short list.
        & (Join-Path $PSScriptRoot 'Copy-DesktopUserData.ps1') -OldRelease $release -NewRelease $next -WorkspaceRoot $root
        $moved = $false
        try {
            if (Test-Path -LiteralPath $release) { Move-Item -LiteralPath (Assert-WorkspacePath $release) -Destination (Assert-WorkspacePath $backup); $moved = $true }
            Move-Item -LiteralPath (Assert-WorkspacePath $next) -Destination (Assert-WorkspacePath $release)
            $installedHash = (Get-FileHash -LiteralPath (Join-Path $release 'DwgTranslator.exe') -Algorithm SHA256).Hash
            if ($installedHash -ne $hash) { throw 'Installed executable hash mismatch' }
            $record = [ordered]@{ schemaVersion=1; status='installed-local'; version=$version; cadPlatform=$platform; appSha256=$hash; installedExecutable=(Join-Path $release 'DwgTranslator.exe'); candidate=$stage; rollbackDirectory=$(if($moved){$backup}else{$null}); installerPath=$installer.installerPath; installerSha256=$installer.installerSha256; publicDirectory=$installer.publicDirectory; acceptanceReport=$installer.acceptanceReport; sourceSnapshot=(Join-Path $deliveryDir 'source-snapshot.json'); deliveryDirectory=$deliveryDir; installedAt=(Get-Date -Format o); publicUploadPerformed=$false }
            $recordJson = $record | ConvertTo-Json -Depth 5
            $recordJson | Set-Content -LiteralPath (Join-Path $deliveryDir 'installed-release.json') -Encoding UTF8
            $currentTemp = Join-Path $deliveryDir 'current-release.tmp'
            $recordJson | Set-Content -LiteralPath $currentTemp -Encoding UTF8
            if (Test-Path -LiteralPath $currentPath) { [IO.File]::Replace($currentTemp, $currentPath, (Join-Path $deliveryDir 'previous-current-release.json')) }
            else { [IO.File]::Move($currentTemp, $currentPath) }
        }
        catch {
            if ($moved) {
                if (Test-Path -LiteralPath $release) { Move-Item -LiteralPath (Assert-WorkspacePath $release) -Destination (Assert-WorkspacePath (Join-Path $root "artifacts/release-failed-$stamp")) }
                Move-Item -LiteralPath (Assert-WorkspacePath $backup) -Destination (Assert-WorkspacePath $release)
            }
            throw
        }
    }
    if (-not $BuildOnly) {
        # The replacement retains the same path; invalidate the Shell's old icon entry.
        # A desktop notification failure must not roll back a verified, usable release.
        try { & (Join-Path $root 'tools/Refresh-DesktopShellIcon.ps1') -ExecutablePath (Join-Path $release 'DwgTranslator.exe') | Out-Host }
        catch { Write-Warning "Installed release is valid; Shell icon refresh failed: $($_.Exception.Message)" }
    }
    if (-not $BuildOnly) {
        # Explicit ownership inventory enables bounded deletion without swallowing later user files.
        $ownedFiles=@(Get-ChildItem -LiteralPath $deliveryDir -File -Recurse -Force | ForEach-Object {
            [pscustomobject]@{path=$_.FullName.Substring($deliveryDir.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
        })
        @{schemaVersion=1;files=$ownedFiles} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $deliveryDir 'delivery-files.json') -Encoding UTF8
    }
    # Retain only the latest verified candidate, installed package, and one rollback copy.
    try { & (Join-Path $root 'tools/Clean-DesktopArtifacts.ps1') | Out-Host }
    catch { Write-Warning "Build succeeded; obsolete artifact cleanup skipped: $($_.Exception.Message)" }
    Write-Host "SUCCESS: $version"
    Write-Host "Build: $exe"
    Write-Host "SHA256: $hash"
    Write-Host "Internal portable ZIP (not the primary download): $zip"
    if (-not $BuildOnly) { Write-Host "UPLOAD_THIS_SETUP: $($installer.installerPath)"; Write-Host "CURRENT_RELEASE: $currentPath" }
    if (-not $BuildOnly) { Write-Host "Installed: $release/DwgTranslator.exe"; Write-Host "Previous release preserved: $backup" }
    exit 0
}
catch { Write-Error "Publish failed. No old executable will be launched. $($_.Exception.Message)"; exit 1 }


finally { if ($publishLock) { $publishLock.Dispose() } }
