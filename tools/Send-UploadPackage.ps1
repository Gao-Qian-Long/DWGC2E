# 把一次完整交付产出的"网盘三件套"投递到 upload\：
#   <版本>-...-Setup.exe / SHA256SUMS.txt / 开始使用.txt
#
# 来源只认交付记录，不猜：
#   1) artifacts/current-release.json 的 publicDirectory（最终交付登记的投递目录）；
#   2) 回退：最新的 artifacts/delivery-*\installer\public；
#   3) 再回退：最新的 artifacts/publish-*\installer\public。
# 绝不从个人 release\ 目录取文件 —— 那里是本机运行版本，不是发布产物。
#
# 安全约定 AGENTS.md「受保护路径」）：
#   * upload\ 是用户准备上传网盘的暂存目录，本脚本**只增不删**：不删除任何已有文件，
#     也不覆盖不同名的文件；同名且内容一致时跳过。
#   * -DryRun 只打印将要复制的清单，不做任何写入。
[CmdletBinding()]
param(
    [switch]$DryRun,
    [string]$UploadDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $UploadDirectory) { $UploadDirectory = Join-Path $root 'upload' }
$uploadDir = [IO.Path]::GetFullPath($UploadDirectory)

function Write-Step([string]$Message) { Write-Host $Message }

function Resolve-SourceDirectory {
    param([string]$Root)
    $manifest = Join-Path $Root 'artifacts/current-release.json'
    if (Test-Path -LiteralPath $manifest) {
        try {
            $json = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($json.publicDirectory) {
                $candidate = [IO.Path]::GetFullPath((if ([IO.Path]::IsPathRooted($json.publicDirectory)) { $json.publicDirectory } else { Join-Path $Root $json.publicDirectory }))
                if (Test-Path -LiteralPath (Join-Path $candidate 'SHA256SUMS.txt')) { return $candidate }
            }
        } catch {
            Write-Step "提示：无法读取 $manifest（$($_.Exception.Message)），改用交付目录回退查找。"
        }
    }
    foreach ($pattern in @('artifacts/delivery-*/installer/public', 'artifacts/publish-*/installer/public')) {
        $hit = Get-ChildItem -Path (Join-Path $Root $pattern) -Directory -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'SHA256SUMS.txt') } |
            Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

$source = Resolve-SourceDirectory -Root $root
if (-not $source) {
    throw '找不到交付产物目录（既没有 current-release.json 的 publicDirectory，也没有 delivery-*/installer/public）。请先运行 build-release.bat。'
}
Write-Step "交付来源：$source"

$payload = @()
$setup = Get-ChildItem -LiteralPath $source -File -Filter '*-Setup.exe' | Select-Object -First 1
if (-not $setup) { throw "交付目录里没有 *-Setup.exe：$source" }
$payload += $setup
foreach ($name in @('SHA256SUMS.txt', '开始使用.txt')) {
    $f = Join-Path $source $name
    if (Test-Path -LiteralPath $f) { $payload += (Get-Item -LiteralPath $f) }
    else { Write-Step "提示：交付目录缺少 $name，跳过（它通常由打包步骤生成）。" }
}

if (-not $DryRun -and -not (Test-Path -LiteralPath $uploadDir)) {
    New-Item -ItemType Directory -Force -Path $uploadDir | Out-Null
}

$copied = 0; $skipped = 0
foreach ($item in $payload) {
    $target = Join-Path $uploadDir $item.Name
    if (Test-Path -LiteralPath $target) {
        $same = (Get-FileHash $item.FullName -Algorithm SHA256).Hash -eq (Get-FileHash $target -Algorithm SHA256).Hash
        if ($same) { Write-Step ("  跳过（已存在且内容一致）：" + $item.Name); $skipped++; continue }
    }
    if ($DryRun) {
        Write-Step ("  [DryRun] 将复制 " + $item.Name + "  ->  " + $uploadDir)
    } else {
        Copy-Item -LiteralPath $item.FullName -Destination $target -Force
        Write-Step ("  已复制 " + $item.Name)
    }
    $copied++
}

Write-Step ""
Write-Step ("投递结果：新增/更新 " + $copied + " 个，跳过 " + $skipped + " 个" + $(if ($DryRun) { "（DryRun，未写入）" } else { "" }))
if (-not $DryRun) {
    $others = Get-ChildItem -LiteralPath $uploadDir -File -Filter '*-Setup.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne $setup.Name }
    if ($others) {
        Write-Step ("提示：upload\ 里还有 " + @($others).Count + " 个历史安装包（本脚本不会删除）：")
        $others | ForEach-Object { Write-Step ("        " + $_.Name + "   " + [math]::Round($_.Length / 1MB, 1) + " MB") }
    }
}
