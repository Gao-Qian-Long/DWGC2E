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
    # Never terminate a user's active application to obtain screenshots.
    if (Get-Process DwgTranslator -ErrorAction SilentlyContinue) {
        throw 'APP_ALREADY_RUNNING: Close the application normally, or use -NoLaunch to review the existing window. No process was stopped.'
    }
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

$failed = @()
foreach ($page in $Pages) {
    $items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItemCondition)
    $target = $items | Where-Object { $_.Current.Name -eq $page } | Select-Object -First 1
    if (-not $target) { Write-Output "MISSING_NAV: $page"; $failed += "MISSING_NAV:$page"; continue }

    try {
        $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    } catch {
        $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    Start-Sleep -Seconds 2

    $safe = $page -replace '[^\w\u4e00-\u9fff]', ''
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\Capture-AppWindow.ps1" -Tag "$Tag-$safe" 2>&1
    $captureExit = $LASTEXITCODE
    $saved = $out | Where-Object { $_ -like 'captured:*' } | Select-Object -First 1
    # A missing PNG or a failed capture process must never be reported as a successfully visited page:
    # this tool exists to prove that every page was rendered and captured.
    if ($captureExit -ne 0 -or -not $saved) {
        $failed += "CAPTURE_FAILED:$page(exit=$captureExit)"
        Write-Output "CAPTURE_FAILED: $page  exit=$captureExit"
    } else {
        Write-Output "PAGE=$page  $saved"
    }
}
if ($failed.Count) { Write-Output ("CAPTURE_ERRORS=" + ($failed -join ',')); exit 1 }
Write-Output "CAPTURE_OK=$($Pages.Count)"
exit 0
