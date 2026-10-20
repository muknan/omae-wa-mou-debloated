# Helpers for Wizard.ps1. Windows PowerShell 5.1 and PowerShell 7 compatible. ASCII only on purpose:
# Windows PowerShell reads BOM-less scripts as ANSI.

$script:Root = Split-Path $PSScriptRoot -Parent
$script:Adb = $null
$script:Dev = $null
$script:Info = $null
$script:Dir = $null
$script:State = $null
$script:Ans = $null
$script:AutoMode = $false
$script:IsWin = ($env:OS -eq 'Windows_NT')

# ------------------------------------------------------------------ console UI
function Say([string]$t = '', [string]$c = 'Gray') { Write-Host $t -ForegroundColor $c }
function Ok([string]$t) { Write-Host "  [ok] $t" -ForegroundColor Green }
function Warn([string]$t) { Write-Host "  [!]  $t" -ForegroundColor Yellow }
function Bad([string]$t) { Write-Host "  [x]  $t" -ForegroundColor Red }
function Info([string]$t) { Write-Host "       $t" -ForegroundColor Gray }
function OnTv([string]$t) { Write-Host "  ON THE TV >> $t" -ForegroundColor Magenta }
function Title([string]$n, [string]$t) {
    Write-Host ''
    Write-Host ('=' * 70) -ForegroundColor DarkCyan
    Write-Host "  $n  $t" -ForegroundColor Cyan
    Write-Host ('=' * 70) -ForegroundColor DarkCyan
}

function Ask-YesNo([string]$q, [bool]$default = $true) {
    if ($script:AutoMode) { return $default }
    $hint = if ($default) { '[Y/n]' } else { '[y/N]' }
    while ($true) {
        $a = Read-Host "  $q $hint"
        if ([string]::IsNullOrWhiteSpace($a)) { return $default }
        switch -Regex ($a.Trim()) { '^(y|yes)$' { return $true } '^(n|no)$' { return $false } }
        Warn 'Please type y or n (or just press Enter for the suggested answer).'
    }
}

# Numbered menu. Returns the chosen index (0-based). Enter = the default.
function Ask-Choice([string]$q, [string[]]$options, [int]$default = 0) {
    if ($script:AutoMode) { return $default }
    Say "  $q"
    for ($i = 0; $i -lt $options.Count; $i++) {
        $mark = if ($i -eq $default) { '  <- suggested' } else { '' }
        Say ("    {0}) {1}{2}" -f ($i + 1), $options[$i], $mark)
    }
    while ($true) {
        $a = Read-Host '  Your choice (Enter = suggested)'
        if ([string]::IsNullOrWhiteSpace($a)) { return $default }
        $n = 0
        if ([int]::TryParse($a.Trim(), [ref]$n) -and $n -ge 1 -and $n -le $options.Count) { return $n - 1 }
        Warn "Type a number from 1 to $($options.Count)."
    }
}

function Wait-Enter([string]$msg = 'Press Enter when that is done') {
    if ($script:AutoMode) { return }
    [void](Read-Host "  $msg")
}

# Waits up to $seconds for a key. Returns the key char, or '' on timeout. Used for "press U to undo" checks.
function Read-KeyTimeout([int]$seconds, [string]$msg) {
    if ($script:AutoMode) { return '' }
    $canPoll = $true
    try { [void][Console]::KeyAvailable } catch { $canPoll = $false }
    if (-not $canPoll) { $a = Read-Host "  $msg (Enter to continue)"; return $a }
    for ($s = $seconds; $s -gt 0; $s--) {
        Write-Host ("`r  $msg  continuing in {0,2}s " -f $s) -NoNewline -ForegroundColor Yellow
        for ($t = 0; $t -lt 10; $t++) {
            if ([Console]::KeyAvailable) { $k = [Console]::ReadKey($true); Write-Host ''; return [string]$k.KeyChar }
            Start-Sleep -Milliseconds 100
        }
    }
    Write-Host ''
    return ''
}

