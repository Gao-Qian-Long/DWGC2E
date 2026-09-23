# Purpose: verify ignore boundaries for formal source/resources and private/generated data.
# Input: Git and this checkout's ignore rules. Output: pass/fail only; no files/index changes.
# Usage: powershell -NoProfile -File tests/BuildPipeline/Test-GitIgnore.ps1
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$cases=@(
    @{Path='build-release.bat';Ignored=$false},
    @{Path='build-setup.bat';Ignored=$false},
    @{Path='DwgTranslator.sln';Ignored=$false},
    @{Path='Directory.Build.props';Ignored=$false},
    @{Path='settings.json.example';Ignored=$false},
    @{Path='assets/icons/icon.ico';Ignored=$false},
    @{Path='assets/icons/brand.svg';Ignored=$false},
    @{Path='assets/glossaries/mechanical_zh_en.json';Ignored=$false},
    @{Path='assets/prompts/deepl_context.txt';Ignored=$false},
    @{Path='src/DwgTranslator.App/App.xaml';Ignored=$false},
    @{Path='src/DwgTranslator.Core/Models/AppConfig.cs';Ignored=$false},
    @{Path='installer/Install.ps1';Ignored=$false},
    @{Path='tests/DwgTranslator.Core.Tests/GlossaryFileStoreTests.cs';Ignored=$false},
    @{Path='settings.json';Ignored=$true},
    @{Path='Directory.Build.props.user';Ignored=$true},
    @{Path='src/DwgTranslator.App/bin/Release/example.dll';Ignored=$true},
    @{Path='src/DwgTranslator.App/obj/example.json';Ignored=$true},
    @{Path='release/settings.json';Ignored=$true},
    @{Path='artifacts/probe/report.json';Ignored=$true},
    @{Path='.wrangler/cache/cf.json';Ignored=$true},
    @{Path='%SystemDrive%/ProgramData/Microsoft/Windows/Caches/cversions.2.db';Ignored=$true},
    @{Path='cf-worker/.dev.vars';Ignored=$true},
    @{Path='cf-worker/.env.local';Ignored=$true},
    @{Path='cf-worker/node_modules/probe/index.js';Ignored=$true},
    @{Path='accounts/probe/tasks.json';Ignored=$true},
    @{Path='logs/probe.log';Ignored=$true},
    @{Path='temp/probe.json';Ignored=$true}
)
foreach($case in $cases){
    # --no-index tests the rule even for an already tracked file; Git needs no fixture to exist.
    & git -C $root check-ignore --no-index --quiet -- $case.Path
    $code=$LASTEXITCODE
    if($code -notin @(0,1)){throw "Git check-ignore failed ($code): $($case.Path)"}
    if(($code -eq 0) -ne $case.Ignored){throw "Unexpected ignore rule: $($case.Path); expected ignored=$($case.Ignored)"}
    Write-Host "PASS $($case.Path); ignored=$($case.Ignored)"
}
Write-Host "GIT_IGNORE=PASS; $($cases.Count) checks; no files or index written"