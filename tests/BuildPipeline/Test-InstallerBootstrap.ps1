# Purpose: run the shipping Chinese CMD entry with production PS scripts in Unicode/space paths.
# Input: fresh ResultDir beneath artifacts. Output: logs, JSON and retained synthetic installation.
# Uses NoLaunch/NoPrompt/NoShortcuts; no actual APP execution or desktop/start-menu writes.
param([string]$ResultDir)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\')
if(-not $ResultDir){$ResultDir=Join-Path $root ('artifacts/installer-safety-20260916-resumed/bootstrap-'+[guid]::NewGuid().ToString('N'))}
$ResultDir=[IO.Path]::GetFullPath($ResultDir).TrimEnd('\')
if(-not $ResultDir.StartsWith($root+'\artifacts\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output outside artifacts'}
$a=$ResultDir
while($a){if((Test-Path -LiteralPath $a) -and ((Get-Item -LiteralPath $a -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked output path'};$a=[IO.Path]::GetDirectoryName($a)}
if(Test-Path -LiteralPath $ResultDir){throw 'Use fresh result directory'}
$package=Join-Path $ResultDir '中文 安装包';$target=Join-Path $ResultDir '中文 安装目标'
New-Item -ItemType Directory -Path $package|Out-Null
$checks=New-Object 'System.Collections.Generic.List[string]'
function Check($v,[string]$label){if(-not $v){throw $label};$checks.Add($label);Write-Host "PASS $label"}
function Hash([string]$p){(Get-FileHash -LiteralPath $p).Hash}
function Put([string]$p,[string]$v){New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($p))|Out-Null;[IO.File]::WriteAllText($p,$v)}
foreach($name in @('DwgTranslator.exe','CadPlugin/DwgTranslator.Cad.dll','CadPlugin/DwgTranslator.Core.dll','CadPlugin/cad-platform.txt','prompts/deepl_context.txt')){Put (Join-Path $package $name) 'synthetic never execute'}
foreach($name in @('settings.json','glossaries/mechanical_zh_en.json','assets/default-glossaries/mechanical_zh_en.json')){Put (Join-Path $package $name) '{}'}
foreach($name in @('Install.ps1','InstallTransaction.ps1','Uninstall.ps1','安装.cmd')){Copy-Item -LiteralPath (Join-Path $root ('installer/'+$name)) -Destination (Join-Path $package $name)}
# Apply the exact production packaging conversion, rather than a test-only normalization.
$errors=$null;$tokens=$null;$packager=Join-Path $root 'tools/New-ReleasePackage.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile($packager,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Packager parse error'}
$fn=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Convert-File'},$true)
if(-not $fn){throw 'Production encoding function missing'}
. ([scriptblock]::Create($fn.Extent.Text))
$cmd=Join-Path $package '安装.cmd'
Convert-File $cmd ([Text.UTF8Encoding]::new($false))
$bytes=[IO.File]::ReadAllBytes($cmd)
Check (-not($bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)) 'CMD has no BOM before echo command'
Check ([IO.File]::ReadAllText($cmd) -notmatch '(?<!\r)\n') 'CMD uses CRLF after production normalization'
function Run-Bootstrap([string]$label){
 $psi=New-Object Diagnostics.ProcessStartInfo
 $psi.FileName=Join-Path $env:SystemRoot 'System32/cmd.exe'
 $psi.Arguments='/d /s /c ""'+$cmd+'" -TargetDir "'+$target+'" -NoLaunch -NoPrompt -NoShortcuts"'
 $psi.WorkingDirectory=$ResultDir;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
 $psi.RedirectStandardInput=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
 $psi.StandardOutputEncoding=[Text.UTF8Encoding]::new($false);$psi.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
 $p=New-Object Diagnostics.Process;$p.StartInfo=$psi
 try{
  [void]$p.Start();$stdout=$p.StandardOutput.ReadToEndAsync();$stderr=$p.StandardError.ReadToEndAsync()
  $p.StandardInput.WriteLine('');$p.StandardInput.Close()
  if(-not $p.WaitForExit(30000)){throw "Bootstrap still live: PID $($p.Id); inspect before retry"}
  $output=$stdout.Result+"`r`n"+$stderr.Result
  [IO.File]::WriteAllText((Join-Path $ResultDir ($label+'.log')),$output,[Text.UTF8Encoding]::new($true))
  return @{Exit=$p.ExitCode;Output=$output;ErrorOutput=$stderr.Result}
 }finally{$p.Dispose()}
}
$r=Run-Bootstrap 'success'
Check ($r.Exit -eq 0) 'Chinese-space bootstrap installs successfully from different working directory'
Check ($r.Output.Contains('安装程序')) 'Chinese bootstrap heading decodes correctly'
Check ([string]::IsNullOrWhiteSpace($r.ErrorOutput)) 'successful bootstrap produces no command or PowerShell errors'
Check ($r.Output.Contains('安装默认使用 D:\DWGC2E，没有 D 盘时使用 C:\DWGC2E。')) 'full default destination guidance is printed'
Check ($r.Output.Contains('安装目录需有写入权限；源码目录会被拒绝，请另选安装位置。')) 'full source protection guidance is printed'
Check ((Hash (Join-Path $package 'DwgTranslator.exe')) -eq (Hash (Join-Path $target 'DwgTranslator.exe'))) 'forwarded target contains exact package executable'
$m=Get-Content (Join-Path $target 'installation-manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
Check ($m.Root -eq $target -and @($m.Shortcuts).Count -eq 0) 'explicit target and NoShortcuts reach production installer'
$before=@{};Get-ChildItem -LiteralPath $target -Recurse -File|ForEach-Object{$before[$_.FullName]=Hash $_.FullName}
Rename-Item -LiteralPath (Join-Path $package 'DwgTranslator.exe') -NewName 'missing-executable.fixture'
$r=Run-Bootstrap 'missing-exe'
Check ($r.Exit -eq 2) 'bootstrap propagates installer failure exit code 2'
Check ([string]::IsNullOrWhiteSpace($r.ErrorOutput)) 'expected package rejection contains no CMD parsing errors'
Check ($r.Output.Contains('安装未完成')) 'failure displays Chinese message instead of reporting success'
$after=@{};Get-ChildItem -LiteralPath $target -Recurse -File|ForEach-Object{$after[$_.FullName]=Hash $_.FullName}
Check ($before.Count -eq $after.Count -and -not @($before.Keys|Where-Object{$before[$_] -ne $after[$_]}).Count) 'failed package leaves existing installation unchanged'
@{Passed=$checks.Count;Checks=@($checks);BootstrapSha256=Hash (Join-Path $root 'installer/安装.cmd');PackagerSha256=Hash $packager;Scope='real CMD and Install.ps1 with production encoding conversion; synthetic payload; no interactive Explorer double-click or physical console rendering'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $ResultDir 'results.json') -Encoding UTF8
Write-Host "INSTALLER_BOOTSTRAP=PASS ($($checks.Count) checks); $ResultDir"