# ------------------------------------------------------------------ adb
function Find-Adb {
    $kit = [IO.Path]::Combine($script:Root, 'tools', 'platform-tools', $(if ($script:IsWin) { 'adb.exe' } else { 'adb' }))
    if (Test-Path $kit) { return $kit }
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    if ($script:IsWin) {
        $wg = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\adb.exe'
        if (Test-Path $wg) { return $wg }
        $pkg = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Filter adb.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($pkg) { return $pkg.FullName }
    }
    return $null
}

function Install-Adb {
    $os = if ($script:IsWin) { 'windows' } elseif ($IsMacOS) { 'darwin' } else { 'linux' }
    $url = "https://dl.google.com/android/repository/platform-tools-latest-$os.zip"
    $tools = Join-Path $script:Root 'tools'
    New-Item -ItemType Directory -Force $tools | Out-Null
    $zip = Join-Path $tools 'platform-tools.zip'
    Info "Downloading Google's Android platform-tools (about 7 MB) from dl.google.com ..."
    Info 'By downloading them you accept the Android SDK terms: https://developer.android.com/studio/terms'
    Get-Download $url $zip
    Expand-Archive -Path $zip -DestinationPath $tools -Force
    Remove-Item $zip -Force
    $a = Find-Adb
    if (-not $script:IsWin -and $a) { & chmod +x $a }
    return $a
}

function Invoke-Adb {
    $out = & $script:Adb @args 2>&1 | ForEach-Object { "$_" }
    return $out
}

# Run a shell command on the TV and return its output lines. Avoid double quotes inside $cmd (Windows
# PowerShell mangles them when passing arguments); use single quotes.
function Sh([string]$cmd) {
    $out = & $script:Adb -s $script:Dev shell -n $cmd 2>&1 | ForEach-Object { "$_" }
    return $out
}
function Sh1([string]$cmd) { return ((Sh $cmd) -join "`n").Trim() }

function Get-AdbState([string]$serial) {
    $s = (& $script:Adb -s $serial get-state 2>&1 | ForEach-Object { "$_" }) -join ' '
    if ($s -match 'unauthorized') { return 'unauthorized' }
    if ($s -match '^\s*device') { return 'device' }
    if ($s -match 'offline') { return 'offline' }
    return 'none'
}

# ------------------------------------------------------------------ downloads
function Get-Download([string]$url, [string]$dest) {
    $old = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
    } finally { $ProgressPreference = $old }
}

# Downloads a pinned helper APK (catalog/helpers.json) into downloads/ and checks its SHA-256.
function Get-HelperApk([string]$id) {
    $h = (Get-Content (Join-Path $script:Root 'catalog/helpers.json') -Raw | ConvertFrom-Json).helpers | Where-Object { $_.id -eq $id }
    if (-not $h) { throw "Unknown helper $id" }
    $url = $h.url; $sha = $h.sha256
    if ($h.abis) {
        $abi = $script:Info.Abi
        $pick = $h.abis | Where-Object { $_.abi -eq $abi } | Select-Object -First 1
        if (-not $pick) { $pick = $h.abis | Where-Object { $_.abi -eq 'universal' } | Select-Object -First 1 }
        $url = $pick.url; $sha = $pick.sha256
    }
    $dl = Join-Path $script:Root 'downloads'
    New-Item -ItemType Directory -Force $dl | Out-Null
    $file = Join-Path $dl ([IO.Path]::GetFileName(([uri]$url).AbsolutePath).Replace('+', '_'))
    if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash -ne $sha.ToUpper()) {
        Info "Downloading $($h.name) from $(([uri]$url).Host) ..."
        Get-Download $url $file
    }
    $got = (Get-FileHash $file -Algorithm SHA256).Hash
    if ($got -ne $sha.ToUpper()) { Remove-Item $file -Force; throw "$($h.name): checksum mismatch, refusing to install it" }
    Ok "$($h.name) $($h.version) downloaded and verified (SHA-256 matches the official release)"
    return [pscustomobject]@{ File = $file; Package = $h.package; Name = $h.name; Version = $h.version }
}

