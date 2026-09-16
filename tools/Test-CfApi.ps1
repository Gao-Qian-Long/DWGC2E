# Purpose: read-only LIVE API diagnostics; requires network and Node.js, not an offline test.
# Input: BaseUrl, WebsiteOrigin, RequireDownload. Output: JSON checks and pass/fail.
# Usage: powershell -NoProfile -File tools/Test-CfApi.ps1 -BaseUrl <approved-test-api> -WebsiteOrigin <test-site>
[CmdletBinding()]
param(
    [string]$BaseUrl = "https://dwgc2e-api.maplehousezz.workers.dev",
    [string]$WebsiteOrigin = "https://cad.pocketter.dpdns.org",
    [switch]$RequireDownload
)
$ErrorActionPreference = 'Stop'
# Read-only requests only: no tokens, emails, accounts, payments or database writes.
$checkArgs = @((Join-Path $PSScriptRoot 'Test-CrossEndIntegration.mjs'), '--api-only', '--live', '--base-url', $BaseUrl, '--website-origin', $WebsiteOrigin)
if ($RequireDownload) { $checkArgs += '--require-download' }
& node @checkArgs
if ($LASTEXITCODE -ne 0) { throw '跨端 API 检查失败；未修改线上配置。请查看上方逐项结果。' }
Write-Host '只读检查完成；警告不等于发行验收通过。真实登录、邮件、付款及 CAD 回写均未测试。' -ForegroundColor Cyan
