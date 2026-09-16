# Notify Windows Shell that a replaced desktop EXE has new icon resources.
# No Explorer termination, global cache deletion, registry edits or executable changes.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
$exe = Get-Item -LiteralPath $ExecutablePath -ErrorAction Stop
if ($exe.PSIsContainer -or $exe.Extension -ine '.exe') { throw 'Expected an existing executable file.' }
if (-not ('DwgTranslator.Tools.ShellIconRefresh' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace DwgTranslator.Tools {
    public static class ShellIconRefresh {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);
    }
}
"@
}
# SHCNE_UPDATEITEM and SHCNE_UPDATEDIR, with SHCNF_PATHW | SHCNF_FLUSH.
# Flush delivery of the notifications; this is not proof a visible window repainted.
[DwgTranslator.Tools.ShellIconRefresh]::SHChangeNotify(0x2000, 0x1005, $exe.FullName, [IntPtr]::Zero)
[DwgTranslator.Tools.ShellIconRefresh]::SHChangeNotify(0x1000, 0x1005, $exe.DirectoryName, [IntPtr]::Zero)
Write-Output "SHELL_ICON_REFRESH=NOTIFIED; PATH=$($exe.FullName)"
