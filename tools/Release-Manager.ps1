# QLCAD 发布助手：统一产品版本、构建 Setup、验证网盘直链并发布后台版本元数据。
# 私钥与管理员密钥只在本机使用，不写入仓库、配置文件或日志。
[CmdletBinding()]
param([string]$WebsiteRoot)

$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Net.Http

$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if(-not $WebsiteRoot){
    $sibling=Join-Path (Split-Path -Parent $root) 'DWGC2E_Website'
    if(Test-Path -LiteralPath $sibling -PathType Container){$WebsiteRoot=$sibling}
}
$versionProps=Join-Path $root 'release\version.props'
$uploadDir=Join-Path $root 'upload'
$expectedKeyId='rsa-sha256-0ae00361040667532278c72bfc83d4fbe7d8282c168a82555061aafab2b766b3'

function Read-Version {
    [xml]$xml=Get-Content -LiteralPath $versionProps -Raw
    return [string]$xml.Project.PropertyGroup.ProductVersion
}
function Write-Version([string]$version,[string]$website){
    if($version -notmatch '^\d+\.\d+\.\d+$'){throw '版本号必须是 x.y.z，例如 2.1.2。'}
    [xml]$xml=Get-Content -LiteralPath $versionProps -Raw
    $xml.Project.PropertyGroup.ProductVersion=$version
    $settings=New-Object System.Xml.XmlWriterSettings
    $settings.Indent=$true;$settings.Encoding=New-Object System.Text.UTF8Encoding($false)
    $writer=[Xml.XmlWriter]::Create($versionProps,$settings);try{$xml.Save($writer)}finally{$writer.Dispose()}
    if($website){
        $siteConfig=Join-Path $website 'js\site-config.js'
        if(-not(Test-Path -LiteralPath $siteConfig -PathType Leaf)){throw "网站目录无效：未找到 $siteConfig"}
        $text=Get-Content -LiteralPath $siteConfig -Raw -Encoding UTF8
        $next=[regex]::Replace($text,'version:\s*"[^"]+"','version: "'+$version+'"',1)
        if($next -eq $text -and $text -notmatch ('version:\s*"'+[regex]::Escape($version)+'"')){throw '无法更新网站兜底版本号。'}
        [IO.File]::WriteAllText($siteConfig,$next,(New-Object Text.UTF8Encoding($false)))
    }
}
function Find-Setup([string]$version){
    if(-not(Test-Path -LiteralPath $uploadDir)){throw 'upload 目录不存在，请先构建。'}
    $hit=Get-ChildItem -LiteralPath $uploadDir -File -Filter ("QLCAD-"+$version+"-*-Setup.exe") |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if(-not $hit){throw "upload 中没有版本 $version 的 Setup.exe，请先构建。"}
    return $hit
}
function Invoke-Signer([string]$setup,[string]$key){
    if(-not(Test-Path -LiteralPath $key -PathType Leaf)){throw '请选择更新签名私钥 PEM 文件。'}
    $project=Join-Path $root 'tools\ReleaseSigner\ReleaseSigner.csproj'
    $output=@(& dotnet run --project $project -c Release -- $setup $key 2>&1)
    if($LASTEXITCODE -ne 0){throw ("签名工具失败："+($output -join [Environment]::NewLine))}
    $json=$output | Where-Object {$_ -match '^\s*\{.*\}\s*$'} | Select-Object -Last 1
    if(-not $json){throw '签名工具没有返回有效 JSON。'}
    $signed=$json|ConvertFrom-Json
    if($signed.signing_key_id -ne $expectedKeyId){throw "私钥与 APP 内置更新公钥不匹配。当前 Key ID：$($signed.signing_key_id)"}
    return $signed
}
function Test-DirectDownload([string]$url,[string]$expectedHash,[int64]$expectedSize){
    if($url -notmatch '^https://'){throw '主下载链接必须是 HTTPS。'}
    $temp=Join-Path ([IO.Path]::GetTempPath()) ('qlcad-release-'+[guid]::NewGuid().ToString('N')+'.exe')
    $client=[Net.Http.HttpClient]::new();$client.Timeout=[TimeSpan]::FromMinutes(30)
    try{
        $response=$client.GetAsync($url,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $response.EnsureSuccessStatusCode()
        $stream=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $file=[IO.FileStream]::new($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$stream.CopyTo($file);$file.Flush($true)}finally{$file.Dispose();$stream.Dispose()}
        $item=Get-Item -LiteralPath $temp
        if($item.Length -ne $expectedSize){throw "下载内容大小不匹配：远端 $($item.Length) B，本地 $expectedSize B。这个链接可能只是网盘分享页面，不是 EXE 直链。"}
        $hash=(Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash.ToLowerInvariant()
        if($hash -ne $expectedHash.ToLowerInvariant()){throw '远端下载内容 SHA-256 与本机构建安装包不一致。'}
        return $true
    }finally{$client.Dispose();Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue}
}
function Publish-Release([string]$apiBase,[string]$adminKey,[string]$version,[string]$mainUrl,[string]$backupUrl,[string]$notes,[string]$keyPath){
    if([string]::IsNullOrWhiteSpace($adminKey) -or $adminKey.Length -lt 32){throw '请输入后台管理员密钥。'}
    $setup=Find-Setup $version
    $signed=Invoke-Signer $setup.FullName $keyPath
    Test-DirectDownload $mainUrl $signed.package_sha256 ([int64]$signed.package_size)|Out-Null
    if($backupUrl -and $backupUrl -notmatch '^https://'){throw '备用下载链接必须是 HTTPS 或留空。'}
    $base=$apiBase.TrimEnd('/')
    $headers=@{Authorization='Bearer '+$adminKey}
    $settings=Invoke-RestMethod -Uri ($base+'/v1/admin/operations/settings') -Headers $headers -Method Get -TimeoutSec 30
    $release=$settings.items|Where-Object section -eq 'release'|Select-Object -First 1
    if(-not $release){throw '后台没有返回 release 配置，请先检查 Worker/D1 部署。'}
    $value=[ordered]@{
        latest_version=$version
        download_url=$mainUrl.Trim()
        backup_download_url=$backupUrl.Trim()
        release_notes=$notes
        package_size=[int64]$signed.package_size
        package_sha256=[string]$signed.package_sha256
        package_signature=[string]$signed.package_signature
        package_type='setup-exe'
        signing_key_id=[string]$signed.signing_key_id
    }
    $payload=[ordered]@{
        value=$value
        revision=[int]$release.revision
        request_id=[guid]::NewGuid().ToString()
        reason="发布 QLCAD $version（发布助手）"
    }|ConvertTo-Json -Depth 5
    $postHeaders=@{Authorization='Bearer '+$adminKey;'Content-Type'='application/json'}
    Invoke-RestMethod -Uri ($base+'/v1/admin/operations/settings/release') -Headers $postHeaders -Method Post -Body $payload -TimeoutSec 30|Out-Null
    $public=Invoke-RestMethod -Uri ($base+'/v1/version?current='+[uri]::EscapeDataString($version)) -Method Get -TimeoutSec 30
    if($public.latest_version -ne $version -or $public.download_url -ne $mainUrl.Trim()){throw '后台已写入，但公开版本回读不一致，请不要对外宣布发布成功。'}
    if($public.package_type -ne 'setup-exe' -or -not $public.package_sha256 -or $public.package_sha256.ToLowerInvariant() -ne $signed.package_sha256.ToLowerInvariant() -or $public.signing_key_id -ne $expectedKeyId){
        throw '公开更新接口没有完整回传 Setup 签名元数据。请先部署本次 Worker 代码，再重新发布；否则 APP 只能提示版本，不能自动安装。'
    }
    return $setup.FullName
}

$form=New-Object Windows.Forms.Form
$form.Text='QLCAD 发布助手'
$form.StartPosition='CenterScreen';$form.Size=New-Object Drawing.Size(860,720);$form.MinimumSize=New-Object Drawing.Size(760,650)
$form.Font=New-Object Drawing.Font('Microsoft YaHei UI',10)
$form.AutoScaleMode='Dpi'

$y=20
function Add-Label([string]$text,[int]$top){$l=New-Object Windows.Forms.Label;$l.Text=$text;$l.Left=24;$l.Top=$top+5;$l.Width=145;$form.Controls.Add($l)}
function Add-Text([int]$top,[int]$height=28){$t=New-Object Windows.Forms.TextBox;$t.Left=175;$t.Top=$top;$t.Width=630;$t.Height=$height;$form.Controls.Add($t);return $t}

Add-Label '目标版本' $y;$versionBox=Add-Text $y;$versionBox.Text=Read-Version;$y+=40
Add-Label '网站仓库目录' $y;$websiteBox=Add-Text $y;$websiteBox.Text=$WebsiteRoot;$y+=40
Add-Label 'API Base' $y;$apiBox=Add-Text $y;$apiBox.Text='https://cad.pocketter.dpdns.org/api';$y+=40
Add-Label '更新签名私钥' $y;$keyBox=Add-Text $y;$browse=New-Object Windows.Forms.Button;$browse.Text='选择…';$browse.Left=710;$browse.Top=$y;$browse.Width=95;$form.Controls.Add($browse);$keyBox.Width=525;$y+=40
Add-Label '管理员密钥' $y;$adminBox=Add-Text $y;$adminBox.UseSystemPasswordChar=$true;$y+=40
Add-Label '主下载直链' $y;$mainBox=Add-Text $y;$y+=40
Add-Label '备用下载链接' $y;$backupBox=Add-Text $y;$y+=40
Add-Label '更新说明' $y;$notesBox=Add-Text $y 90;$notesBox.Multiline=$true;$notesBox.ScrollBars='Vertical';$y+=105

$sync=New-Object Windows.Forms.Button;$sync.Text='1. 同步版本';$sync.Left=175;$sync.Top=$y;$sync.Width=135;$sync.Height=36;$form.Controls.Add($sync)
$build=New-Object Windows.Forms.Button;$build.Text='2. 构建并生成 upload';$build.Left=320;$build.Top=$y;$build.Width=185;$build.Height=36;$form.Controls.Add($build)
$verify=New-Object Windows.Forms.Button;$verify.Text='3. 验证下载直链';$verify.Left=515;$verify.Top=$y;$verify.Width=145;$verify.Height=36;$form.Controls.Add($verify)
$publish=New-Object Windows.Forms.Button;$publish.Text='4. 发布';$publish.Left=670;$publish.Top=$y;$publish.Width=135;$publish.Height=36;$form.Controls.Add($publish);$y+=50

$status=New-Object Windows.Forms.TextBox;$status.Left=24;$status.Top=$y;$status.Width=781;$status.Height=145;$status.Multiline=$true;$status.ReadOnly=$true;$status.ScrollBars='Vertical';$status.BackColor=[Drawing.Color]::White;$form.Controls.Add($status)
function Log([string]$m){$status.AppendText((Get-Date -Format 'HH:mm:ss')+'  '+$m+[Environment]::NewLine);$status.SelectionStart=$status.TextLength;$status.ScrollToCaret();[Windows.Forms.Application]::DoEvents()}
function Run-Ui([scriptblock]$action){
    foreach($b in @($sync,$build,$verify,$publish)){$b.Enabled=$false}
    try{&$action}catch{Log ('失败：'+$_.Exception.Message);[Windows.Forms.MessageBox]::Show($_.Exception.Message,'QLCAD 发布助手','OK','Error')|Out-Null}
    finally{foreach($b in @($sync,$build,$verify,$publish)){$b.Enabled=$true}}
}
$browse.Add_Click({$d=New-Object Windows.Forms.OpenFileDialog;$d.Filter='PEM 私钥 (*.pem;*.key)|*.pem;*.key|所有文件 (*.*)|*.*';if($d.ShowDialog() -eq 'OK'){$keyBox.Text=$d.FileName}})
$sync.Add_Click({Run-Ui {Write-Version $versionBox.Text.Trim() $websiteBox.Text.Trim();Log ('版本已统一为 '+$versionBox.Text.Trim()+'；APP 与网站兜底版本源已同步。')}})
$build.Add_Click({Run-Ui {
    $v=$versionBox.Text.Trim();Write-Version $v $websiteBox.Text.Trim();Log '开始完整 Release / Publish / Setup 验收，请勿关闭窗口。'
    & (Join-Path $root 'build-setup.bat')
    if($LASTEXITCODE -ne 0){throw "构建失败，退出码 $LASTEXITCODE"}
    $setup=Find-Setup $v;Log ('构建完成：'+$setup.FullName)
}})
$verify.Add_Click({Run-Ui {
    $v=$versionBox.Text.Trim();$setup=Find-Setup $v;$signed=Invoke-Signer $setup.FullName $keyBox.Text.Trim()
    Test-DirectDownload $mainBox.Text.Trim() $signed.package_sha256 ([int64]$signed.package_size)|Out-Null
    Log '主下载链接已验证：远端内容与本地 Setup.exe 完全一致，可用于 APP 自动更新。'
}})
$publish.Add_Click({Run-Ui {
    $v=$versionBox.Text.Trim()
    if([Windows.Forms.MessageBox]::Show("确认发布 QLCAD $v？\n后台版本、官网和 APP 更新查询将立即使用这份配置。",'确认发布','YesNo','Warning') -ne 'Yes'){Log '已取消发布。';return}
    $setup=Publish-Release $apiBox.Text.Trim() $adminBox.Text $v $mainBox.Text.Trim() $backupBox.Text.Trim() $notesBox.Text $keyBox.Text.Trim()
    Log ('发布成功并回读验证：'+$v);Log ('本地安装包：'+$setup)
    [Windows.Forms.MessageBox]::Show("QLCAD $v 已发布并完成公开接口回读验证。",'发布成功','OK','Information')|Out-Null
}})
Log '发布助手已就绪。顺序：同步版本 → 构建 → 上传 Setup 到网盘 → 填直链和说明 → 验证 → 发布。'
[void]$form.ShowDialog()
