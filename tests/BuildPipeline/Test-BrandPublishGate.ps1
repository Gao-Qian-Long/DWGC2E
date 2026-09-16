# Exercise branding gates without rebuilding APP or invoking the publisher.
param([string]$ResultDir)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(-not $ResultDir){$ResultDir=Join-Path $root 'artifacts/icon-cache-20260916/publish-gate'}
New-Item -ItemType Directory -Force -Path $ResultDir | Out-Null
$verify=Join-Path $root 'tools/Verify-ExecutableIcon.ps1'
$exe=Join-Path $root 'release/DwgTranslator.exe'
$before=(Get-FileHash -LiteralPath $exe).Hash
& $verify -ExecutablePath $exe
& $verify -ExecutablePath $exe # repeated in-process use of native type
$badIcon=Join-Path $ResultDir 'mismatched.ico'
$bytes=[IO.File]::ReadAllBytes((Join-Path $root 'assets/icons/icon.ico'))
$bytes[$bytes.Length-1]=$bytes[$bytes.Length-1] -bxor 1
[IO.File]::WriteAllBytes($badIcon,$bytes)
$rejected=$false
try { & $verify -ExecutablePath $exe -IconPath $badIcon | Out-Null } catch {$rejected=$true}
if(-not $rejected){throw 'Mismatched ICO was accepted'}
$fake=Join-Path $ResultDir 'not-a-pe.exe'; [IO.File]::WriteAllText($fake,'not an executable')
$rejected=$false
try { & $verify -ExecutablePath $fake | Out-Null } catch {$rejected=$true}
if(-not $rejected){throw 'Invalid executable was accepted'}
$publish=[IO.File]::ReadAllText((Join-Path $root 'tools/Publish-Desktop.ps1'))
$brand=$publish.IndexOf('tests/BuildPipeline/Test-InstallerBrand.ps1')
$build=$publish.IndexOf('& dotnet publish ')
$verifyAt=$publish.IndexOf('tools/Verify-ExecutableIcon.ps1')
$shell=$publish.IndexOf('tests/BuildPipeline/Test-DesktopShellIconRefresh.ps1')
$zip=$publish.IndexOf('    Compress-Archive ')
$replace=$publish.IndexOf('Move-Item -LiteralPath (Assert-WorkspacePath $release)')
if(-not ($brand -ge 0 -and $brand -lt $build -and $build -lt $verifyAt -and $verifyAt -lt $shell -and $shell -lt $zip -and $zip -lt $replace)){throw 'Brand gates must precede packaging/release replacement'}
foreach($message in @('Brand configuration regression failed','Candidate executable icon verification failed','Shell icon refresh regression failed')){
 if(-not $publish.Contains("if (`$LASTEXITCODE -ne 0) { throw '$message' }")){throw "Missing failure gate: $message"}
}
foreach($file in @('tools/Publish-Desktop.ps1','tests/BuildPipeline/Test-InnoInstaller.ps1')){
 $tokens=$null;$errors=$null
 [Management.Automation.Language.Parser]::ParseFile((Join-Path $root $file),[ref]$tokens,[ref]$errors)|Out-Null
 if($errors.Count){throw "Invalid syntax: $file"}
}
if((Get-FileHash -LiteralPath $exe).Hash -ne $before){throw 'Installed executable changed'}
@{passed=$true;checks=@('installed nine frames','repeat invocation','mismatched ICO rejected','invalid PE rejected','gate ordering','nonzero exit rejection','publisher and installer syntax','installed EXE unchanged');releaseSha256=$before;scope='Direct verification and structural wiring checks; no APP rebuild or publisher execution'} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $ResultDir 'results.json') -Encoding UTF8
Write-Output 'BRAND_PUBLISH_GATE_TESTS=PASS'
