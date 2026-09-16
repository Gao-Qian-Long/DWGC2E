# Purpose: verify screenshot launch guards without opening UI or touching real processes.
# Input: repository screenshot scripts. Output: pass/fail; mocked process calls only.
# Usage: powershell -NoProfile -File tests/BuildPipeline/Test-CaptureLaunchSafety.ps1
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$count = 0
foreach ($name in @('Capture-AllPages.ps1', 'Capture-Dialogs.ps1')) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root "tools/$name"), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw "Parse failed: $name" }
    $kill = $ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -in @('Stop-Process','taskkill','taskkill.exe')
    }, $true)
    if ($kill.Count) { throw "Process termination is forbidden in capture tools: $name" }
    $count++
    $guard = @($ast.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Clauses[0].Item1.Extent.Text -eq '-not $NoLaunch'
    })
    if ($guard.Count -ne 1) { throw "Expected exactly one launch guard: $name" }
    $code = [scriptblock]::Create($guard[0].Extent.Text)
    foreach ($case in @(
        @{Running=$true;NoLaunch=$false;Throws=$true;Launches=0},
        @{Running=$true;NoLaunch=$true;Throws=$false;Launches=0},
        @{Running=$false;NoLaunch=$false;Throws=$false;Launches=1},
        @{Running=$false;NoLaunch=$true;Throws=$false;Launches=0}
    )) {
        & {
            param($case,$code)
            $script:running = $case.Running
            $script:launches = 0
            function Get-Process { param($Name,$ErrorAction) if($script:running){ [pscustomobject]@{Id=123} } }
            function Start-Process { param($FilePath) if($FilePath -ne 'mock-app.exe'){throw 'Unexpected executable'}; $script:launches++ }
            function Start-Sleep { param($Milliseconds) }
            $NoLaunch = $case.NoLaunch
            $ExePath = 'mock-app.exe'
            $failure = $null
            try { & $code } catch { $failure = $_ }
            if ($case.Throws) {
                if (!$failure -or $failure.Exception.Message -notlike 'APP_ALREADY_RUNNING:*') { throw 'Running app must be protected' }
            } elseif ($failure) { throw $failure }
            if ($script:launches -ne $case.Launches) { throw 'Unexpected launch count' }
        } $case $code
        $count++
    }
    Write-Host "PASS $name (no kill commands; four launch scenarios)"
}
Write-Host "CAPTURE_LAUNCH_SAFETY=PASS ($count checks; no real UI/process interaction)"
