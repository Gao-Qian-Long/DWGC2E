param(
 [Parameter(Mandatory=$true)][string]$CadExe,
 [Parameter(Mandatory=$true)][string]$EvidenceRoot,
 [string]$PluginPath = (Join-Path $PSScriptRoot '../../release/CadPlugin/DwgTranslator.Cad.dll')
)
$ErrorActionPreference='Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
$CadExe=(Resolve-Path -LiteralPath $CadExe).Path
$PluginPath=(Resolve-Path -LiteralPath $PluginPath).Path
$root=[IO.Path]::GetFullPath($EvidenceRoot)
# Never reuse a fixture directory, overwrite user drawings, or touch another CAD process.
if(Test-Path -LiteralPath $root){throw 'Choose a new, empty evidence directory.'}
New-Item -ItemType Directory -Path $root | Out-Null
function CadPath([string]$p){'"'+$p.Replace('\','/')+'"'}
function Run-Cad([string]$name,[string[]]$commands){
 $script=Join-Path $root ($name+'.scr')
 @('FILEDIA','0','_.NETLOAD',(CadPath $PluginPath))+$commands+@('_.QUIT','_N','') | Set-Content -LiteralPath $script -Encoding utf8
 $p=Start-Process -FilePath $CadExe -ArgumentList @('/nologo','/b',(CadPath $script)) -WorkingDirectory $root -WindowStyle Hidden -PassThru
 $identity=@{Pid=$p.Id;StartTimeUtc=$p.StartTime.ToUniversalTime().ToString('o');Exe=$CadExe;Script=$script}
 $identity | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root ($name+'-process.json'))
 if(-not $p.WaitForExit(90000)){
  # Preserve a live process on timeout. A timeout must never trigger a second launch.
  throw "Owned CAD process $($p.Id) still running; inspect it before retrying."
 }
 if($p.ExitCode -ne 0){throw "CAD exited with $($p.ExitCode)"}
}
Run-Cad 'seed' @('DWGLAYOUTREGRESSION',(CadPath $root))
$source=Join-Path $root 'synthetic-source.dwg'
$seedReport=Get-Content -LiteralPath (Join-Path $root 'synthetic.report')
if($seedReport -notcontains 'WRITEBACK=6/6'){throw 'Synthetic writeback did not complete.'}
$rows=@($seedReport | Where-Object {$_ -match '^HANDLE='})
if($rows.Count -ne 6 -or @($rows | Where-Object {$_ -notmatch '\|CONTENT=True$'}).Count){throw 'Synthetic text content mismatch.'}
$handles=@($rows | ForEach-Object {($_ -split '\|')[0].Substring(7)})
$entities=@(for($i=0;$i -lt 6;$i++){@{Handle=$handles[$i];RawText='阀门反馈';PlainText='阀门反馈';TranslatedText='Dust removal valve closed position feedback';Height=3.5;OriginalHeight=3.5;MTextRectangleWidth=$(if($i%2){30}else{0});Status=2}})
$sourceHash=(Get-FileHash -LiteralPath $source).Hash
$output=Join-Path $root 'command-output.dwg'
$guarded=Join-Path $root 'existing-output.dwg'
Copy-Item -LiteralPath $source -Destination $guarded
$guardedHash=(Get-FileHash -LiteralPath $guarded).Hash
$cases=@(
 @{Name='normal';Source=$source;Output=$output;Expected='success';Success=6;Failed=0},
 @{Name='existing-output';Source=$source;Output=$guarded;Expected='failed';Success=0;Failed=6},
 @{Name='same-source';Source=$source;Output=$source;Expected='failed';Success=0;Failed=6},
 @{Name='missing-source';Source=(Join-Path $root 'missing.dwg');Output=(Join-Path $root 'must-not-exist.dwg');Expected='source_missing';Success=0;Failed=0}
)
$commands=@()
foreach($case in $cases){
 $config=Join-Path $root ($case.Name+'.json');$done=Join-Path $root ($case.Name+'.done')
 @{SourceDwgPath=$case.Source;OutputDwgPath=$case.Output;OverwriteExisting=$false;TargetIsCjk=$false;Entities=$entities;SessionId=$case.Name;DoneSignalPath=$done} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $config -Encoding utf8
 $commands+=@('DwgTranslateWrite',(CadPath $config))
}
Run-Cad 'writeback' $commands
$results=@(foreach($case in $cases){
 $signal=Get-Content -LiteralPath (Join-Path $root ($case.Name+'.done')) -Raw
 $parts=$signal -split '\|'
 if($parts[0] -ne $case.Expected -or $parts[1] -ne $case.Name -or [int]$parts[2] -ne $case.Success -or [int]$parts[3] -ne $case.Failed){throw "Unexpected $($case.Name) result: $signal"}
 @{Case=$case.Name;Signal=$signal;Passed=$true}
})
if((Get-FileHash -LiteralPath $source).Hash -ne $sourceHash){throw 'Source changed.'}
if((Get-FileHash -LiteralPath $guarded).Hash -ne $guardedHash){throw 'Existing output changed.'}
if(-not(Test-Path -LiteralPath $output) -or (Get-Item -LiteralPath $output).Length -eq 0){throw 'Output missing/empty.'}
if(Test-Path -LiteralPath (Join-Path $root 'must-not-exist.dwg')){throw 'Missing-source request created output.'}
if(@(Get-ChildItem -LiteralPath $root -Filter '*.tmp.dwg' -Force).Count){throw 'Temporary drawings leaked.'}
$audit=Join-Path $root 'layout-request.txt'
@($source,$output,($handles -join ',')) | Set-Content -LiteralPath $audit -Encoding utf8
Run-Cad 'layout' @('DWGVERIFYLAYOUT',(CadPath $audit))
$layout=Get-Content -LiteralPath ($audit+'.report')
if($layout -notcontains 'ENVELOPES_PASSED=6/6' -or $layout -notcontains 'NEW_TEXT_OVERLAPS=0'){throw 'Layout envelope/overlap acceptance failed.'}
@{Cases=$results;SourceHash=$sourceHash;SourcePreserved=$true;ExistingOutputPreserved=$true;TemporaryFilesClean=$true;PluginHash=(Get-FileHash -LiteralPath $PluginPath).Hash;Layout=$layout;LegacyStrictEnvelopeReport=$seedReport;Scope='GstarCAD synthetic DBText/MText 0/90/40 degrees; no APP pipeline, cloud translation, or other entities'} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'verification.json')
Write-Output 'PASS: four command outcomes, source/output protection, temporary cleanup, six content and six available-space checks. Legacy strict-envelope outcomes retained separately.'
