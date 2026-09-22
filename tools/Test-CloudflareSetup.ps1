# Purpose: LIVE endpoint checks and Wrangler secret-NAME inventory; requires approved Cloudflare access.
# Input: WorkerUrl, WorkerName. Output: diagnostics; no secret values requested.
# Usage: powershell -NoProfile -File tools/Test-CloudflareSetup.ps1 -WorkerUrl <test-api> -WorkerName <test-worker>
# QLCAD Cloudflare 配置检查（不读取 Secret 值、不发送邮件、不修改 D1）
param(
  [string]$WorkerUrl = 'https://dwgc2e-api.maplehousezz.workers.dev',
  [string]$WorkerName = 'dwgc2e-api'
)
$ErrorActionPreference = 'Stop'
$WorkerUrl = $WorkerUrl.TrimEnd('/')

function Assert-Status([string]$Path, [int]$Expected) {
  try {
    $response = Invoke-WebRequest -UseBasicParsing -Uri ($WorkerUrl + $Path) -Method Get -ErrorAction Stop
    $status = [int]$response.StatusCode
  } catch {
    if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
    else { throw "无法连接 $WorkerUrl：$($_.Exception.Message)" }
  }
  if ($status -ne $Expected) { throw "$Path 返回 $status，预期 $Expected" }
  Write-Host "[OK] $Path -> $status"
}

Write-Host "检查 Worker：$WorkerUrl"
Assert-Status '/' 200
Assert-Status '/v1/version' 200

$required = @('PASSWORD_PEPPER', 'JWT_SECRET', 'DEEPSEEK_API_KEY', 'BREVO_API_KEY')
$workerDir = Join-Path $PSScriptRoot '..\cf-worker'
Push-Location $workerDir
try {
  $secretJson = npx wrangler secret list --name $WorkerName | Out-String
} finally {
  Pop-Location
}
try {
  $secrets = $secretJson | ConvertFrom-Json
} catch {
  throw "无法读取 Wrangler Secret 名称列表，请先执行 npx wrangler login。输出：$secretJson"
}
$secretNames = @($secrets | ForEach-Object { $_.name })
foreach ($name in $required) {
  if ($secretNames -notcontains $name) { throw "缺少 Worker Secret：$name（脚本不会读取 Secret 值）" }
  Write-Host "[OK] Secret $name 已存在"
}

Write-Host 'Cloudflare 配置基础检查完成。'
Write-Host '注意：此脚本不能替代真实邮箱、登录、额度和翻译链路测试。'