function Install-Helper([string]$id) {
    $apk = Get-HelperApk $id
    $already = Sh1 "pm path $($apk.Package)"
    $out = (Invoke-Adb -s $script:Dev install -r $apk.File) -join ' '
    if ($out -notmatch 'Success') { throw "$($apk.Name) did not install: $out" }
    if (-not $already) { Add-Change @{ type = 'install'; package = $apk.Package; name = $apk.Name } }
    Ok "$($apk.Name) installed"
    return $apk
}

# ------------------------------------------------------------------ device detection
function Get-TvInfo {
    $p = @{}
    foreach ($l in (Sh 'for k in ro.product.manufacturer ro.product.brand ro.product.model ro.product.name ro.product.device ro.build.version.release ro.build.version.sdk ro.product.cpu.abi ro.serialno; do echo $k=$(getprop $k); done')) {
        if ($l -match '^([^=]+)=(.*)$') { $p[$matches[1]] = $matches[2].Trim() }
    }
    $ramKb = 0; [void][int]::TryParse(((Sh1 'grep MemTotal /proc/meminfo') -replace '\D', ''), [ref]$ramKb)
    $all = @(Sh 'pm list packages' | ForEach-Object { $_ -replace '^package:', '' } | Where-Object { $_ })
    $disabled = @(Sh 'pm list packages -d' | ForEach-Object { $_ -replace '^package:', '' } | Where-Object { $_ })
    $system = @(Sh 'pm list packages -s' | ForEach-Object { $_ -replace '^package:', '' } | Where-Object { $_ })
    $user = @(Sh 'pm list packages -3' | ForEach-Object { $_ -replace '^package:', '' } | Where-Object { $_ })
    $platform = 'Android'
    if ($all -contains 'com.google.android.apps.tv.launcherx') { $platform = 'Google TV' }
    elseif ($all -contains 'com.google.android.tvlauncher' -or $all -contains 'com.google.android.leanbacklauncher') { $platform = 'Android TV' }
    elseif ($all -contains 'com.amazon.tv.launcher') { $platform = 'Fire TV' }
    $sdk = 0; [void][int]::TryParse($p['ro.build.version.sdk'], [ref]$sdk)
    $homePkg = Get-HomeApp $sdk
    $serial = $p['ro.serialno']; if (-not $serial) { $serial = ($script:Dev -replace '[^0-9A-Za-z]', '') }
    return [pscustomobject]@{
        Maker = $p['ro.product.manufacturer']; Brand = $p['ro.product.brand']; Model = $p['ro.product.model']
        Device = $p['ro.product.device']; Android = $p['ro.build.version.release']; Sdk = $sdk
        Abi = $p['ro.product.cpu.abi']; RamMb = [int]($ramKb / 1024); Serial = $serial; Platform = $platform
        Home = $homePkg; All = $all; Disabled = $disabled; System = $system; User = $user
    }
}

function Get-HomeApp([int]$sdk) {
    $h = ''
    if ($sdk -ge 29) { $h = Sh1 'cmd role get-role-holders android.app.role.HOME' }
    if (-not $h) { $h = ((Sh 'cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME') | Select-Object -Last 1) -replace '/.*', '' }
    return $h.Trim()
}

function Get-Measure {
    $mem = 0; [void][int]::TryParse(((Sh1 'grep MemAvailable /proc/meminfo') -replace '\D', ''), [ref]$mem)
    $apps = 0; [void][int]::TryParse((Sh1 'ps -A -o USER | grep -c ^u0_a'), [ref]$apps)
    $up = 0; [void][double]::TryParse(((Sh1 'cut -d. -f1 /proc/uptime')), [ref]$up)
    return [pscustomobject]@{ FreeMb = [int]($mem / 1024); AppProcesses = $apps; UptimeS = [int]$up; Time = (Get-Date).ToString('s') }
}

