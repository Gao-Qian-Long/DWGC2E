namespace DwgTranslator.Core.Services;

public static class UpdateTrust
{
    public const string SigningKeyId = "rsa-sha256-0ae00361040667532278c72bfc83d4fbe7d8282c168a82555061aafab2b766b3";
    public const string PublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA6rtFK3IID9p1bChNk3+l
lK7GO3DQbGJJ8UXXYD03AfgSBdPy+a9qs1thX7atjnT3o+RjWJ5bjsXJEAlDe2LE
0hZkKpYKuRfWXEo3NTt4G9L0SPbW3O/uE0fEJiaiaWCfuxSThe/iGqBvCD3+hcbF
lscKxLRTOyU9xGSQzLMODHwO7qM30uhU3ooQns5mGdPj6HqkKQso3UWuw40V0Zxx
oCTgDSXGAvZ/B59vU2vUU+y3MJouG6VVuCeSaW8O6HMUjz95tQUw7cnmQYCdMh8e
b6TbK/qeA+SI49wkhGwuUaSLXiiJK0Iz+G1Y8+R7lICELiRlARSt6vGk5YDexbVs
YaPfhNGw6J/n8iflq1qWGPdXCEC3U5iT2Tw4Uq0OGCtvlQOzWg4S+fWX/tbnS0rb
ShsncJuwb2hG4yL0KFGJr7wKFHph6c0q71LjSITxSAYekMNOC3cjn/f0rKQ2MpoY
B+maTw+tq8sd/C5LWyRDxsiAdiLyvQprEZMd/LNmk4kBAgMBAAE=
-----END PUBLIC KEY-----
""";
}

public static class UpdateApplyScript
{
    public static string Write(string workDirectory)
    {
        Directory.CreateDirectory(workDirectory);
        var path = Path.Combine(workDirectory, "apply-update.ps1");
        File.WriteAllText(path, Script, new System.Text.UTF8Encoding(true));
        return path;
    }

    private const string Script = """
param(
 [Parameter(Mandatory=$true)][int]$AppPid,
 [Parameter(Mandatory=$true)][string]$Package,
 [Parameter(Mandatory=$true)][string]$PackageSha256,
 [Parameter(Mandatory=$true)][string]$Install,
 [Parameter(Mandatory=$true)][string]$Log
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Write-Log([string]$m){Add-Content -LiteralPath $Log -Value ((Get-Date -Format o)+' '+$m) -Encoding UTF8}
function Get-Sha256([string]$path){
 $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 $sha=[Security.Cryptography.SHA256]::Create()
 try{return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$sha.Dispose();$stream.Dispose()}
}
function Safe-Child([string]$root,[string]$path){
 $r=[IO.Path]::GetFullPath($root).TrimEnd('\')+'\';$p=[IO.Path]::GetFullPath($path)
 if(-not $p.StartsWith($r,[StringComparison]::OrdinalIgnoreCase)){throw '路径越界'}
 return $p
}
function Normalize-Relative([string]$value){
 if([string]::IsNullOrWhiteSpace($value)){throw '更新清单包含空路径'}
 $value=$value.Trim().Replace('/','\')
 if([IO.Path]::IsPathRooted($value)){throw '更新路径必须是相对路径'}
 $parts=@($value -split '\\')
 if($parts.Count -eq 0 -or ($parts|Where-Object{[string]::IsNullOrWhiteSpace($_) -or $_ -eq '.' -or $_ -eq '..'})){throw '更新路径无效或越界'}
 return ($parts -join '\')
}
function Assert-ManagedPath([string]$relative){
 $relative=Normalize-Relative $relative;$first=($relative -split '\\')[0];$ext=[IO.Path]::GetExtension($relative)
 if($first -ieq 'settings.json' -or $first -ieq 'data' -or $first -ieq 'projects' -or $first -ieq 'glossaries' -or $first -ieq 'update-managed-files.json' -or $ext -in @('.db','.sqlite','.sqlite3')){throw ('更新清单试图管理用户数据：'+$relative)}
 return $relative
}
function Extract-VerifiedZip([string]$zip,[string]$destination){
 $root=[IO.Path]::GetFullPath($destination).TrimEnd('\')+'\';$seen=@{};$total=[int64]0
 $archive=[IO.Compression.ZipFile]::OpenRead($zip)
 try{
  foreach($entry in $archive.Entries){
   if(($entry.ExternalAttributes -band 0xF0000000) -eq 0xA0000000){throw '更新包禁止包含符号链接'}
   $relative=Normalize-Relative $entry.FullName
   if($seen.ContainsKey($relative)){throw ('更新包包含重复路径：'+$relative)};$seen[$relative]=$true
   $dest=[IO.Path]::GetFullPath((Join-Path $destination $relative));if(-not $dest.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){throw '更新包路径越界'}
   if([string]::IsNullOrEmpty($entry.Name)){New-Item -ItemType Directory -Path $dest -Force|Out-Null;continue}
   $nextTotal=[decimal]$total+[decimal][int64]$entry.Length;if($nextTotal -gt 4GB){throw '更新包解压后体积超过安全上限'};$total=[int64]$nextTotal
   New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force|Out-Null
   $input=$entry.Open();$output=[IO.FileStream]::new($dest,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
   try{$input.CopyTo($output);$output.Flush($true)}finally{$output.Dispose();$input.Dispose()}
  }
 }finally{$archive.Dispose()}
}
function Backup-Destination([string]$dest){
 if($backed.ContainsKey($dest) -or $created.Contains($dest)){return}
 if(Test-Path -LiteralPath $dest){
  if((Get-Item -LiteralPath $dest -Force).PSIsContainer){throw ('更新只允许替换文件：'+$dest)}
  $relative=$dest.Substring($install.Length).TrimStart('\');$backup=Safe-Child $rollback (Join-Path $rollback $relative)
  New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force|Out-Null;Copy-Item -LiteralPath $dest -Destination $backup -Force
  $backed[$dest]=$backup
 }else{$created.Add($dest)|Out-Null}
}
$work=$null;$applyRoot=$null;$verifiedPackage=$null;$rollback=$null
$created=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$backed=@{};$tempFiles=New-Object System.Collections.Generic.List[string]
try{
 Write-Log '等待 APP 退出';try{Wait-Process -Id $AppPid -Timeout 600 -ErrorAction Stop}catch{throw 'APP 未能在十分钟内退出'}
 if(Get-Process acad,acadlt,gcad -ErrorAction SilentlyContinue){throw '检测到 CAD 仍在运行，取消更新'}
 $package=(Resolve-Path -LiteralPath $Package).Path;$install=(Resolve-Path -LiteralPath $Install).Path
 if($PackageSha256 -notmatch '^[0-9a-fA-F]{64}$'){throw '预期更新包哈希格式无效'}
 $work=(Split-Path -Parent $package);$applyRoot=Safe-Child $work (Join-Path $work ('apply-'+[Guid]::NewGuid().ToString('N')))
 $verifiedPackage=Safe-Child $work (Join-Path $work ('verified-'+[Guid]::NewGuid().ToString('N')+'.zip'))
 Copy-Item -LiteralPath $package -Destination $verifiedPackage
 $actualPackageHash=(Get-Sha256 $verifiedPackage)
 if($actualPackageHash -ne $PackageSha256){throw 'APP 退出后更新包 SHA-256 二次校验失败'}
 # 注意：本脚本运行在 Windows PowerShell 5.1（.NET Framework）上，无法加载 net8.0 的
 # DwgTranslator.Core，因此这里只做文件哈希复验；RSA 签名与密钥标识由 APP 侧完成。
 # 把签名复验下沉到应用时，必须使用 DwgTranslator.Updater（net8.0），不能靠本脚本。
 New-Item -ItemType Directory -Path $applyRoot|Out-Null;Extract-VerifiedZip $verifiedPackage $applyRoot
 $manifestPath=Join-Path $applyRoot 'update-manifest.json';if(-not(Test-Path -LiteralPath $manifestPath -PathType Leaf)){throw '更新包缺少 update-manifest.json'}
 $manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
 if($manifest.schemaVersion -ne 2 -or -not $manifest.files -or [string]::IsNullOrWhiteSpace([string]$manifest.version)){throw '更新清单架构无效'}
 $listed=@{}
 foreach($file in @($manifest.files)){
  $relative=Assert-ManagedPath ([string]$file.path);if($listed.ContainsKey($relative)){throw ('更新清单路径重复：'+$relative)}
  if([int64]$file.size -lt 0 -or [string]$file.sha256 -notmatch '^[0-9a-fA-F]{64}$'){throw ('更新清单文件记录无效：'+$relative)}
  $source=Safe-Child $applyRoot (Join-Path $applyRoot $relative);if(-not(Test-Path -LiteralPath $source -PathType Leaf)){throw ('更新包缺少清单文件：'+$relative)}
  if((Get-Item -LiteralPath $source).Length -ne [int64]$file.size -or (Get-Sha256 $source) -ne [string]$file.sha256){throw ('更新文件校验失败：'+$relative)}
  $listed[$relative]=$file
 }
 $actual=@(Get-ChildItem -LiteralPath $applyRoot -Recurse -File|ForEach-Object{$_.FullName.Substring($applyRoot.Length).TrimStart('\')}|Where-Object{$_ -ine 'update-manifest.json'})
 if($actual.Count -ne $listed.Count -or ($actual|Where-Object{-not $listed.ContainsKey($_)})){throw '更新包实际文件与完整受管文件列表不一致'}
 $exeRelative=Assert-ManagedPath ([string]$manifest.executablePath)
 if(-not $listed.ContainsKey($exeRelative) -or [string]$manifest.executableSha256 -notmatch '^[0-9a-fA-F]{64}$' -or [string]$manifest.executableSha256 -ne [string]$listed[$exeRelative].sha256){throw '更新清单主程序记录无效'}
 $newManaged=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase);foreach($relative in $listed.Keys){$newManaged.Add($relative)|Out-Null}
 $obsolete=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
 $receiptPath=Join-Path $install 'update-managed-files.json'
 if(Test-Path -LiteralPath $receiptPath){
  $oldReceipt=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json
  foreach($value in @($oldReceipt.files)){$relative=Assert-ManagedPath ([string]$value);if(-not $newManaged.Contains($relative)){$obsolete.Add($relative)|Out-Null}}
 }
 foreach($value in @($manifest.obsoletePaths)){$relative=Assert-ManagedPath ([string]$value);if($newManaged.Contains($relative)){throw ('待删除路径仍由新版本管理：'+$relative)};$obsolete.Add($relative)|Out-Null}
 $rollback=Safe-Child (Split-Path -Parent $install) (Join-Path (Split-Path -Parent $install) ((Split-Path -Leaf $install)+'.rollback'))
 if(Test-Path -LiteralPath $rollback){$resolvedRollback=(Resolve-Path -LiteralPath $rollback).Path;if($resolvedRollback -ne $rollback){throw '回滚目录解析异常'};Remove-Item -LiteralPath $rollback -Recurse -Force}
 New-Item -ItemType Directory -Path $rollback|Out-Null
 foreach($relative in $obsolete){
  $dest=Safe-Child $install (Join-Path $install $relative)
  if(Test-Path -LiteralPath $dest){Backup-Destination $dest;Remove-Item -LiteralPath $dest -Force}
 }
 foreach($relative in $listed.Keys){
  $source=Safe-Child $applyRoot (Join-Path $applyRoot $relative);$dest=Safe-Child $install (Join-Path $install $relative)
  New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force|Out-Null;Backup-Destination $dest
  $tmp=$dest+'.update-tmp';$tempFiles.Add($tmp);Copy-Item -LiteralPath $source -Destination $tmp -Force
  if((Get-Sha256 $tmp) -ne [string]$listed[$relative].sha256){throw ('安装临时文件校验失败：'+$relative)}
  Move-Item -LiteralPath $tmp -Destination $dest -Force;$tempFiles.Remove($tmp)|Out-Null
 }
 Backup-Destination $receiptPath
 $receiptTmp=$receiptPath+'.update-tmp';$tempFiles.Add($receiptTmp)
 [ordered]@{schemaVersion=1;version=[string]$manifest.version;files=@($listed.Keys|Sort-Object)}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $receiptTmp -Encoding UTF8
 Move-Item -LiteralPath $receiptTmp -Destination $receiptPath -Force;$tempFiles.Remove($receiptTmp)|Out-Null
 foreach($relative in $listed.Keys){$dest=Safe-Child $install (Join-Path $install $relative);if((Get-Sha256 $dest) -ne [string]$listed[$relative].sha256){throw ('安装后的文件哈希不匹配：'+$relative)}}
 $exe=Safe-Child $install (Join-Path $install $exeRelative)
 Write-Log '更新成功，正在重启';Start-Process -FilePath $exe -WorkingDirectory $install
 exit 0
}catch{
 Write-Log ('更新失败：'+$_.Exception.Message+'；位置：'+$_.InvocationInfo.PositionMessage.Replace([Environment]::NewLine,' ')+'；开始回滚')
 try{
  foreach($tmp in $tempFiles){if(Test-Path -LiteralPath $tmp){Remove-Item -LiteralPath $tmp -Force}}
  foreach($dest in $created){if(Test-Path -LiteralPath $dest){Remove-Item -LiteralPath $dest -Force}}
  foreach($dest in $backed.Keys){$backup=[string]$backed[$dest];if(Test-Path -LiteralPath $backup){New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force|Out-Null;Copy-Item -LiteralPath $backup -Destination $dest -Force}}
 }catch{Write-Log ('回滚失败：'+$_.Exception.Message)}
 exit 1
}finally{
 try{if($applyRoot -and (Test-Path -LiteralPath $applyRoot)){Remove-Item -LiteralPath (Safe-Child $work $applyRoot) -Recurse -Force}}catch{Write-Log ('清理解压目录失败：'+$_.Exception.Message)}
 try{if($verifiedPackage -and (Test-Path -LiteralPath $verifiedPackage)){Remove-Item -LiteralPath (Safe-Child $work $verifiedPackage) -Force}}catch{Write-Log ('清理验证包失败：'+$_.Exception.Message)}
}
""";
}
