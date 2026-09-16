# Purpose: fingerprint build inputs including uncommitted changes without copying their content.
# Output: deterministic file hashes; no secret values or runtime data.
param([string]$WorkspaceRoot=(Join-Path $PSScriptRoot '..'))
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $WorkspaceRoot).Path.TrimEnd('\')
$files=New-Object 'System.Collections.Generic.List[object]'
function Visit([string]$directory){
 foreach($item in Get-ChildItem -LiteralPath $directory -Force){
  if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Linked build input rejected: $($item.Name)"}
  if($item.PSIsContainer){if($item.Name -notin @('bin','obj','node_modules','.git')){Visit $item.FullName};continue}
  if($item.Name -match '^\.env|^\.dev\.vars' -or $item.Extension -in @('.log','.tmp','.cache')){continue}
  $files.Add([pscustomobject]@{path=$item.FullName.Substring($root.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash})
 }
}
foreach($name in @('src','assets','installer','tools','tests')){Visit (Join-Path $root $name)}
foreach($name in @('Directory.Build.props','Directory.Build.props.user','DwgTranslator.sln','settings.json.example')){
 $file=Join-Path $root $name
 if(Test-Path -LiteralPath $file){if((Get-Item -LiteralPath $file).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked build configuration rejected'};$files.Add([pscustomobject]@{path=$name;sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash})}
}
$files.ToArray() | Sort-Object path
