<#
.SYNOPSIS  Read-only snapshot of your TV: build, memory, CPU hogs, packages, launchers, settings. Changes nothing.
.EXAMPLE   .\Audit.ps1 -Device 192.168.1.50:5555
#>
param(
    [Parameter(Mandatory)][string]$Device,
    [string]$OutDir = (Join-Path $PWD ("tv-audit-" + (Get-Date -Format "yyyyMMdd-HHmm")))
)
. "$PSScriptRoot\common.ps1"
$adb = Connect-Tv $Device
New-Item -ItemType Directory -Force $OutDir | Out-Null

$grabs = [ordered]@{
    "build"             = "getprop | grep -E 'ro.product.model|ro.build.fingerprint|ro.build.version.release|security_patch|ro.board.platform|ro.hardware'"
    "memory"            = "cat /proc/meminfo; dumpsys meminfo | sed -n '/Total RSS by process/,/Total RSS by OOM/p'"
    "cpu"               = "uptime; dumpsys cpuinfo | head -40; top -b -n 1 -m 25"
    "storage"           = "df -h /data"
    "packages-system"   = "pm list packages -s"
    "packages-user"     = "pm list packages -3"
    "packages-disabled" = "pm list packages -d"
    "home"              = "cmd role get-role-holders android.app.role.HOME; cmd package query-activities --brief -a android.intent.action.MAIN -c android.intent.category.HOME"
    "accessibility"     = "settings get secure enabled_accessibility_services; dumpsys accessibility | grep -E 'Bound services|Crashed'"
    "background-24h"    = "dumpsys procstats --hours 24 | grep -E '^  \* |TOTAL:'"
}
foreach ($k in $grabs.Keys) {
    & $adb -s $Device shell $grabs[$k] 2>&1 | Out-File -Encoding utf8 (Join-Path $OutDir "$k.txt")
    Write-Host "saved $k"
}
Write-Host "`nAudit saved to $OutDir (it can contain device identifiers - don't publish it as-is)."
