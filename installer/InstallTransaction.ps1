# Purpose: rollback portable install failures and recover on the next invocation; no recursive deletion.
# Input: validated target and exact destination paths. Recovery evidence is retained if restoration fails.
function Get-InstallFileSha256([string]$LiteralPath) {
    # Do not depend on Microsoft.PowerShell.Utility module autoloading. The bootstrap
    # must work on locked-down/offline Windows PowerShell hosts as well as normal shells.
    $stream=$null
    $sha=$null
    try {
        $stream=[IO.File]::Open($LiteralPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        $sha=[Security.Cryptography.SHA256]::Create()
        return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')
    } finally {
        if($sha){$sha.Dispose()}
        if($stream){$stream.Dispose()}
    }
}
function Start-InstallTransaction([string]$Target) {
    $parent=[IO.Path]::GetDirectoryName($Target)
    $backup=Join-Path $parent ('.dwgc2e-install-recovery-'+[guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($backup) | Out-Null
    return @{State='Active';Root=$Target;Backup=$backup;Entries=(New-Object 'System.Collections.Generic.List[object]');Seen=@{};Directories=(New-Object 'System.Collections.Generic.List[string]')}
}
function Assert-TransactionPath([string]$Path) {
    $p=[IO.Path]::GetFullPath($Path)
    while($p) {
        if((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Linked transaction path: $p"}
        $p=[IO.Path]::GetDirectoryName($p)
    }
}
function Save-InstallTransaction($Transaction) {
    # Write evidence before touching the destination. A previous valid journal survives failed replacement.
    $journal=Join-Path $Transaction.Backup 'journal.json'
    $temp=Join-Path $Transaction.Backup 'journal.next'
    @{Schema=1;State=$Transaction.State;Root=$Transaction.Root;Entries=@($Transaction.Entries.ToArray());Directories=@($Transaction.Directories.ToArray())} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temp -Encoding UTF8
    if(Test-Path -LiteralPath $journal){[IO.File]::Replace($temp,$journal,(Join-Path $Transaction.Backup 'journal.previous'))}else{[IO.File]::Move($temp,$journal)}
}
function Register-InstallWrite($Transaction,[string]$Destination,[string]$ExpectedNewHash) {
    $path=[IO.Path]::GetFullPath($Destination)
    Assert-TransactionPath $path
    if($Transaction.Seen.ContainsKey($path)){return}
    $inRoot=$path.StartsWith($Transaction.Root+'\',[StringComparison]::OrdinalIgnoreCase)
    $menu=Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\QLCAD.lnk'
    $desktop=Join-Path ([Environment]::GetFolderPath('Desktop')) 'QLCAD.lnk'
    if(-not $inRoot -and $path -ne $menu -and $path -ne $desktop){throw "Unapproved transaction destination: $path"}
    $exists=Test-Path -LiteralPath $path
    $entry=@{Path=$path;Existed=$exists;NewHash=$ExpectedNewHash;BackupName=($Transaction.Entries.Count.ToString()+'.bin');Hash=$null;Attributes=$null;LastWriteUtc=$null}
    if($exists) {
        if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "Transaction destination is a directory: $path"}
        $item=Get-Item -LiteralPath $path -Force
        $entry.Attributes=[int]$item.Attributes
        $entry.LastWriteUtc=$item.LastWriteTimeUtc.ToString('o')
        $entry.Hash=(Get-InstallFileSha256 $path)
        $copy=Join-Path $Transaction.Backup $entry.BackupName
        Copy-Item -LiteralPath $path -Destination $copy
        if((Get-InstallFileSha256 $copy) -ne $entry.Hash){throw 'Transaction backup verification failed'}
    }
    $parent=[IO.Path]::GetDirectoryName($path)
    while($parent -and -not(Test-Path -LiteralPath $parent)) {
        if(-not $Transaction.Directories.Contains($parent)){$Transaction.Directories.Add($parent)}
        $parent=[IO.Path]::GetDirectoryName($parent)
    }
    $Transaction.Entries.Add($entry)
    Save-InstallTransaction $Transaction
    $Transaction.Seen[$path]=$true
}
function Clear-InstallTransaction($Transaction) {
    # Only the explicit files this transaction created; never enumerate another task for deletion.
    Assert-TransactionPath $Transaction.Backup
    foreach($entry in $Transaction.Entries) {
        $p=Join-Path $Transaction.Backup $entry.BackupName
        if(Test-Path -LiteralPath $p){[IO.File]::SetAttributes($p,[IO.FileAttributes]::Normal);Remove-Item -LiteralPath $p -Force}
    }
    foreach($name in @('journal.json','journal.next','journal.previous')) {
        $p=Join-Path $Transaction.Backup $name
        if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force}
    }
    [IO.Directory]::Delete($Transaction.Backup,$false)
}
function Undo-InstallTransaction($Transaction) {
    # Validate ALL snapshots and destinations before the first restore.
    foreach($entry in $Transaction.Entries) {
        Assert-TransactionPath $entry.Path
        if(-not $entry.Existed -and $entry.Path -match '\\(?:settings\.json|glossaries\\)' -and (Test-Path -LiteralPath $entry.Path -PathType Leaf)) {
            if(-not $entry.NewHash -or (Get-InstallFileSha256 $entry.Path) -ne $entry.NewHash){throw 'Seeded personal file changed after interruption; recovery retained for review'}
        }
        if($entry.Existed) {
            $copy=Join-Path $Transaction.Backup $entry.BackupName
            Assert-TransactionPath $copy
            if((Get-InstallFileSha256 $copy) -ne $entry.Hash){throw 'Recovery backup was modified; retained for inspection'}
        }
    }
    for($i=$Transaction.Entries.Count-1;$i -ge 0;$i--) {
        $entry=$Transaction.Entries[$i]
        if($entry.Existed) {
            if((Test-Path -LiteralPath $entry.Path -PathType Leaf) -and ((Get-InstallFileSha256 $entry.Path) -eq $entry.Hash)){continue}
            Copy-Item -LiteralPath (Join-Path $Transaction.Backup $entry.BackupName) -Destination $entry.Path -Force
            if((Get-InstallFileSha256 $entry.Path) -ne $entry.Hash){throw 'Restored file hash mismatch'}
            [IO.File]::SetLastWriteTimeUtc($entry.Path,[DateTime]::Parse($entry.LastWriteUtc).ToUniversalTime())
            [IO.File]::SetAttributes($entry.Path,[IO.FileAttributes]$entry.Attributes)
        } elseif(Test-Path -LiteralPath $entry.Path) {
            # Only a newly created exact destination, never recursive deletion.
            if(-not(Test-Path -LiteralPath $entry.Path -PathType Leaf)){throw 'New destination became a directory; recovery stopped'}
            Remove-Item -LiteralPath $entry.Path -Force
        }
    }
    foreach($dir in @($Transaction.Directories | Sort-Object Length -Descending)) {
        Assert-TransactionPath $dir
        if((Test-Path -LiteralPath $dir -PathType Container) -and @(Get-ChildItem -LiteralPath $dir -Force).Count -eq 0){[IO.Directory]::Delete($dir,$false)}
    }
    # Persist completion before deleting snapshots, so interrupted cleanup is resumable.
    $Transaction.State='RolledBack'
    Save-InstallTransaction $Transaction
    Clear-InstallTransaction $Transaction
}

function Import-InstallTransaction([string]$Backup,[string]$Root) {
    Assert-TransactionPath $Backup
    $record=Get-Content -LiteralPath (Join-Path $Backup 'journal.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if($record.Schema -ne 1 -or $record.Root -ne $Root -or $record.State -notin @('Active','Committed','RolledBack')){throw 'Invalid recovery journal'}
    if(@($record.Entries).Count -gt 10000){throw 'Too many recovery entries'}
    $transaction=@{State=$record.State;Root=$Root;Backup=$Backup;Entries=(New-Object 'System.Collections.Generic.List[object]');Seen=@{};Directories=(New-Object 'System.Collections.Generic.List[string]')}
    $menu=Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\QLCAD.lnk'
    $desktop=Join-Path ([Environment]::GetFolderPath('Desktop')) 'QLCAD.lnk'
    $index=0
    foreach($entry in @($record.Entries)) {
        $path=[IO.Path]::GetFullPath([string]$entry.Path)
        if($path -ne $entry.Path -or $transaction.Seen.ContainsKey($path) -or $entry.BackupName -cne ($index.ToString()+'.bin') -or $entry.Existed -isnot [bool]){throw 'Invalid recovery entry'}
        if($path.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase)) {
            $relative=$path.Substring($Root.Length+1)
            if($relative -notmatch '^(QLCAD\.exe|settings\.json|使用说明\.txt|Uninstall\.ps1|installation-manifest\.json|卸载\.cmd|(?:CadPlugin|assets|glossaries)\\[^:]+|prompts\\deepl_context\.txt)$' -or $relative -match '(^|[\\/])\.\.?([\\/]|$)'){throw 'Recovery path not in installer write set'}
            if($entry.Existed -and $relative -match '^(settings\.json|glossaries\\)'){throw 'Recovery must not overwrite personal data'}
        } elseif($path -ne $menu -and $path -ne $desktop){throw 'Recovery path outside installation'}
        Assert-TransactionPath $path
        if($entry.Existed) {
            if($entry.Hash -notmatch '^[A-Fa-f0-9]{64}$'){throw 'Invalid recovery hash'}
            [DateTime]::Parse($entry.LastWriteUtc) | Out-Null
            $copy=Join-Path $Backup $entry.BackupName
            Assert-TransactionPath $copy
            if($record.State -eq 'Active' -or (Test-Path -LiteralPath $copy)){
                if((Get-InstallFileSha256 $copy) -ne $entry.Hash){throw 'Recovery backup hash mismatch'}
            }
        }
        $transaction.Entries.Add($entry);$transaction.Seen[$path]=$true;$index++
    }
    foreach($dir in @($record.Directories)) {
        $full=[IO.Path]::GetFullPath([string]$dir)
        if($full -ne $dir -or ($full -ne $Root -and -not $full.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase))){throw 'Recovery directory outside installation'}
        Assert-TransactionPath $full
        $transaction.Directories.Add($full)
    }
    return $transaction
}

function Restore-PendingInstallTransaction([string]$Target) {
    $parent=[IO.Path]::GetDirectoryName($Target)
    # Resume only journals belonging to this exact installation; other installations are untouched.
    foreach($pending in @(Get-ChildItem -LiteralPath $parent -Directory -Filter '.dwgc2e-install-recovery-*' -ErrorAction SilentlyContinue)) {
        Assert-TransactionPath $pending.FullName
        $journal=Join-Path $pending.FullName 'journal.json'
        if(-not(Test-Path -LiteralPath $journal -PathType Leaf)){continue}
        $record=Get-Content -LiteralPath $journal -Raw -Encoding UTF8 | ConvertFrom-Json
        if($record.Root -ne $Target){continue}
        $previous=Import-InstallTransaction $pending.FullName $Target
        if($previous.State -in @('Committed','RolledBack')){Clear-InstallTransaction $previous}
        else {Undo-InstallTransaction $previous; Write-Host 'INSTALL_INTERRUPTION_RECOVERY=PASS'}
    }
}
