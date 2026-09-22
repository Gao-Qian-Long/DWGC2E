# Purpose: manually capture a desktop window; requires a real interactive Windows session.
# Input: Name, OutputDirectory, optional WindowHandle. Output: screenshot in selected directory.
# Usage: powershell -NoProfile -File tools/Capture-DesktopReview.ps1 -Name settings -OutputDirectory artifacts/my-review
param(
    [Parameter(Mandatory)][ValidatePattern('^[^\\/:*?"<>|]+$')][string]$Name,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/正式版精修完成审核-20260915'),
    [long]$WindowHandle = 0
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('DesktopReviewCapture' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DesktopReviewCapture {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint id);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
}
"@
}
$release = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../release/QLCAD.exe'))
$processes = @(Get-Process QLCAD -ErrorAction SilentlyContinue | Where-Object Path -EQ $release)
if ($processes.Count -ne 1) { throw 'Expected exactly one installed release process.' }
if ($WindowHandle -eq 0) { $WindowHandle = $processes[0].MainWindowHandle }
$h = [IntPtr]$WindowHandle
[uint32]$ownerId = 0
[void][DesktopReviewCapture]::GetWindowThreadProcessId($h, [ref]$ownerId)
if ($ownerId -ne $processes[0].Id) { throw 'Window does not belong to installed release.' }
if ([DesktopReviewCapture]::IsIconic($h) -or -not [DesktopReviewCapture]::IsWindowVisible($h)) {
    throw 'Window is minimized or hidden. Restore it before capture; no invalid image was saved.'
}
$previousDpi = [DesktopReviewCapture]::SetThreadDpiAwarenessContext([IntPtr](-4))
$bitmap = $null; $graphics = $null
try {
    $r = New-Object DesktopReviewCapture+RECT
    if (-not [DesktopReviewCapture]::GetWindowRect($h, [ref]$r)) { throw 'Could not read window bounds.' }
    $width = $r.Right - $r.Left; $height = $r.Bottom - $r.Top
    if ($width -lt 480 -or $height -lt 280) { throw "Invalid review image size: $width x $height" }
    $bitmap = New-Object Drawing.Bitmap $width, $height
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { $ok = [DesktopReviewCapture]::PrintWindow($h, $dc, 2) }
    finally { $graphics.ReleaseHdc($dc) }
    if (-not $ok) { throw 'Window capture failed.' }
    $colors = [Collections.Generic.HashSet[int]]::new()
    for ($y = 8; $y -lt $height; $y += 17) {
        for ($x = 8; $x -lt $width; $x += 17) { [void]$colors.Add($bitmap.GetPixel($x, $y).ToArgb()) }
    }
    if ($colors.Count -lt 8) { throw 'Captured image appears blank; no image was saved.' }
    $directory = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $path = Join-Path $directory ($Name + '.png')
    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    [ordered]@{
        file = $path; capturedAt = (Get-Date -Format o); width = $width; height = $height
        windowDpi = [DesktopReviewCapture]::GetDpiForWindow($h)
        windowHandle = $WindowHandle; executable = $release
        sha256 = (Get-FileHash -LiteralPath $release -Algorithm SHA256).Hash
        method = 'PrintWindow, full native window; not a simulated DPI render'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory ($Name + '.capture.json')) -Encoding UTF8
    Write-Output $path
}
finally {
    if ($graphics) { $graphics.Dispose() }
    if ($bitmap) { $bitmap.Dispose() }
    [void][DesktopReviewCapture]::SetThreadDpiAwarenessContext($previousDpi)
}
