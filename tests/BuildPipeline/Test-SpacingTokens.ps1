# 目的：锁定"间距必须走令牌"这条设计规则，防止各页面重新写回硬编码 Margin。
# 只读扫描 XAML，不构建、不修改任何文件。
[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$viewsRoot = Join-Path $root 'src\DwgTranslator.App\Views'
# 基线：迁移完成时的字面量数量。允许持平或下降，不允许增长。
# 增长意味着又出现了新的硬编码间距——请改用 Spacing.* 令牌。
# 2026-09-19：收紧到当前实测值 48（原 85 是布局迁移走到一半时的水位）。
$literalBudget = 48
$failures = New-Object System.Collections.Generic.List[string]
if (-not (Test-Path -LiteralPath $viewsRoot)) { throw "找不到视图目录：$viewsRoot" }
$files = Get-ChildItem -LiteralPath $viewsRoot -Recurse -Filter *.xaml -File
$totalTokens = 0; $totalLiterals = 0; $offenders = @()
foreach ($file in $files) {
    $text = [IO.File]::ReadAllText($file.FullName)
    $tokens = ([regex]::Matches($text, 'Margin="\{DynamicResource Spacing\.')).Count
    $literals = ([regex]::Matches($text, 'Margin="-?\d')).Count
    $totalTokens += $tokens; $totalLiterals += $literals
    if ($literals -gt 0) { $offenders += [pscustomobject]@{ Name = $file.Name; Literals = $literals } }
    if ($tokens -eq 0 -and $literals -eq 0) { continue }
    $coverage = if ($tokens + $literals -gt 0) { [math]::Round(100.0 * $tokens / ($tokens + $literals)) } else { 100 }
    Write-Host ("  {0,-32} 令牌={1,-4} 字面量={2,-4} 覆盖={3}%" -f $file.Name, $tokens, $literals, $coverage)
}
$total = $totalTokens + $totalLiterals
$pct = if ($total -gt 0) { [math]::Round(100.0 * $totalTokens / $total) } else { 100 }
Write-Host ""
Write-Host ("间距令牌覆盖：{0}%  （令牌 {1} / 字面量 {2}）" -f $pct, $totalTokens, $totalLiterals)
if ($totalLiterals -gt $literalBudget) {
    $failures.Add("硬编码 Margin 数量 $totalLiterals 超过基线 $literalBudget；新增间距请使用 Spacing.* 令牌")
}
if ($pct -lt 75) { $failures.Add("间距令牌覆盖率 $pct% 低于下限 75%") }
if ($failures.Count -gt 0) {
    foreach ($item in $failures) { Write-Host "FAIL $item" -ForegroundColor Red }
    Write-Host "SPACING_TOKENS=FAIL ($($failures.Count) 项)"
    exit 1
}
Write-Host "SPACING_TOKENS=PASS (字面量 $totalLiterals <= 基线 $literalBudget；覆盖 $pct%)"
exit 0