# Packages that expose a given service (e.g. VPN, TV input) - from the package manager, not guessed from names.
function Get-ServicePackages([string]$action) {
    return @(Sh "cmd package query-services --brief -a $action" | Where-Object { $_ -match '/' } | ForEach-Object { ($_.Trim() -split '/')[0] } | Sort-Object -Unique)
}
function Get-LauncherApps {
    return @(Sh 'cmd package query-activities --brief -a android.intent.action.MAIN -c android.intent.category.LEANBACK_LAUNCHER' | Where-Object { $_ -match '/' } | ForEach-Object { ($_.Trim() -split '/')[0] } | Sort-Object -Unique)
}
function Get-ProtectedSystemApps {
    $p = @($script:Info.Home)
    $p += ((Sh1 'settings get secure default_input_method') -replace '/.*', '')
    $p += ((Sh1 'settings get secure enabled_accessibility_services') -split ':' | ForEach-Object { $_ -replace '/.*', '' })
    return @($p | Where-Object { $_ -and $_ -ne 'null' } | Sort-Object -Unique)
}

# ------------------------------------------------------------------ per-TV folder, state and change log
function Open-DeviceFolder {
    $name = ('{0}_{1}' -f $script:Info.Model, ($script:Info.Serial -replace '^.*(.{6})$', '$1')) -replace '[^0-9A-Za-z_.-]', '-'
    $script:Dir = [IO.Path]::Combine($script:Root, 'my-tv', $name)
    New-Item -ItemType Directory -Force $script:Dir | Out-Null
    $sf = Join-Path $script:Dir 'state.json'
    if (Test-Path $sf) { $script:State = Get-Content $sf -Raw | ConvertFrom-Json }
    else { $script:State = [pscustomobject]@{ stepsDone = @(); skip = @(); conf = $null; before = $null } }
    foreach ($k in 'stepsDone', 'skip', 'conf', 'before') { if (-not ($script:State.PSObject.Properties.Name -contains $k)) { $script:State | Add-Member $k $null } }
    if ($null -eq $script:State.stepsDone) { $script:State.stepsDone = @() }
    if ($null -eq $script:State.skip) { $script:State.skip = @() }
}
function Save-State { $script:State | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $script:Dir 'state.json') -Encoding UTF8 }
function Set-StepDone([string]$s) { $script:State.stepsDone = @(@($script:State.stepsDone) + $s | Sort-Object -Unique); Save-State }
function Test-StepDone([string]$s) { return (@($script:State.stepsDone) -contains $s) }

