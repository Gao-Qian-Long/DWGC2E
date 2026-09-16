# Purpose: evaluate architecture and resource contracts without building or restoring APP.
# Input: installed .NET SDK and local project files. Output: pass/fail.
# Usage: powershell -NoProfile -File tests/BuildPipeline/Test-ProjectStructure.ps1
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$count=0
function Check([bool]$ok,[string]$label){if(!$ok){throw $label};$script:count++;Write-Host "PASS $label"}
function Evaluate([string]$project,[string]$framework,[string]$platform=""){
 $args=@('msbuild',(Join-Path $root $project),'-nologo','-p:Configuration=Release','-getProperty:UseWPF,UseWindowsForms,TargetFramework,DefineConstants','-getItem:ProjectReference,Compile,Content,Resource,Reference,FrameworkReference')
 if($framework){$args += "-p:TargetFramework=$framework"}
 if($platform){$args += "-p:CadPlatform=$platform"}
 $json=(& dotnet @args | Out-String)
 if($LASTEXITCODE -ne 0){throw "Evaluation failed: $project/$framework"}
 return ($json | ConvertFrom-Json)
}
foreach($spec in @(
 @{Path='src/DwgTranslator.Core/DwgTranslator.Core.csproj';Tfm='net8.0';Core=$true},
 @{Path='src/DwgTranslator.Core/DwgTranslator.Core.csproj';Tfm='net48';Core=$true},
 @{Path='src/DwgTranslator.App/DwgTranslator.App.csproj';Tfm='net8.0-windows';Core=$false}
)){
 $data=Evaluate $spec.Path $spec.Tfm
 $label="$($spec.Path)/$($spec.Tfm)"
 $refs=@($data.Items.ProjectReference)
 Check (@($refs | Where-Object { $_.FullPath -match '[\\/]tests[\\/]' }).Count -eq 0) "$label has no test project dependency"
 Check (@($refs | Where-Object { !(Test-Path -LiteralPath $_.FullPath -PathType Leaf) }).Count -eq 0) "$label project references exist"
 $sources=@($data.Items.Compile)
 Check ($sources.Count -gt 0) "$label includes source files"
 Check (@($sources | Where-Object { !(Test-Path -LiteralPath $_.FullPath -PathType Leaf) }).Count -eq 0) "$label source files exist"
 Check (@($sources | Where-Object { $_.FullPath -match '[\\/](tests|artifacts|release)[\\/]' }).Count -eq 0) "$label does not compile tests or delivery trees"
 if($spec.Core){
  Check ($data.Properties.UseWPF -ne 'true' -and $data.Properties.UseWindowsForms -ne 'true') "$label does not enable desktop UI SDKs"
  Check (@($refs | Where-Object {$_.FullPath -match '[\\/]DwgTranslator.App[\\/]'}).Count -eq 0) "$label does not reference APP"
  $ui=@($data.Items.Reference)+@($data.Items.FrameworkReference)
  Check (@($ui | Where-Object {$_.Identity -match '^(PresentationFramework|PresentationCore|System.Windows.Forms|Microsoft.WindowsDesktop.App)(\.|,|$)'}).Count -eq 0) "$label has no direct WPF or WinForms assembly/framework reference"
 } else {
  $projectDir=Split-Path (Join-Path $root $spec.Path)
  foreach($contract in @(
   @{Kind='Content';Link='settings.json';Source='settings.json.example'},
   @{Kind='Content';Link='assets\default-glossaries\mechanical_zh_en.json';Source='assets/glossaries/mechanical_zh_en.json'},
   @{Kind='Content';Link='prompts\deepl_context.txt';Source='assets/prompts/deepl_context.txt'},
   @{Kind='Resource';Link='icon.ico';Source='assets/icons/icon.ico'}
  )) {
   $items=@($data.Items.($contract.Kind) | Where-Object {$_.Link -eq $contract.Link})
   Check ($items.Count -eq 1) "Unique resource contract: $($contract.Link)"
   Check ($items[0].FullPath -eq [IO.Path]::GetFullPath((Join-Path $root $contract.Source)) -and (Test-Path -LiteralPath $items[0].FullPath -PathType Leaf)) "Resource source exists and remains centralized: $($contract.Link)"
   if($contract.Kind -eq 'Content'){Check ($items[0].CopyToPublishDirectory -eq 'PreserveNewest') "Publish copy contract: $($contract.Link)"}
  }
 }
}
# Evaluate each supported host branch without building, resolving SDK DLLs or starting CAD.
foreach($hostSpec in @(
 @{Platform='GstarCAD';Tfm='net48';Sdk=@('GcCoreMgd','GcDbMgd','GcMgd')},
 @{Platform='AutoCAD';Tfm='net8.0';Sdk=@('AcCoreMgd','AcDbMgd','AcMgd','AcCui')}
)){
 $cad=Evaluate 'src/DwgTranslator.Cad/DwgTranslator.Cad.csproj' '' $hostSpec.Platform
 $label="CAD/$($hostSpec.Platform)"
 Check ($cad.Properties.TargetFramework -eq $hostSpec.Tfm) "$label chooses expected target framework without override"
 $cadRefs=@($cad.Items.ProjectReference)
 Check ($cadRefs.Count -eq 1 -and $cadRefs[0].FullPath -eq (Join-Path $root 'src/DwgTranslator.Core/DwgTranslator.Core.csproj')) "$label references Core only, never APP or tests"
 $sdkRefs=@($cad.Items.Reference|Where-Object {$_.Identity -match '^(Ac|Gc)(CoreMgd|DbMgd|Mgd|Cui)$'})
 Check (@(Compare-Object @($sdkRefs.Identity|Sort-Object) @($hostSpec.Sdk|Sort-Object)).Count -eq 0) "$label selects only its own host SDK references"
 Check (@($sdkRefs|Where-Object {$_.Private -ne 'False'}).Count -eq 0) "$label does not copy host SDK assemblies into delivery"
 $cadSources=@($cad.Items.Compile)
 Check ($cadSources.Count -gt 0 -and @($cadSources|Where-Object {!(Test-Path -LiteralPath $_.FullPath -PathType Leaf)}).Count -eq 0) "$label compile inputs exist"
 Check (@($cadSources|Where-Object {$_.FullPath -match '[\/](tests|artifacts|release)[\/]'}).Count -eq 0) "$label does not compile tests or delivery trees"
 $interactive=@($cadSources|Where-Object {$_.Identity -match 'DwgTranslatorCommands\.cs$|^Extraction[\/]'})
 if($hostSpec.Platform -eq 'GstarCAD'){
  Check ($interactive.Count -eq 0 -and $cad.Properties.DefineConstants.Split(';') -contains 'GSTARCAD') "$label retains compact CLR4 surface and GSTARCAD conditional compilation"
 } else {
  Check ($interactive.Count -gt 0 -and $cad.Properties.DefineConstants.Split(';') -notcontains 'GSTARCAD') "$label retains interactive extraction without GSTARCAD conditional compilation"
 }
}
Write-Host "PROJECT_STRUCTURE=PASS ($count checks; evaluation only, no Build/Restore/Publish)"
