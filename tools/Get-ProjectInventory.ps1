# Purpose: read-only inventory and explicit project-reference evidence; never deletes files.
# Usage: ./tools/Get-ProjectInventory.ps1 -OutputDir <new artifact directory>
# Output: files.csv, classification-summary.csv, project-references.csv, scope.json.
param([Parameter(Mandatory=$true)][string]$OutputDir)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$out=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
if(Test-Path -LiteralPath $out){throw 'Choose a new output directory to preserve evidence'}
$rows=New-Object 'System.Collections.Generic.List[object]'
$links=New-Object 'System.Collections.Generic.List[object]'
$skipped=New-Object 'System.Collections.Generic.List[string]'
$stack=New-Object 'System.Collections.Generic.Stack[string]'
$stack.Push($root)
while($stack.Count){
    $dir=$stack.Pop()
    foreach($item in Get-ChildItem -LiteralPath $dir -Force){
        $relative=$item.FullName.Substring($root.Length+1).Replace('\','/')
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){$skipped.Add($relative);continue}
        if($item.PSIsContainer){
            if($relative -eq '.git'){$skipped.Add('.git (repository database)');continue}
            $stack.Push($item.FullName);continue
        }
        $class='UNKNOWN';$evidence='No classification rule; retain for review'
        switch -Regex ($relative){
            '(^|/)(bin|obj|node_modules)/' {$class='GENERATED_OR_DEPENDENCY';$evidence='Build/dependency directory; not a deletion authorization';break}
            '^release/' {$class='LOCAL_DELIVERY_OR_USER_DATA';$evidence='Canonical runnable release; preserve personal data';break}
            '^artifacts/' {$class='ARTIFACT_OR_EVIDENCE';$evidence='May contain unique evidence and recovery data';break}
            '^tests/' {$class='TEST';$evidence='Test tree; formal test scope still defined by projects/runners';break}
            '^src/.+/Embedded/' {$class='EMBEDDED_RESOURCE';$evidence='Embedded build/plugin inputs; preserve dynamic dependencies';break}
            '^src/.*\.(cs|xaml|resx|csproj)$' {$class='SOURCE';$evidence='Source tree and recognized project/source extension';break}
            '^assets/' {$class='RESOURCE';$evidence='Central resource tree';break}
            '^installer/' {$class='INSTALLER';$evidence='Installer source and distribution helpers';break}
            '^tools/' {$class='DEVELOPMENT_OR_BUILD_TOOL';$evidence='Tool tree; age/name does not prove unused';break}
            '^docs/|(^|/)(README|CHANGELOG|AGENTS)\.md$|^cleanup-report\.md$' {$class='DOCUMENTATION';$evidence='Documentation path';break}
            '^cf-worker/(package(-lock)?\.json|wrangler\.toml)$' {$class='BACKEND_CONFIGURATION';$evidence='Reviewed package/deployment entry; preserve, not a secret-content audit';break}
            '^cf-worker/(schema\.sql|upgrades/[^/]+\.sql)$' {$class='BACKEND_SCHEMA_OR_UPGRADE';$evidence='Database schema or explicit upgrade input; never execute or delete as cleanup';break}
            '^cf-worker/(CF_BACKEND_CONTRACT\.md|ops/[^/]+\.md)$' {$class='DOCUMENTATION';$evidence='Backend contract or operational record';break}
            '^cf-worker/ops/retention-preview\.sql$' {$class='OPERATIONS_TOOL';$evidence='Reviewed read-only operational preview; inventory does not execute it';break}
            '^cf-worker/tools/(schema-preflight|verify-remote-schema)\.mjs$' {$class='DEVELOPMENT_OR_BUILD_TOOL';$evidence='Reviewed offline schema verification entry; does not authorize remote access';break}
            '^cf-worker/(src|migrations|tests|public)/' {$class='BACKEND_SOURCE_OR_RESOURCE';$evidence='Independent backend; outside desktop move scope';break}
            '(^|/)\.(wrangler|vs|vscode|claude)/|^%SystemDrive%/' {$class='LOCAL_STATE_REVIEW';$evidence='Local state; retain, may include recovery data';break}
            '^settings\.json$' {$class='UNKNOWN';$evidence='Root local settings; not the publication template';break}
            '\.(sln|csproj|props|targets)$|^settings\.json\.example$|^\.gitignore$|^(dev|publish)\.bat$' {$class='PROJECT_CONFIGURATION';$evidence='Project/build entry or explicit default template';break}
        }
        $trackedType=if($item.Extension -match '^\.(dll|exe|plugin|bundle)$'){'BINARY_REQUIRES_RUNTIME_REVIEW'}else{''}
        $rows.Add([pscustomobject]@{Path=$relative;Bytes=$item.Length;Category=$class;Evidence=$evidence;BinaryReview=$trackedType})
        $isBuildInput=$class -ne 'GENERATED_OR_DEPENDENCY' -and $relative -notmatch '^(release|artifacts)/'
        if($isBuildInput -and $item.Extension -in @('.csproj','.props','.targets')){
            # Parse declarations only. Never evaluate MSBuild tasks/imports or resolve external entities.
            $readerSettings=New-Object System.Xml.XmlReaderSettings
            $readerSettings.DtdProcessing=[System.Xml.DtdProcessing]::Prohibit
            $readerSettings.XmlResolver=$null
            $reader=[System.Xml.XmlReader]::Create($item.FullName,$readerSettings)
            try {
                $xml=New-Object System.Xml.XmlDocument
                $xml.XmlResolver=$null
                $xml.Load($reader)
            } finally { $reader.Dispose() }
            foreach($ref in $xml.SelectNodes('//*[@Include or @Projects or @Project]')){
                $value=if($ref.HasAttribute('Include')){$ref.GetAttribute('Include')}elseif($ref.HasAttribute('Projects')){$ref.GetAttribute('Projects')}else{$ref.GetAttribute('Project')}
                $conditions=@()
                for($ancestor=$ref;$ancestor -is [System.Xml.XmlElement];$ancestor=$ancestor.ParentNode){
                    if($ancestor.HasAttribute('Condition')){$conditions += $ancestor.GetAttribute('Condition')}
                }
                $links.Add([pscustomobject]@{Project=$relative;Kind=$ref.LocalName;Reference=$value;Condition=($conditions -join ' AND ');Scope='Unevaluated XML declarations; conditions, properties, globs and imports are not executed or resolved'})
            }
        }
        if($isBuildInput -and $item.Extension -eq '.sln'){
            $solutionProjects=@{}
            $solutionLines=@([IO.File]::ReadLines($item.FullName))
            foreach($line in $solutionLines){
                if($line -match '^Project\("(?<type>[^"\r\n]+)"\)\s*=\s*"[^"\r\n]*",\s*"(?<path>[^"\r\n]+)",\s*"(?<guid>[^"\r\n]+)"'){
                    # Solution-folder declarations are virtual containers, not project dependencies.
                    if($Matches['type'] -in @('{66A26720-8FB5-11D2-AA7E-00C04F688DDE}','{2150E333-8FDC-42A3-9474-1A3956D46DE8}')) { continue }
                    $solutionProjects[$Matches['guid']]=$Matches['path']
                    $links.Add([pscustomobject]@{Project=$relative;Kind='SolutionProject';Reference=$Matches['path'];Condition='';Scope='Solution declaration only; project existence/configuration not evaluated'})
                }
            }
            $owner=$null; $inDependencies=$false
            foreach($line in $solutionLines){
                if($line -match '^Project\(.*\)\s*=.*"(?<guid>\{[^}]+\})"\s*$'){$owner=$Matches['guid']}
                elseif($line -match '^\s*ProjectSection\(ProjectDependencies\)'){$inDependencies=$true}
                elseif($line -match '^\s*EndProjectSection'){$inDependencies=$false}
                elseif($line -match '^EndProject'){$owner=$null;$inDependencies=$false}
                elseif($inDependencies -and $line -match '^\s*(?<guid>\{[^}]+\})\s*='){
                    $dependency=$Matches['guid']
                    $source=if($solutionProjects.ContainsKey($owner)){$solutionProjects[$owner]}else{$owner}
                    $target=if($solutionProjects.ContainsKey($dependency)){$solutionProjects[$dependency]}else{$dependency}
                    $links.Add([pscustomobject]@{Project=$relative;Kind='SolutionDependency';Reference=$target;Condition=('Owner='+$source);Scope='Explicit solution build-order dependency; not an assembly reference'})
                }
            }
        }
    }
}
New-Item -ItemType Directory -Path $out | Out-Null
$rows.ToArray() | Sort-Object Path | Export-Csv -LiteralPath (Join-Path $out 'files.csv') -NoTypeInformation -Encoding UTF8
$rows.ToArray() | Group-Object Category | Select-Object Name,Count | Export-Csv -LiteralPath (Join-Path $out 'classification-summary.csv') -NoTypeInformation -Encoding UTF8
$links.ToArray() | Export-Csv -LiteralPath (Join-Path $out 'project-references.csv') -NoTypeInformation -Encoding UTF8
@{Root=$root;Created=(Get-Date -Format o);Files=$rows.Count;ExplicitProjectItems=$links.Count;Skipped=$skipped.ToArray();Limitations='Current metadata snapshot, not reconstruction of missing original inventory; categories are conservative path-based hints, not proof of dead code or deletion approval. No secret file contents captured.'} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'scope.json') -Encoding UTF8
Write-Host "INVENTORY=PASS Files=$($rows.Count) ExplicitProjectItems=$($links.Count)"