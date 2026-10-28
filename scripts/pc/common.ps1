# Shared helpers. Dot-source it:  . "$PSScriptRoot\common.ps1"

function Get-Adb {
    if ($env:ADB -and (Test-Path $env:ADB)) { return $env:ADB }
    $own = Join-Path $PSScriptRoot '..\..\tools\platform-tools\adb.exe'   # where the setup wizard downloads it
    if (Test-Path $own) { return (Resolve-Path $own).Path }
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $winget = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter adb.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($winget) { return $winget.FullName }
    throw "adb not found. Install it with:  winget install --id Google.PlatformTools -e   (or set `$env:ADB to adb.exe)"
}

function Connect-Tv([string]$Device) {
    $adb = Get-Adb
    & $adb connect $Device | Out-Null
    $state = (& $adb -s $Device get-state 2>$null)
    if ($state -ne "device") { throw "Can't reach $Device (state: '$state'). Is wireless debugging on and this PC approved?" }
    return $adb
}

function Write-ChangeLog([string]$Path, [hashtable]$Entry) {
    $Entry.time = (Get-Date).ToString("s")
    ($Entry | ConvertTo-Json -Compress) | Add-Content -Path $Path -Encoding utf8
}
