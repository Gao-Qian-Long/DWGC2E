[CmdletBinding()]
param(
    [string]$BaseUrl = "https://api.cad.pocketter.dpdns.org",
    [string]$WebsiteOrigin = "https://cad.pocketter.dpdns.org"
)

$ErrorActionPreference = "Stop"
$BaseUrl = $BaseUrl.TrimEnd('/')

function Get-Json($Uri, $Method = "GET", $Headers = @{}) {
    $response = Invoke-WebRequest -Uri $Uri -Method $Method -Headers $Headers -UseBasicParsing
    [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Body = $response.Content }
}

Write-Host "检查 Worker: $BaseUrl" -ForegroundColor Cyan
$health = Get-Json "$BaseUrl/"
if ($health.StatusCode -ne 200) { throw "健康检查失败: HTTP $($health.StatusCode)" }
$healthJson = $health.Body | ConvertFrom-Json
if ($healthJson.status -ne 'ok') { throw "Worker 未返回 status=ok" }
Write-Host "[OK] / 返回 status=ok" -ForegroundColor Green

$version = Get-Json "$BaseUrl/v1/version"
if ($version.StatusCode -ne 200) { throw "版本接口失败: HTTP $($version.StatusCode)" }
$versionJson = $version.Body | ConvertFrom-Json
if ($versionJson.download_url -eq $WebsiteOrigin) {
    Write-Warning "DOWNLOAD_URL 当前仍是官网地址，不是真实 EXE 下载地址。"
} elseif ([string]::IsNullOrWhiteSpace([string]$versionJson.download_url)) {
    Write-Host "[INFO] DOWNLOAD_URL 尚未配置；自动更新/下载入口暂不可用。" -ForegroundColor Yellow
} else {
    Write-Host "[OK] DOWNLOAD_URL=$($versionJson.download_url)" -ForegroundColor Green
}
Write-Host "[OK] /v1/version 返回 latest_version=$($versionJson.latest_version)" -ForegroundColor Green

$cors = Get-Json "$BaseUrl/" "OPTIONS" @{ Origin = $WebsiteOrigin }
$allowOrigin = $null
try { $allowOrigin = (Invoke-WebRequest -Uri "$BaseUrl/" -Method OPTIONS -Headers @{ Origin = $WebsiteOrigin } -UseBasicParsing).Headers['Access-Control-Allow-Origin'] } catch { }
if ($allowOrigin -and $allowOrigin -ne $WebsiteOrigin) { Write-Warning "CORS 返回值不是官网域名: $allowOrigin" }
else { Write-Host "[OK] 已发送官网 Origin 的 CORS 预检检查" -ForegroundColor Green }

Write-Host "基础检查完成。注册、登录、邮箱和翻译额度仍需使用测试账号进行真实联调。" -ForegroundColor Cyan
