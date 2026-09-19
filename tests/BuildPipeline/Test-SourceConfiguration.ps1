# Purpose: enforce safe default configuration and Git exclusions without reading local secrets.
# Input: none. Output: pass/fail only; never prints credential values or calls external services.
# Run: powershell -NoProfile -File tests/BuildPipeline/Test-SourceConfiguration.ps1
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$checks=0
function Check([bool]$Condition,[string]$Label){
    if(-not $Condition){throw $Label}
    $script:checks++;Write-Host "PASS $Label"
}
function Assert-EmptySecrets($Object,[string]$Path='default'){
    if($null -eq $Object -or $Object -is [string] -or $Object.GetType().IsValueType){return}
    if($Object -is [array]){foreach($item in $Object){Assert-EmptySecrets $item $Path};return}
    foreach($property in $Object.PSObject.Properties){
        $name=$property.Name;$value=$property.Value
        if($name -match '(?i)(api.?key|api.?secret|password|passwd|access.?token|refresh.?token|auth.?token|private.?key|client.?secret|pepper|credential)'){
            if($null -ne $value -and -not ($value -is [string] -and [string]::IsNullOrWhiteSpace($value))){
                throw "Nonempty credential field in $Path.$name (value omitted)"
            }
        }
        Assert-EmptySecrets $value ($Path+'.'+$name)
    }
}
$template=Get-Content -LiteralPath (Join-Path $root 'settings.json.example') -Raw -Encoding UTF8|ConvertFrom-Json
Assert-EmptySecrets $template
foreach($legacy in @('deepSeekApiKey','deepSeekBaseUrl','deepSeekModel','apiMode')){Check ($null -eq $template.PSObject.Properties[$legacy]) ("Default template omits client-side AI field: "+$legacy)}
# Negative control proves nonempty nested credentials cannot silently pass the gate.
$rejected=$false
try{Assert-EmptySecrets ([pscustomobject]@{nested=@([pscustomobject]@{apiKey='synthetic-not-a-secret'})})}catch{$rejected=$_.Exception.Message -like 'Nonempty credential field*'}
Check $rejected 'Nonempty nested fixture credential is rejected without exposing its value'
[xml]$project=Get-Content -LiteralPath (Join-Path $root 'src/DwgTranslator.App/DwgTranslator.App.csproj') -Raw
$config=@($project.SelectNodes('//*[local-name()="Content" and @Link="settings.json"]'))
Check ($config.Count -eq 1) 'Exactly one default configuration supplies the APP output'
$source=[IO.Path]::GetFullPath((Join-Path (Join-Path $root 'src/DwgTranslator.App') $config[0].Include))
Check ($source -eq (Join-Path $root 'settings.json.example')) 'APP publishes the example, never local settings.json'
Check ($config[0].CopyToPublishDirectory -eq 'PreserveNewest') 'Default configuration retains publish copy contract'
$ignored=@('settings.json','src/DwgTranslator.App/appsettings.Development.json','cf-worker/.dev.vars','cf-worker/.env.production','.env','.env.local')
foreach($path in $ignored){
    & git -C $root check-ignore --no-index --quiet -- $path
    Check ($LASTEXITCODE -eq 0) "Local configuration is ignored: $path"
}
& git -C $root check-ignore --no-index --quiet -- settings.json.example
Check ($LASTEXITCODE -eq 1) 'Default template is not ignored'
$tracked=@(& git -C $root -c core.quotePath=false ls-files)
if($LASTEXITCODE -ne 0){throw 'Cannot enumerate tracked files'}
$secretNames=@($tracked|Where-Object {
    $_ -match '(^|/)(settings\.json|\.env(?:\..+)?|\.dev\.vars(?:\..+)?|id_rsa|id_ed25519)$' -and
    $_ -notmatch '\.(example|sample|template)$'
})
Check ($secretNames.Count -eq 0) 'No local credential configuration filenames are already tracked'
# Worker deployed secrets must be supplied outside source vars. Print no assigned values.
$worker=Get-Content -LiteralPath (Join-Path $root 'cf-worker/wrangler.toml') -Raw -Encoding UTF8
$secretAssignment='(?im)^\s*(?:\w*(?:API_KEY|API_SECRET|PASSWORD|PEPPER|PRIVATE_KEY|ACCESS_TOKEN|REFRESH_TOKEN)|EZFPY_KEY)\s*='
Check (-not [regex]::IsMatch($worker,$secretAssignment)) 'Worker source configuration contains no secret variable assignments'
Write-Host "SOURCE_CONFIGURATION=PASS ($checks checks); no ignored local credentials read."
