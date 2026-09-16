# Usage: powershell -NoProfile -File tools/Test-DesktopArtifactCleanup.ps1
# Compatibility entry. Formal isolated regression lives under tests/BuildPipeline.
# Input: none; output: pass/fail, no production data changes.
& (Join-Path $PSScriptRoot '../tests/BuildPipeline/Test-DesktopArtifactCleanup.ps1')