function Add-Change([hashtable]$h) {
    $h['time'] = (Get-Date).ToString('s')
    $h['device'] = $script:Dev
    Add-Content -Path (Join-Path $script:Dir 'changes.jsonl') -Value ($h | ConvertTo-Json -Compress) -Encoding UTF8
}
function Get-Changes {
    $f = Join-Path $script:Dir 'changes.jsonl'
    if (-not (Test-Path $f)) { return @() }
    return @(Get-Content $f | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
}

# Snapshot of everything we might change, taken once per TV before the first change.
function Save-Snapshot {
    $f = Join-Path $script:Dir 'snapshot-original.json'
    if (Test-Path $f) { return }
    $snap = [ordered]@{
        time = (Get-Date).ToString('s'); model = $script:Info.Model; android = $script:Info.Android
        home = $script:Info.Home; disabled = $script:Info.Disabled
        accessibility = (Sh1 'settings get secure enabled_accessibility_services')
        anim = [ordered]@{}
    }
    foreach ($k in 'window_animation_scale', 'transition_animation_scale', 'animator_duration_scale', 'mitv_fast_cur_anim_scale') {
        $snap.anim[$k] = Sh1 "settings get global $k"
    }
    $snap | ConvertTo-Json -Depth 4 | Set-Content $f -Encoding UTF8
}

# ------------------------------------------------------------------ catalog
function Read-Catalog {
    return @(Import-Csv (Join-Path $script:Root 'catalog/packages.csv'))
}
function Read-Questions {
    return (Get-Content (Join-Path $script:Root 'catalog/questions.json') -Raw | ConvertFrom-Json).questions
}
function Read-BackgroundCatalog {
    return @(Import-Csv (Join-Path $script:Root 'catalog/background.csv'))
}

# Matches the catalog against what is installed. Returns one object per installed catalog package.
function Get-CatalogMatches($catalog) {
    $res = New-Object System.Collections.ArrayList
    $seen = @{}
    foreach ($row in $catalog) {
        $hits = if ($row.package.Contains('*')) { @($script:Info.All | Where-Object { $_ -like $row.package }) } else { @($script:Info.All | Where-Object { $_ -eq $row.package }) }
        foreach ($p in $hits) {
            if ($seen.ContainsKey($p)) { continue }
            $seen[$p] = $true
            [void]$res.Add([pscustomobject]@{ Package = $p; Risk = $row.risk; Feature = $row.feature; What = $row.what; Verified = $row.verified; AlreadyOff = ($script:Info.Disabled -contains $p) })
        }
    }
    return $res
}

# Answer lookup: answers file > auto (recommendation) > ask.
function Get-Answer([string]$section, [string]$key) {
    if ($null -eq $script:Ans) { return $null }
    $s = $script:Ans.$section
    if ($null -eq $s) { return $null }
    if ($key) { return $s.$key }
    return $s
}

# ------------------------------------------------------------------ health check
function Get-HealthBaseline {
    return [pscustomobject]@{
        SystemServer = Sh1 'pidof system_server'
        Crashes = [int](Sh1 'logcat -b crash -d 2>/dev/null | grep -c FATAL')
    }
}

# Automatic checks after a batch: Android didn't restart itself, Home still works, no setup/recovery screen.
function Test-TvHealth($base) {
    $problems = @()
    if ((Get-AdbState $script:Dev) -ne 'device') { return @('lost the connection to the TV') }
    $ss = Sh1 'pidof system_server'
    if ($ss -and $base.SystemServer -and $ss -ne $base.SystemServer) { $problems += 'Android restarted itself (system_server changed)' }
    $homeApp = Get-HomeApp $script:Info.Sdk
    if (-not $homeApp) { $problems += 'no Home app is set any more' }
    elseif ((Sh 'pm list packages -d') -contains "package:$homeApp") { $problems += "the Home app ($homeApp) got disabled" }
    [void](Sh 'input keyevent KEYCODE_HOME')
    Start-Sleep -Seconds 3
    $focus = Sh1 'dumpsys window 2>/dev/null | grep -m1 -E ''mCurrentFocus|mFocusedWindow'''
    if ($focus -match 'Recovery|setupwraith|SetupWizard|Not Responding|isn.t responding|Application Error') { $problems += "the TV shows: $focus" }
    $crashes = [int](Sh1 'logcat -b crash -d 2>/dev/null | grep -c FATAL')
    if ($crashes -gt $base.Crashes) {
        $who = (Sh 'logcat -b crash -d 2>/dev/null | grep -A1 FATAL | grep -oE ''Process: [a-zA-Z0-9_.:]+''' | Select-Object -Last 3) -join ', '
        Warn "Something crashed during this batch ($who). Checking whether it matters..."
        if ($who -match [regex]::Escape($homeApp) -or $who -match 'systemui|launcher') { $problems += "crash in $who" }
    }
    return $problems
}

# adb push that fails loudly instead of leaving an old file on the TV.
function Send-File([string]$local, [string]$remote) {
    $out = (Invoke-Adb -s $script:Dev push $local $remote) -join ' '
    if ($out -notmatch 'pushed') { throw "Could not copy $([IO.Path]::GetFileName($local)) to the TV: $out" }
}
