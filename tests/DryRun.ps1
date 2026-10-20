<#
.SYNOPSIS
  Runs wizard steps against a real TV with every change intercepted: read-only commands go to the TV,
  anything that would modify it is printed instead. For developing the wizard and the catalog.

.EXAMPLE  .\tests\DryRun.ps1 -Device 192.168.1.50 -Steps debloat,background -Auto
.EXAMPLE  .\tests\DryRun.ps1 -Device 192.168.1.50 -Undo
#>
param(
    [Parameter(Mandatory)][string]$Device,
    [string[]]$Steps = @('debloat', 'background'),
    [string]$Answers,
    [switch]$Auto,
    [switch]$Undo
)
$tvDevice = if ($Device -match ':\d+$') { $Device } else { "${Device}:5555" }
$runSteps = $Steps; $runAuto = [bool]$Auto; $runAnswers = $Answers; $runUndo = [bool]$Undo
. (Join-Path $PSScriptRoot '..\setup\Wizard.ps1')

$ReadOnly = '^(pm list|pm path|dumpsys|getprop|cat |ls |ps |settings get|cmd package query|cmd package resolve|cmd role get|logcat|pidof|grep|wc |tail|for k in|cut |\[ -f)'
function Sh([string]$cmd) {
    if ($cmd -match $ReadOnly) { return (& $script:Adb -s $script:Dev shell -n $cmd 2>&1 | ForEach-Object { "$_" }) }
    Write-Host "    DRYRUN shell: $cmd" -ForegroundColor DarkYellow
    if ($cmd -match 'pm disable-user') { return 'Package new state: disabled-user' }
    if ($cmd -match 'tv-apply.sh;') { return @('2026-01-01 00:00:00 apply start (uptime 1s, 3 blocked + 1 stop-only)') }
    return ''
}
function Invoke-Adb {
    if ($args[0] -in 'connect', 'mdns') { return (& $script:Adb @args 2>&1 | ForEach-Object { "$_" }) }
    Write-Host "    DRYRUN adb: $($args -join ' ')" -ForegroundColor DarkYellow
    return 'Success'
}
function Add-Change([hashtable]$h) { Write-Host "    DRYRUN log: $($h | ConvertTo-Json -Compress)" -ForegroundColor DarkYellow }
function Save-State { }

$script:Adb = Find-Adb
$script:AutoMode = $runAuto
if ($runAnswers) { $script:Ans = Get-Content $runAnswers -Raw | ConvertFrom-Json }
if (-not (Connect-Serial $tvDevice)) { throw "can't connect to $tvDevice" }
$script:Info = Get-TvInfo
Open-DeviceFolder
Show-TvCard
if ($runUndo) {
    # Fake change log so every undo branch runs (all intercepted).
    $fake = @(
        @{ type = 'install'; package = 'com.example.helper'; name = 'Example helper' },
        @{ type = 'deploy'; anim = '0.5' },
        @{ type = 'accessibility-add'; component = 'com.example/.Svc' },
        @{ type = 'pm-disable'; package = 'com.android.printspooler' }
    )
    function Get-Changes { return @($fake | ForEach-Object { [pscustomobject]$_ }) }
    function Move-Item { }
    Invoke-Undo
    return
}
foreach ($s in $runSteps) {
    switch ($s) { 'debloat' { Step-Debloat } 'background' { Step-Background } }
}
Write-Host "`nDry run finished. Nothing on the TV was changed." -ForegroundColor Green
