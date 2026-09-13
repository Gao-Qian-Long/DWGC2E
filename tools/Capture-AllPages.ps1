# 五页截图巡回：启动（或复用）DWG Translator，用 UI 自动化逐个点开左侧导航并截图，
# 用于逐页核对与设计稿的复刻程度。
param(
    [string]$ExePath = 'D:\DWGC2E\release\DwgTranslator.exe',
    [string[]]$Pages = @('图纸翻译', '批量任务', '术语库', '会员中心', '设置'),
    [string]$Tag = 'pages',
    [int]$StartupTimeoutSeconds = 45,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

if (-not $NoLaunch) {
    Get-Process DwgTranslator -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    Start-Process $ExePath | Out-Null
}

$proc = $null
for ($i = 0; $i -lt ($StartupTimeoutSeconds * 2); $i++) {
    Start-Sleep -Milliseconds 500
    $proc = Get-Process DwgTranslator -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($proc) { break }
}
if (-not $proc) { Write-Output 'NO_WINDOW'; exit 2 }
Start-Sleep -Seconds 2

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$listItemCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)

foreach ($page in $Pages) {
    $items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItemCondition)
    $target = $items | Where-Object { $_.Current.Name -eq $page } | Select-Object -First 1
    if (-not $target) { Write-Output "MISSING_NAV: $page"; continue }

    try {
        $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    } catch {
        $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    Start-Sleep -Seconds 2

    $safe = $page -replace '[^\w\u4e00-\u9fff]', ''
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\Capture-AppWindow.ps1" -Tag "$Tag-$safe" 2>&1
    $saved = $out | Where-Object { $_ -like 'captured:*' } | Select-Object -First 1
    Write-Output "PAGE=$page  $saved"
}
