<#
.SYNOPSIS
  Pushes tv-apply.sh / tv-refresh.sh to the TV and runs tv-apply once. Re-run after editing the CONFIG block.
  -Termux also stages the Termux boot-agent files in /sdcard/Download/tvagent for the one-time setup.

.EXAMPLE  .\Deploy-Scripts.ps1 -Device 192.168.1.50:5555
.EXAMPLE  .\Deploy-Scripts.ps1 -Device 192.168.1.50:5555 -Termux
.EXAMPLE  .\Deploy-Scripts.ps1 -Device 192.168.1.50:5555 -Remove      # delete the scripts from the TV
#>
param(
    [Parameter(Mandatory)][string]$Device,
    [switch]$Termux,
    [switch]$CleanStaging,
    [switch]$Remove
)
. "$PSScriptRoot\common.ps1"
$adb = Connect-Tv $Device
$root = Split-Path $PSScriptRoot -Parent

if ($CleanStaging) {
    # Don't leave the boot script on shared storage: any app with storage access could edit it.
    & $adb -s $Device shell "rm -rf /sdcard/Download/tvagent"
    Write-Host "Staging folder removed."
    return
}

if ($Remove) {
    & $adb -s $Device shell 'kill $(cat /data/local/tmp/tv-daily.pid 2>/dev/null) 2>/dev/null; rm -rf /data/local/tmp/tv-daily.sh /data/local/tmp/tv-daily.pid /data/local/tmp/tv-daily.last /data/local/tmp/tv-daily.idle /data/local/tmp/tv-apply.blocked'
    & $adb -s $Device shell 'pkill -f tv-volhol[d].sh; rm -f /data/local/tmp/tv-volhold.sh /data/local/tmp/tv-volhold.pid /data/local/tmp/tv-volhold.held /data/local/tmp/tv-volhold.unsupported'
    & $adb -s $Device shell "rm -f /data/local/tmp/tv-apply.sh /data/local/tmp/tv-refresh.sh /data/local/tmp/tv-apply.log /data/local/tmp/tv-apply.boot; rm -rf /sdcard/Download/tvagent"
    & $adb -s $Device shell 'for k in ip_reachability_ignore_incompleted_ipv6_default_router_version ip_reachability_ignore_incompleted_ipv6_dns_server_version; do device_config delete connectivity $k; done'
    # Back to stock animation speed (1.0). The background limits clear themselves at the next restart.
    & $adb -s $Device shell "for k in window_animation_scale transition_animation_scale animator_duration_scale mitv_fast_cur_anim_scale; do settings put global `$k 1.0; done"
    Write-Host "Removed and animations reset. Restart the TV to clear the background limits."
    Write-Host "(If you installed Termux / Termux:Boot for the boot helper, uninstall them from the TV's app settings.)"
    return
}

foreach ($f in "tv-apply.sh", "tv-refresh.sh", "tv-daily.sh", "tv-volhold.sh") {
    & $adb -s $Device push (Join-Path $root "device\$f") "/data/local/tmp/$f" | Out-Null
}
& $adb -s $Device shell "chmod 755 /data/local/tmp/tv-apply.sh /data/local/tmp/tv-refresh.sh /data/local/tmp/tv-daily.sh /data/local/tmp/tv-volhold.sh; sh /data/local/tmp/tv-apply.sh; tail -n 4 /data/local/tmp/tv-apply.log"

if ($Termux) {
    & $adb -s $Device shell "mkdir -p /sdcard/Download/tvagent"
    foreach ($f in "setup.sh", "10-tv-agent.sh") {
        & $adb -s $Device push (Join-Path $root "termux\$f") "/sdcard/Download/tvagent/$f" | Out-Null
    }
    Write-Host "`nStaged. Now open Termux on the TV and run:  bash /sdcard/Download/tvagent/setup.sh"
    Write-Host "After setup succeeds, delete the staged copies:  .\Deploy-Scripts.ps1 -Device $Device -CleanStaging"
}
