# Captures the running QLCAD window (or any window of that process) to a PNG so UI
# changes can be reviewed and iterated without a human in the loop.
param(
    [string]$ProcessName = 'DwgTranslator',
    [string]$OutDir = "$env:TEMP\uicap",
    [string]$Tag = 'shot'
)

Add-Type -AssemblyName System.Drawing
# UIA reports geometry in PHYSICAL pixels on a scaled display, while Win32 calls made from a
# DPI-unaware host (PowerShell) are virtualized: a 1920x1170 window at 150% reports 1280x780 and
# capturing that rectangle crops the right side off. UIA is therefore the source of truth here.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

if (-not ('WinCap' -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class WinCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

    public static List<IntPtr> TopLevelWindowsOf(int pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => {
            int owner; GetWindowThreadProcessId(h, out owner);
            if (owner == pid && IsWindowVisible(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }
    public static string TitleOf(IntPtr h) {
        var sb = new StringBuilder(GetWindowTextLength(h) + 2);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }
}
"@
}

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output "NO_WINDOW: $ProcessName is not running with a visible window"; exit 2 }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$windows = [WinCap]::TopLevelWindowsOf($proc.Id)
Write-Output "process=$($proc.Id) windows=$($windows.Count)"

function Get-PhysicalRect([IntPtr]$hwnd) {
    try {
        $element = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        if ($element) {
            $r = $element.Current.BoundingRectangle
            if ($r.Width -gt 1 -and $r.Height -gt 1) {
                return [pscustomobject]@{
                    Left = [int]$r.X; Top = [int]$r.Y
                    Width = [int]$r.Width; Height = [int]$r.Height
                }
            }
        }
    } catch { }
    $rect = New-Object WinCap+RECT
    [WinCap]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    return [pscustomobject]@{
        Left = $rect.Left; Top = $rect.Top
        Width = $rect.Right - $rect.Left; Height = $rect.Bottom - $rect.Top
    }
}

$index = 0
foreach ($hwnd in $windows) {
    $title = [WinCap]::TitleOf($hwnd)
    $geom = Get-PhysicalRect $hwnd
    $w = $geom.Width
    $h = $geom.Height
    if ($w -lt 80 -or $h -lt 80) { continue }

    [WinCap]::ShowWindow($hwnd, 9) | Out-Null      # SW_RESTORE
    [WinCap]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 450

    $geom = Get-PhysicalRect $hwnd
    $w = $geom.Width
    $h = $geom.Height
    # Popup windows (menus, tooltips) can report degenerate or screen-sized rectangles; a bitmap of
    # those sizes either throws or captures the whole desktop.
    if ($w -lt 40 -or $h -lt 24 -or $w -gt 4096 -or $h -gt 4096) { continue }

    $bmp = $null
    try { $bmp = New-Object System.Drawing.Bitmap($w, $h) } catch { continue }
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)

    # PrintWindow asks the window to render itself, which is the only reliable way to capture a
    # window that is off-screen or occluded. PW_RENDERFULLCONTENT (2) is required for
    # DirectX-composed content such as WPF.
    $hdc = $gfx.GetHdc()
    $ok = [WinCap]::PrintWindow($hwnd, $hdc, 2)
    $gfx.ReleaseHdc($hdc)
    if (-not $ok) {
        $gfx.Dispose()
        $bmp.Dispose()
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $gfx = [System.Drawing.Graphics]::FromImage($bmp)
        $gfx.CopyFromScreen($geom.Left, $geom.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    }
    $gfx.Dispose()

    $index++
    $name = "{0}-{1:d2}-{2}x{3}.png" -f $Tag, $index, $w, $h
    $path = Join-Path $OutDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "captured: $path   title='$title'"
}
if ($index -eq 0) { Write-Output 'NO_CAPTURE' ; exit 3 }
