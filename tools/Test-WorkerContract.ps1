# Worker 公共契约冒烟检查（不需要登录，不会发送邮件或修改 D1）
param([string]$BaseUrl = 'https://api.cad.pocketter.dpdns.org')
$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')
function Check([string]$Path, [int[]]$Expected) {
  try { $r = Invoke-WebRequest -UseBasicParsing -Uri ($BaseUrl + $Path) -Method Get -ErrorAction Stop; $status = [int]$r.StatusCode }
  catch { if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode } else { throw } }
  if ($Expected -notcontains $status) { throw "$Path 返回 $status，预期 $($Expected -join '/')" }
  Write-Host "[OK] $Path -> $status"
}
Check '/' @(200)
Check '/v1/version' @(200)
foreach ($path in @('/v1/profile','/v1/subscription','/v1/usage','/v1/devices','/v1/translate')) { Check $path @(401,405) }
Write-Host 'Worker 公共契约检查完成。'
