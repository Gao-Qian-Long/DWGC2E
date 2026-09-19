# Purpose: exact public runtime payload; never include development evidence or personal files.
# Input: verified candidate. Output: relative path, size and SHA256 for every installable file.
param([Parameter(Mandatory=$true)][string]$PublishDir, [string]$WorkspaceRoot)
$ErrorActionPreference='Stop'
$info = & (Join-Path $PSScriptRoot 'Assert-CleanPackageInput.ps1') -PublishDir $PublishDir -WorkspaceRoot $WorkspaceRoot
$dir=(Resolve-Path -LiteralPath $PublishDir).Path.TrimEnd('\')
$runtime=@('DwgTranslator.exe','settings.json','assets/default-glossaries/mechanical_zh_en.json','glossaries/mechanical_zh_en.json','CadPlugin/cad-files.txt')
$manifest=Join-Path $dir 'CadPlugin/cad-files.txt'
if(-not(Test-Path -LiteralPath $manifest -PathType Leaf)){throw 'Public installer requires an explicit CAD payload manifest.'}
foreach($name in Get-Content -LiteralPath $manifest -Encoding UTF8){
 $name=$name.Replace('\','/')
 # The manifest is the only source of payload paths, so traversal and absolute entries are rejected
 # here instead of relying on the current caller chain (Verify-CadPluginPackage.ps1) to have rejected
 # them first. Join-Path would otherwise silently replace the left side with an absolute entry.
 if([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::IsPathRooted($name)){throw "Unsafe CAD payload entry: $name"}
 foreach($part in @($name.Split('/'))){
  if(-not $part -or $part -in @('.','..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0){throw "Unsafe CAD payload entry: $name"}
 }
 if($name -ne 'cad-platform.txt' -and $name -notmatch '\.(dll|json|config|resources)$'){throw "Non-runtime CAD payload entry: $name"}
 $runtime += 'CadPlugin/'+$name
}
# These are private build evidence, not installed or shared with end users.
$private=@('build-info.json','architecture-audit.json','DwgTranslator.pdb','DwgTranslator.Core.pdb')
foreach($file in Get-ChildItem -LiteralPath $dir -File -Recurse -Force){
 $relative=$file.FullName.Substring($dir.Length+1).Replace('\','/')
 if($relative -notin $runtime -and $relative -notin $private){throw "Unreviewed file in candidate: $relative"}
}
foreach($name in $runtime | Sort-Object -Unique){
 $file=Get-Item -LiteralPath (Join-Path $dir $name)
 [pscustomobject]@{path=$name;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
}
