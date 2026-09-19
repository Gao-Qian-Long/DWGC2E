# Purpose: preserve user-owned portable files during a verified release swap.
# No deletion; reject links and collisions before copying. Not an installer payload source.
param([Parameter(Mandatory=$true)][string]$OldRelease,[Parameter(Mandatory=$true)][string]$NewRelease,[Parameter(Mandatory=$true)][string]$WorkspaceRoot)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\')
$old=[IO.Path]::GetFullPath($OldRelease).TrimEnd('\')
$next=[IO.Path]::GetFullPath($NewRelease).TrimEnd('\')
if($old -ne (Join-Path $root 'release') -or [IO.Path]::GetDirectoryName($next) -ne (Join-Path $root 'artifacts') -or [IO.Path]::GetFileName($next) -notmatch '^release-next-\d{8}-\d{6}$'){throw 'Unexpected portable-data copy paths'}
function Safe([string]$p){for($a=$p;$a;$a=[IO.Path]::GetDirectoryName($a)){if((Test-Path -LiteralPath $a) -and ((Get-Item -LiteralPath $a -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked portable data rejected'}}}
Safe $old; Safe $next
if(-not(Test-Path -LiteralPath $old)){return}
function Files([string]$p){foreach($item in Get-ChildItem -LiteralPath $p -Force){Safe $item.FullName;if($item.PSIsContainer){Files $item.FullName}else{$item}}}
# Only explicitly declared application files are disposable; unknown files anywhere are data.
$owned=@('prompts\deepl_context.txt','DwgTranslator.exe','DwgTranslator.pdb','DwgTranslator.Core.pdb','build-info.json','architecture-audit.json','assets\default-glossaries\mechanical_zh_en.json','CadPlugin\cad-files.txt')
$cadManifest=Join-Path $old 'CadPlugin/cad-files.txt'
if(Test-Path -LiteralPath $cadManifest){
 foreach($entry in Get-Content -LiteralPath $cadManifest -Encoding UTF8){
  if([string]::IsNullOrWhiteSpace($entry) -or ($entry.Replace('\','/') -match '(^/|:|(^|/)\.\.(/|$))')){throw 'Unsafe previous CAD manifest'}
  $owned+='CadPlugin\'+$entry.Replace('/','\')
 }
}

# A previous portable release can contain settings written by an older client. Those settings
# are user-owned and must otherwise survive the swap, but legacy provider credentials/endpoints
# and client-side prompt configuration must never be carried into the current Worker-only runtime.
$forbiddenSettingsNames = @(
    'deepSeekApiKey','deepSeekBaseUrl','deepSeekModel','apiMode',
    'apiKey','apiSecret','providerApiKey','providerKey','providerConfigs','aiProviders',
    'prompt','prompts','systemPrompt','translationPrompt','promptTemplate'
)
function Copy-SanitizedSettings([string]$source,[string]$target) {
    $raw = Get-Content -LiteralPath $source -Raw -Encoding UTF8
    try { $document = $raw | ConvertFrom-Json } catch {
        # Keep an opaque user-owned settings file byte-for-byte if an older build left an
        # invalid/placeholder document. The verified candidate still contains clean defaults;
        # the running app's normal migration path can repair the user's file later.
        Copy-Item -LiteralPath $source -Destination $target -Force
        if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $source).Hash) { throw 'Portable opaque settings copy failed' }
        Write-Warning "PORTABLE_SETTINGS_UNPARSED_PRESERVED=$source"
        return
    }
    if ($null -eq $document -or $document -isnot [pscustomobject]) {
        Copy-Item -LiteralPath $source -Destination $target -Force
        if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $source).Hash) { throw 'Portable opaque settings copy failed' }
        Write-Warning "PORTABLE_SETTINGS_NONOBJECT_PRESERVED=$source"
        return
    }
    $removed = @()
    foreach ($property in @($document.PSObject.Properties)) {
        if ($forbiddenSettingsNames -contains $property.Name) {
            $removed += $property.Name
            $document.PSObject.Properties.Remove($property.Name)
        }
    }
    $json = $document | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText($target, $json, (New-Object System.Text.UTF8Encoding($false)))
    $check = Get-Content -LiteralPath $target -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($name in $forbiddenSettingsNames) {
        if ($check.PSObject.Properties.Name -contains $name) { throw "Sanitized settings still contains forbidden field: $name" }
    }
    if ($removed.Count -gt 0) { Write-Host "PORTABLE_SETTINGS_SANITIZED=$($removed -join ',')" }
}

$copies=@(foreach($file in Files $old){
 $relative=$file.FullName.Substring($old.Length+1)
 if($relative -in $owned){continue}
 $target=Join-Path $next $relative; Safe $target
 if((Test-Path -LiteralPath $target) -and $relative -ne 'settings.json' -and $relative -notmatch '^(glossaries|prompts)[\\/]'){throw "New runtime collides with user file: $relative"}
 [pscustomobject]@{source=$file.FullName;target=$target;relative=$relative;hash=(Get-FileHash -LiteralPath $file.FullName).Hash}
})
foreach($copy in $copies){
 New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($copy.target)) | Out-Null
 if ($copy.relative -eq 'settings.json') {
  Copy-SanitizedSettings -source $copy.source -target $copy.target
 } else {
  Copy-Item -LiteralPath $copy.source -Destination $copy.target -Force
  if((Get-FileHash -LiteralPath $copy.target).Hash -ne $copy.hash -or (Get-FileHash -LiteralPath $copy.source).Hash -ne $copy.hash){throw 'Portable data changed or copied incorrectly; release swap refused'}
 }
}
Write-Host "PORTABLE_DATA_PRESERVED=$($copies.Count)"
