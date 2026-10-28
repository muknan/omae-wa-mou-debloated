<#
.SYNOPSIS
  Disables (never uninstalls) the packages in a list file, one batch at a time, logging every change.
  Reversible: -Restore re-enables everything this script disabled.

.EXAMPLE  .\Debloat.ps1 -Device 192.168.1.50:5555 -List ..\..\lists\xiaomi-google-tv.txt -Tier LOW -WhatIf
.EXAMPLE  .\Debloat.ps1 -Device 192.168.1.50:5555 -List ..\..\lists\xiaomi-google-tv.txt -Tier LOW
.EXAMPLE  .\Debloat.ps1 -Device 192.168.1.50:5555 -Restore

.NOTES
  List format:  <package>  <TIER>  # comment      (TIER = LOW | CONDITIONAL | DNT)
  DNT ("do not touch") lines are documentation only and are never applied.
  Persistent system apps may keep a process running after disable (their components still can't start).
#>
param(
    [Parameter(Mandatory)][string]$Device,
    [string]$List,
    [ValidateSet("LOW", "CONDITIONAL")][string[]]$Tier = @("LOW"),
    [string[]]$Only,                      # optional: just these packages from the list
    [int]$BatchSize = 3,
    [string]$Log = (Join-Path $PSScriptRoot "changes.jsonl"),
    [switch]$Restore,
    [switch]$WhatIf
)
. "$PSScriptRoot\common.ps1"
$adb = Connect-Tv $Device

function Test-Health {
    $h = & $adb -s $Device shell "echo HOME=`$(cmd role get-role-holders android.app.role.HOME); dumpsys accessibility | grep -c 'Crashed services:{}'"
    Write-Host "   health: $($h -join ' | ')"
}

if ($Restore) {
    if (-not (Test-Path $Log)) { throw "No change log at $Log" }
    $entries = @(Get-Content $Log | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.type -eq "pm-disable" })
    [array]::Reverse($entries)
    foreach ($e in $entries) {
        if ($WhatIf) { Write-Host "[WhatIf] pm enable --user 0 $($e.package)"; continue }
        & $adb -s $Device shell "pm enable --user 0 $($e.package)"
    }
    return
}

if (-not $List) { throw "-List is required (or use -Restore)" }
$rows = Get-Content $List | Where-Object { $_ -match '^\s*[a-z]' } | ForEach-Object {
    $p = ($_ -split '#')[0].Trim() -split '\s+'
    [pscustomobject]@{ Package = $p[0]; Tier = $p[1]; Note = (($_ -split '#', 2)[1]).Trim() }
}
$targets = $rows | Where-Object { $Tier -contains $_.Tier }
if ($Only) { $targets = $targets | Where-Object { $Only -contains $_.Package } }
$installed = (& $adb -s $Device shell "pm list packages") -replace '^package:', ''
$alreadyOff = (& $adb -s $Device shell "pm list packages -d") -replace '^package:', ''
$targets = $targets | Where-Object { $installed -contains $_.Package }
$skipped = @($targets | Where-Object { $alreadyOff -contains $_.Package })
$targets = $targets | Where-Object { $alreadyOff -notcontains $_.Package }
if ($skipped.Count) { Write-Host "$($skipped.Count) app(s) on the list are already switched off - skipping them." }
if (-not $targets) { Write-Host "Nothing to do: everything selected is already off or not on this TV."; return }

Write-Host "Will disable $(@($targets).Count) package(s) in batches of ${BatchSize}:`n"
$targets | Format-Table Package, Tier, Note -AutoSize | Out-String | Write-Host
if ($WhatIf) { Write-Host "[WhatIf] nothing changed."; return }

$i = 0
foreach ($t in $targets) {
    $out = & $adb -s $Device shell "pm disable-user --user 0 $($t.Package)"
    Write-Host " $($t.Package): $out"
    if ($out -match "disabled-user") { Write-ChangeLog $Log @{ type = "pm-disable"; package = $t.Package; tier = $t.Tier } }
    $i++
    if ($i % $BatchSize -eq 0) {
        Test-Health
        Read-Host "   Test Home / Back / an HDMI input / a streaming app, then press Enter for the next batch"
    }
}
Test-Health
Write-Host "`nDone. Undo any time with:  .\Debloat.ps1 -Device $Device -Restore"
