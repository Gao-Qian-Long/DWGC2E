# Purpose: statically verify that every XAML Command binding and theme resource reference resolves.
# Input: workspace root (optional). Output: console report; exits 1 when unresolved references exist.
# Usage: powershell -NoProfile -File tests/BuildPipeline/Test-UiBindingIntegrity.ps1
# Rationale: a mistyped {Binding FooCommand} or {DynamicResource Brush.Missing} fails silently at
# runtime (the button simply does nothing, the brush simply does not apply), so it never shows up
# as a crash or a test failure unless something checks the names offline.
# $PSScriptRoot is empty while param defaults are evaluated under `powershell.exe -File`,
# which is exactly how Publish-Desktop.ps1 invokes this gate; resolve it after the param block.
param([string]$Root)
if (!$Root) { $Root = Join-Path $PSScriptRoot '../..' }
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Root)
$appDir = Join-Path $root 'src/DwgTranslator.App'
if (!(Test-Path -LiteralPath $appDir)) { throw "App project not found: $appDir" }

$xamlFiles = @(Get-ChildItem -LiteralPath $appDir -Recurse -File -Filter '*.xaml' |
    Where-Object { $_.FullName -notmatch '\\obj\\' } | Sort-Object FullName)
$csFiles = @(Get-ChildItem -LiteralPath $appDir -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | Sort-Object FullName)
if (!$xamlFiles.Count) { throw "No XAML found under $appDir" }

# ---- names the view models actually expose -------------------------------
$commands = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach ($file in $csFiles) {
    $text = [IO.File]::ReadAllText($file.FullName)
    # [RelayCommand] on a method generates <Name>Command; a trailing Async is stripped first.
    $pattern = '(?m)^\s*(?:\[RelayCommand[^\]]*\]\s*)+\s*(?:(?:public|private|internal|protected)\s+)?(?:static\s+)?(?:async\s+)?[\w<>\[\],\.\?]+\s+(\w+)\s*\('
    foreach ($m in [regex]::Matches($text, $pattern)) {
        $name = $m.Groups[1].Value
        if ($name.EndsWith('Async')) { $name = $name.Substring(0, $name.Length - 5) }
        [void]$commands.Add($name + 'Command')
    }
    # Hand-written command properties.
    $pattern = '(?m)\b(?:public|internal|protected)\s+(?:static\s+)?(?:ICommand|RelayCommand|AsyncRelayCommand|IRelayCommand|DelegateCommand)\s+(\w+)\s*(?:\{|=>)'
    foreach ($m in [regex]::Matches($text, $pattern)) { [void]$commands.Add($m.Groups[1].Value) }
}

# ---- resource keys defined by any XAML in the app ------------------------
$keys = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach ($file in $xamlFiles) {
    $text = [IO.File]::ReadAllText($file.FullName)
    foreach ($m in [regex]::Matches($text, 'x:Key\s*=\s*"([^"]+)"')) { [void]$keys.Add($m.Groups[1].Value) }
}

# ---- references from XAML -------------------------------------------------
$unresolvedCommands = New-Object 'System.Collections.Generic.List[string]'
$unresolvedKeys = New-Object 'System.Collections.Generic.List[string]'
$dottedPaths = 0
$bindingCount = 0
$keyRefCount = 0
foreach ($file in $xamlFiles) {
    $relative = $file.FullName.Substring($root.Length + 1)
    $lineNo = 0
    foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
        $lineNo++
        # Skip commented-out markup so a stale commented binding is not reported.
        if ($line -match '^\s*<!--') { continue }
        foreach ($m in [regex]::Matches($line, 'Command\s*=\s*"\{Binding\s+(?:Path\s*=\s*)?([A-Za-z_][\w\.]*)\s*[,}"]')) {
            $target = $m.Groups[1].Value
            # A dotted path (DataContext.XxxCommand) reaches the command through the DataContext's own
            # member set, so only its leaf is knowable statically - compare that leaf instead of
            # skipping, otherwise a mistyped dotted binding would pass the gate unnoticed.
            # Inline-source bindings never arrive here: Source=/ElementName=/RelativeSource= all sit
            # where the pattern requires a terminator, so they do not match at all.
            if ($target.Contains('.')) { $dottedPaths++; $target = $target.Substring($target.LastIndexOf('.') + 1) }
            $bindingCount++
            if (!$commands.Contains($target)) { $unresolvedCommands.Add("$relative`:$lineNo  Command={Binding $($m.Groups[1].Value)}") }
        }
        foreach ($m in [regex]::Matches($line, '\{(?:Dynamic|Static)Resource\s+([A-Za-z_][\w\.]*)\s*\}')) {
            $keyRefCount++
            $name = $m.Groups[1].Value
            if (!$keys.Contains($name)) { $unresolvedKeys.Add("$relative`:$lineNo  $($m.Groups[0].Value)") }
        }
    }
}

Write-Output "COMMAND_NAMES=$($commands.Count)"
Write-Output "COMMAND_BINDINGS=$bindingCount (dotted paths resolved by leaf: $dottedPaths, skipped: 0)"
Write-Output "RESOURCE_KEYS=$($keys.Count)"
Write-Output "RESOURCE_REFERENCES=$keyRefCount"
foreach ($item in $unresolvedCommands) { Write-Output "UNRESOLVED_COMMAND: $item" }
foreach ($item in $unresolvedKeys) { Write-Output "UNRESOLVED_RESOURCE: $item" }
$failed = $unresolvedCommands.Count + $unresolvedKeys.Count
if ($failed) {
    Write-Output "UI_BINDING_INTEGRITY=BROKEN ($failed unresolved)"
    exit 1
}
Write-Output 'UI_BINDING_INTEGRITY=OK'
exit 0
