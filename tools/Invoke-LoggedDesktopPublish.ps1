param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$PublishArguments
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$logDirectory = Join-Path $root 'artifacts/release-run-logs'
[IO.Directory]::CreateDirectory($logDirectory) | Out-Null

$started = Get-Date
$runId = $started.ToString('yyyyMMdd-HHmmss-fff') + "-$PID"
$logPath = Join-Path $logDirectory "release-$runId.log"
$publisher = Join-Path $PSScriptRoot 'Publish-Desktop.ps1'

function Write-LoggedLine([string]$Line) {
    $Line | Tee-Object -FilePath $logPath -Append
}

Write-LoggedLine "===== RELEASE RUN START: $($started.ToString('o')) ====="
Write-LoggedLine "Workspace: $root"
Write-LoggedLine "Publisher: $publisher"
Write-LoggedLine "Arguments: $($PublishArguments -join ' ')"

$nativeArguments = @(
    '-NoProfile'
    '-ExecutionPolicy'
    'Bypass'
    '-File'
    $publisher
) + $PublishArguments

$previousErrorActionPreference = $ErrorActionPreference
try {
    # Windows PowerShell surfaces native stderr as ErrorRecord objects. With Stop, one
    # diagnostic line aborts the pipeline before Tee-Object can persist the publisher's
    # actual error text or its final exit code.
    $ErrorActionPreference = 'Continue'
    & powershell.exe @nativeArguments 2>&1 | Tee-Object -FilePath $logPath -Append
    $publishExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}
if ($null -eq $publishExitCode) { $publishExitCode = 1 }

$finished = Get-Date
Write-LoggedLine "===== RELEASE RUN END: $($finished.ToString('o')); EXIT_CODE=$publishExitCode ====="
Write-Host "Log: $logPath"
exit $publishExitCode
