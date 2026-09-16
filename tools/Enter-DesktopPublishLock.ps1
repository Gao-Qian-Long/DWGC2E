# Usage: internal helper for Publish-Desktop.ps1; assign returned handle and Dispose it in finally.
# Purpose: hold an exclusive, workspace-local desktop publish lock until disposed.
# Input: absolute workspace root. Output: open FileStream; caller MUST dispose in finally.
# The marker may remain after exit; only the OS handle determines ownership.
param([Parameter(Mandatory=$true)][string]$WorkspaceRoot)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\')
$artifacts = Join-Path $root 'artifacts'
$lockPath = Join-Path $artifacts '.desktop-publish.lock'
foreach ($path in @($root, $artifacts, $lockPath)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Linked publish lock location is not allowed: $path"
    }
}
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
try {
    $handle = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
}
catch [IO.IOException] {
    throw "DESKTOP_PUBLISH_LOCK_UNAVAILABLE: Another publisher may be active, or the lock location is unavailable. No build or release update started. $($_.Exception.Message)"
}
try {
    $metadata = [Text.Encoding]::UTF8.GetBytes("pid=$PID`r`nstarted=$([DateTimeOffset]::Now.ToString('o'))`r`n")
    $handle.SetLength(0)
    $handle.Write($metadata, 0, $metadata.Length)
    $handle.Flush($true)
    Write-Output -NoEnumerate $handle
}
catch {
    $handle.Dispose()
    throw
}
