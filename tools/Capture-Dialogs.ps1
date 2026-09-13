# 逐个打开可点击进入的对话框并截图（设置 / 环境自检 / 帮助 / 术语库管理），
# 用于统一核对控件样式是否与浅色工程风一致。
param(
    [string]$ExePath = 'D:\DWGC2E\release\DwgTranslator.exe',
    [string]$Tag = 'dlg',
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

if (-not $NoLaunch) {
    Get-Process DwgTranslator -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    Start-Process $ExePath | Out-Null
}

$proc = $null
for ($i = 0; $i -lt 90; $i++) {
    Start-Sleep -Milliseconds 500
    $proc = Get-Process DwgTranslator -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($proc) { break }
}
if (-not $proc) { Write-Output 'NO_WINDOW'; exit 2 }
Start-Sleep -Seconds 2

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

function Invoke-ByName([string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $btn = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) |
        Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
    if ($btn) { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
    return $false
}

function Close-Dialogs {
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $pidCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    foreach ($w in $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)) {
        if ($w.Current.NativeWindowHandle -eq $proc.MainWindowHandle) { continue }
        $btnCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        $close = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond) |
            Where-Object { $_.Current.Name -in @('取消', '关闭', 'Close', 'Cancel') } | Select-Object -First 1
        if ($close) { try { $close.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { } }
    }
}

$cases = @(
    @{ Name = '设置对话框';      Open = { $root.SetFocus(); Start-Sleep -Milliseconds 300; [System.Windows.Forms.SendKeys]::SendWait('^,'); $true } },
    @{ Name = '术语库管理';      Open = { Invoke-ByName '管理术语库' } },
    @{ Name = '环境自检与安装';  Open = { Invoke-ByName '环境自检与安装' } },
    @{ Name = '帮助';            Open = { $root.SetFocus(); Start-Sleep -Milliseconds 300; [System.Windows.Forms.SendKeys]::SendWait('{F1}'); $true } }
)

foreach ($case in $cases) {
    & $case.Open | Out-Null
    Start-Sleep -Seconds 3
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\Capture-AppWindow.ps1" -Tag $Tag 2>&1
    $saved = ($out | Where-Object { $_ -like 'captured:*' } | Select-Object -First 1)
    Write-Output "DIALOG=$($case.Name)  $saved"
    Close-Dialogs
    Start-Sleep -Seconds 1
}
