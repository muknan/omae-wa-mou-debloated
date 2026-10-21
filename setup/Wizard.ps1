<#
.SYNOPSIS
  omae-wa-mou-debloated setup wizard. Connects to an Android / Google TV, asks a few questions and does the rest.
  Everything it changes is logged in my-tv\<your TV>\ and can be undone with -Undo.

.EXAMPLE  .\Start-Here.cmd                                   # normal use: double-click it
.EXAMPLE  .\setup\Wizard.ps1 -Device 192.168.1.50 -Plan       # detect + show the plan, change nothing (writes plan.json)
.EXAMPLE  .\setup\Wizard.ps1 -Device 192.168.1.50 -Answers my-answers.json -Auto   # no questions (for AI assistants)
.EXAMPLE  .\setup\Wizard.ps1 -Device 192.168.1.50 -Undo
.EXAMPLE  .\setup\Wizard.ps1 -Device 192.168.1.50 -PairCode 123456 [-PairPort 37123]  # pair ADB Auto Enable later
#>
param(
    [string]$Device,
    [string]$Answers,
    [switch]$Auto,
    [switch]$Plan,
    [string[]]$Steps,          # debloat, background, stick, remote, launcher (commas or spaces)
    [switch]$Undo,
    [switch]$Status,
    [switch]$NoReboot,
    [string]$PairCode,
    [int]$PairPort
)
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'lib.ps1')

$StepNames = [ordered]@{
    debloat    = 'Switch off junk apps (ads, tracking, demo mode)'
    background = 'Keep apps asleep when you are not using them + faster menus'
    stick      = 'Make it survive restarts (installs small free helper apps on the TV)'
    remote     = 'A "fresh boot" button on your remote (Key Mapper)'
    launcher   = 'Ad-free home screen with rotating wallpapers (Projectivy)'
    extras     = 'Small extras: ad-blocking DNS, YouTube links open in SmartTube'
}
$Recommended = @('debloat', 'background', 'stick')

# ====================================================================== connect
function Show-DebugHelp([int]$brand) {
    Say ''
    Say '  Turn on debugging on the TV (one time only):' White
    if ($brand -eq 5) {
        Info '1. Settings > My Fire TV > About. Highlight your device name and press OK 7 times.'
        Info '2. Back to My Fire TV > Developer options > turn on ADB debugging.'
    } elseif ($brand -eq 6) {
        Info '1. Settings > Device Preferences > About. Press OK on "Build" 7 times.'
        Info '2. Back to Device Preferences > Developer options > turn on Network debugging.'
    } else {
        Info '1. Settings > System > About. Scroll to "Android TV OS build" and press OK on it 7 times'
        Info '   until it says "You are now a developer".'
        Info '   (Older Android TVs: Settings > Device Preferences > About > Build.)'
        Info '2. Go back to Settings > System > Developer options (or Device Preferences > Developer options).'
        Info '3. Turn on USB debugging. If you also see "Wireless debugging" or "Network debugging", turn that on too.'
    }
    Info 'Also note the TV''s IP address: Settings > Network & Internet > your Wi-Fi (looks like 192.168.1.50).'
}

function Find-Tvs {
    $found = New-Object System.Collections.ArrayList
    $mine = @()
    foreach ($ni in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($ni.OperationalStatus -ne 'Up' -or $ni.NetworkInterfaceType -eq 'Loopback') { continue }
        foreach ($u in $ni.GetIPProperties().UnicastAddresses) {
            if ($u.Address.AddressFamily -eq 'InterNetwork' -and $u.Address.ToString() -match '^(10|172\.(1[6-9]|2\d|3[01])|192\.168)\.') { $mine += $u.Address.ToString() }
        }
    }
    foreach ($ip in ($mine | Sort-Object -Unique)) {
        $base = $ip -replace '\.\d+$', ''
        $probes = @()
        for ($i = 1; $i -le 254; $i++) {
            $c = New-Object Net.Sockets.TcpClient
            $probes += [pscustomobject]@{ Ip = "$base.$i"; Client = $c; Task = $c.ConnectAsync("$base.$i", 5555) }
        }
        try { [void][Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($probes.Task), 2500) } catch { }
        foreach ($p in $probes) {
            if ($p.Task.Status -eq 'RanToCompletion' -and $p.Client.Connected -and $p.Ip -ne $ip) { [void]$found.Add([pscustomobject]@{ Ip = $p.Ip; Kind = 'port5555' }) }
            try { $p.Client.Close() } catch { }
        }
    }
    foreach ($l in (Invoke-Adb mdns services)) {
        if ($l -match '\s_adb\._tcp\S*\s+(\d+\.\d+\.\d+\.\d+):5555') {
            if (-not ($found | Where-Object { $_.Ip -eq $matches[1] })) { [void]$found.Add([pscustomobject]@{ Ip = $matches[1]; Kind = 'port5555' }) }
        }
        if ($l -match '_adb-tls-connect\._tcp\S*\s+(\d+\.\d+\.\d+\.\d+):(\d+)') {
            if (-not ($found | Where-Object { $_.Ip -eq $matches[1] })) { [void]$found.Add([pscustomobject]@{ Ip = $matches[1]; Kind = "wireless:$($matches[2])" }) }
        }
    }
    return $found
}

function Connect-Serial([string]$serial) {
    $script:LastConnect = 'refused'
    $out = (Invoke-Adb connect $serial) -join ' '
    if ($out -notmatch 'connected to|authenticate') { return $false }
    $script:LastConnect = 'unauthorized'
    $told = $false
    for ($i = 0; $i -lt 60; $i++) {
        $st = Get-AdbState $serial
        if ($st -eq 'device') { $script:Dev = $serial; return $true }
        if ($st -eq 'unauthorized' -and -not $told) {
            OnTv 'A box "Allow USB debugging?" appears. Tick "Always allow from this computer" and choose Allow.'
            $told = $true
        }
        Start-Sleep -Seconds 2
        if ($st -eq 'none' -or $st -eq 'offline') { [void](Invoke-Adb connect $serial) }
    }
    return $false
}

function Find-MdnsPort([string]$ip, [string]$kind, [int]$seconds) {
    for ($i = 0; $i -lt $seconds; $i += 2) {
        foreach ($l in (Invoke-Adb mdns services)) {
            if ($l -match "_adb-tls-$kind\S*\s+$([regex]::Escape($ip)):(\d+)") { return [int]$matches[1] }
        }
        Start-Sleep -Seconds 2
    }
    return 0
}

function Connect-Pairing([string]$ip) {
    Say ''
    Say '  This TV wants a pairing code (newer Android). One time only:' White
    OnTv 'Settings > System > Developer options > Wireless debugging: turn it on, then choose "Pair device with pairing code".'
    Info 'A box shows a 6-digit code and an address like 192.168.1.50:37123. Leave it open.'
    Wait-Enter 'Press Enter when you see the code'
    $port = Find-MdnsPort $ip 'pairing' 10
    $addr = if ($port) { "${ip}:$port" } else { (Read-Host '  Type the address shown under the code (like 192.168.1.50:37123)').Trim() }
    $code = (Read-Host '  Type the 6-digit code').Trim()
    $out = (Invoke-Adb pair $addr $code) -join ' '
    if ($out -notmatch 'Successfully paired') { Bad "Pairing failed: $out"; return $false }
    Ok 'Paired'
    $cport = Find-MdnsPort $ip 'connect' 10
    $caddr = if ($cport) { "${ip}:$cport" } else { (Read-Host '  Type the "IP address & Port" shown on the Wireless debugging screen (not the pairing box)').Trim() }
    if (-not (Connect-Serial $caddr)) { Bad "Could not connect to $caddr"; return $false }
    # Switch to the classic port 5555: it doesn't change, and the boot helper uses it.
    [void](Invoke-Adb -s $caddr tcpip 5555)
    Start-Sleep -Seconds 4
    if (Connect-Serial "${ip}:5555") { Ok "Switched to ${ip}:5555"; return $true }
    $script:Dev = $caddr
    Warn "Staying on $caddr. This port changes after a restart; run the wizard again if it can't connect later."
    return $true
}

function Connect-Tv {
    if ($Device) {
        $d = if ($Device -match ':\d+$') { $Device } else { "${Device}:5555" }
        if (Connect-Serial $d) { return }
        if ($script:LastConnect -eq 'unauthorized') { throw "The TV didn't allow this computer. Run again and press Allow on the TV (tick 'Always allow')." }
        if ($script:AutoMode) { throw "Can't connect to $d. Is debugging on, and did you allow this computer on the TV?" }
        if (Connect-Pairing ($d -replace ':\d+$', '')) { return }
        throw "Can't connect to $d."
    }
    $brands = 'Xiaomi / Redmi', 'TCL', 'Sony', 'Philips', 'Hisense', 'Amazon Fire TV', 'NVIDIA Shield', 'Chromecast / Google TV Streamer', 'onn', 'Other Google TV or Android TV'
    $b = Ask-Choice 'Which TV or streaming box is it?' $brands 0
    Show-DebugHelp $b
    Wait-Enter 'Press Enter when debugging is on'
    while ($true) {
        Info 'Looking for your TV on the network...'
        $tvs = @(Find-Tvs)
        $opts = @($tvs | ForEach-Object { if ($_.Kind -eq 'port5555') { "$($_.Ip)  (ready)" } else { "$($_.Ip)  (wireless debugging, needs a pairing code)" } })
        $opts += 'Type the IP address myself'
        $opts += 'Search again'
        $c = if ($tvs.Count -gt 0) { Ask-Choice 'Which one is your TV?' $opts 0 } else { Warn 'No TV found automatically.'; Ask-Choice 'What now?' $opts 0 }
        if ($c -eq $opts.Count - 1) { continue }
        $ip = if ($c -eq $opts.Count - 2) { (Read-Host '  TV IP address').Trim() -replace ':\d+$', '' } else { $tvs[$c].Ip }
        if (Connect-Serial "${ip}:5555") { return }
        if ($script:LastConnect -eq 'unauthorized') { Warn 'The TV did not allow this computer yet. Choose your TV again and press Allow on the TV.'; continue }
        if (Connect-Pairing $ip) { return }
        Warn 'That did not work. Check that debugging is still on (it switches off if the TV loses Wi-Fi) and try again.'
    }
}

function Show-TvCard {
    $i = $script:Info
    Say ''
    Ok ("Connected: {0} {1}  |  {2}, Android {3}  |  {4} GB RAM  |  {5}" -f $i.Maker, $i.Model, $i.Platform, $i.Android, ([math]::Ceiling($i.RamMb / 512) / 2), $i.Abi)
    Info "Home app: $($i.Home)   |   apps you installed: $($i.User.Count)   |   already switched off: $($i.Disabled.Count)"
    if ($i.Sdk -lt 28) { Warn "Android $($i.Android) is old: junk apps can be switched off, but keeping apps asleep needs Android 9 or newer." }
    if ($i.Platform -eq 'Fire TV') { Info 'Fire TV: the junk-app and sleep steps work; the Projectivy home screen step does not (Fire OS blocks other launchers).' }
}

# ====================================================================== step 1: debloat
function Get-FeatureAnswer($q, $pkgs) {
    $rec = $q.recommend
    if ($rec -eq 'keep-if-ram-over-3gb') { $rec = if ($script:Info.RamMb -gt 3200) { 'keep' } else { 'off' } }
    $a = Get-Answer 'features' $q.id
    if ($a) { return $a }
    if ($script:AutoMode) { return $rec }
    Say ''
    Say "  $($q.ask)" White
    Info $q.help
    Info ('On this TV that covers: ' + (($pkgs | Select-Object -First 4 | ForEach-Object { $_.What }) -join '; '))
    $useIt = Ask-YesNo 'Yes = keep them, No = switch them off.' ($rec -eq 'keep')
    if ($useIt) { return 'keep' } else { return 'off' }
}

function Get-AppCandidates($matched) {
    $catPkgs = @($matched | ForEach-Object { $_.Package })
    $bg = Read-BackgroundCatalog
    $prot = @(Get-ProtectedSystemApps) + @($bg | Where-Object { $_.kind -eq 'protect' } | ForEach-Object { $_.package })
    $skipRx = 'settings|setup|launcher|livetv|\.tv$|tvinput|input|source|player|explorer|vending|filemanager|files|cast|share|remote|bluetooth|update|ota|upgrade|inputmethod|keyboard|projengmenu'
    return @(Get-LauncherApps | Where-Object {
        ($script:Info.System -contains $_) -and ($script:Info.Disabled -notcontains $_) -and ($catPkgs -notcontains $_) -and
        ($prot -notcontains $_) -and ($_ -notmatch $skipRx)
    })
}

function Step-Debloat {
    Title 'Step 1' $StepNames.debloat
    $matched = @(Get-CatalogMatches (Read-Catalog))
    $low = @($matched | Where-Object { $_.Risk -eq 'low' -and -not $_.AlreadyOff })
    $med = @($matched | Where-Object { $_.Risk -eq 'medium' -and -not $_.AlreadyOff })
    $keep = @($matched | Where-Object { $_.Risk -eq 'keep' })
    $already = @($matched | Where-Object { $_.AlreadyOff -and $_.Risk -ne 'keep' })
    $unknown = $script:Info.System.Count - $matched.Count
    Info "Checked $($script:Info.All.Count) packages against the catalog:"
    Ok "$($low.Count) junk (safe to switch off for everyone)"
    Ok "$($med.Count) depend on how you use the TV (a few quick questions)"
    Ok "$($keep.Count) important ones that are never touched (inputs, remote, updates, casting...)"
    if ($already.Count) { Ok "$($already.Count) already switched off" }
    Info "The other $unknown system packages aren't in the catalog, so they are left alone. Better safe than bricked."

    $off = New-Object System.Collections.ArrayList
    foreach ($p in $low) { [void]$off.Add([pscustomobject]@{ Package = $p.Package; What = $p.What; Why = 'junk'; Verified = $p.Verified }) }

    $features = [ordered]@{}
    foreach ($q in (Read-Questions)) {
        $pk = @($med | Where-Object { $_.Feature -eq $q.id })
        if (-not $pk.Count) { continue }
        $ans = Get-FeatureAnswer $q $pk
        $features[$q.id] = $ans
        if ($ans -eq 'off') { foreach ($p in $pk) { [void]$off.Add([pscustomobject]@{ Package = $p.Package; What = $p.What; Why = "you don't use it ($($q.id))"; Verified = $p.Verified }) } }
    }

    $cands = @(Get-AppCandidates $matched)
    $picked = @()
    $preset = Get-Answer 'disableApps' $null
    if ($preset) { $picked = @($cands | Where-Object { @($preset) -contains $_ }) }
    elseif ($cands.Count -and -not $script:AutoMode) {
        Say ''
        Say '  Apps that came with the TV. Any you NEVER use can be switched off too (they come back with Undo):' White
        $names = @{}; Read-BackgroundCatalog | ForEach-Object { $names[$_.package] = $_.what }
        for ($i = 0; $i -lt $cands.Count; $i++) {
            $n = if ($names.ContainsKey($cands[$i])) { "  ($($names[$cands[$i]]))" } else { '' }
            Say ("    {0,2}) {1}{2}" -f ($i + 1), $cands[$i], $n)
        }
        $a = Read-Host '  Type the numbers to switch off (e.g. 2 5), or press Enter to keep them all'
        foreach ($t in ($a -split '[\s,]+' | Where-Object { $_ -match '^\d+$' })) {
            $n = [int]$t; if ($n -ge 1 -and $n -le $cands.Count) { $picked += $cands[$n - 1] }
        }
    }
    foreach ($p in ($picked | Sort-Object -Unique)) { [void]$off.Add([pscustomobject]@{ Package = $p; What = 'preinstalled app'; Why = 'you picked it'; Verified = '' }) }

    $skip = @($script:State.skip) + @(Get-Answer 'skipPackages' $null)
    $final = @($off | Where-Object { $skip -notcontains $_.Package })
    if (-not $final.Count) { Ok 'Nothing to switch off.'; Set-StepDone 'debloat'; return }

    Say ''
    Say "  Plan: switch off $($final.Count) package(s). Nothing is deleted; Undo brings them back." White
    foreach ($f in $final) { Say ("    - {0,-52} {1}  [{2}]" -f $f.Package, $f.What, $f.Why) DarkGray }
    $mode = if ($script:AutoMode) { 0 } else { Ask-Choice 'Go ahead?' @('Yes, do it (3 at a time, checking the TV after each)', 'Let me approve each one', 'Skip this step') 0 }
    if ($mode -eq 2) { return }
    if ($mode -eq 1) { $final = @($final | Where-Object { Ask-YesNo "Switch off $($_.Package) ($($_.What))?" $true }) }

    Info 'After each batch the wizard presses Home on the TV and checks that everything still works.'
    $base = Get-HealthBaseline
    $batches = [math]::Ceiling($final.Count / 3)
    for ($b = 0; $b -lt $batches; $b++) {
        $batch = @($final | Select-Object -Skip ($b * 3) -First 3)
        $done = @()
        foreach ($f in $batch) {
            $out = Sh1 "pm disable-user --user 0 $($f.Package)"
            if ($out -match 'disabled-user') { $done += $f.Package; Add-Change @{ type = 'pm-disable'; package = $f.Package; why = $f.Why } }
            else { Warn "$($f.Package): $out" }
        }
        $problems = @(Test-TvHealth $base)
        $undo = $false
        if ($problems.Count) {
            foreach ($p in $problems) { Bad $p }
            $undo = $true
        } else {
            Ok ("Batch {0}/{1} fine: {2}" -f ($b + 1), $batches, ($done -join ', '))
            $k = Read-KeyTimeout 6 'Quick look at the TV. Press U to undo this batch.'
            if ($k -match '^[uU]') { $undo = $true }
        }
        if ($undo) {
            foreach ($p in $done) { [void](Sh "pm enable --user 0 $p"); Add-Change @{ type = 'pm-enable'; package = $p; why = 'rolled back' } }
            $script:State.skip = @(@($script:State.skip) + $done | Sort-Object -Unique); Save-State
            Warn "Rolled back: $($done -join ', '). They'll be skipped on this TV from now on."
            Start-Sleep -Seconds 5
            $base = Get-HealthBaseline
        }
    }
    $script:State | Add-Member -Force features $features
    Set-StepDone 'debloat'
}

# ====================================================================== step 2: background apps
function Get-BackgroundGroups {
    $bg = Read-BackgroundCatalog
    $kind = @{}; $names = @{}
    foreach ($r in $bg) { $kind[$r.package] = $r.kind; $names[$r.package] = $r.what }
    $vpn = @(Get-ServicePackages 'android.net.VpnService')
    $tvin = @(Get-ServicePackages 'android.media.tv.TvInputService')
    $auto = @(Get-ProtectedSystemApps) + @(Get-ServicePackages 'tv.projectivy.plugin.WALLPAPER_PROVIDER')
    $rows = New-Object System.Collections.ArrayList
    foreach ($p in $script:Info.User) {
        if ($auto -contains $p) { continue }
        $g = 'asleep'; $why = ''
        if ($kind[$p] -in 'protect', 'receiver') { $g = 'protect'; $why = $names[$p] }
        elseif ($tvin -contains $p) { $g = 'protect'; $why = 'provides TV channels' }
        elseif ($kind[$p] -eq 'music') { $g = 'stoponly'; $why = 'music' }
        elseif ($vpn -contains $p) { $g = 'stoponly'; $why = 'VPN' }
        [void]$rows.Add([pscustomobject]@{ Package = $p; Group = $g; Why = $why; Pre = $false })
    }
    foreach ($r in $bg | Where-Object { $_.kind -eq 'stream' }) {
        if ($script:Info.System -contains $r.package -and $script:Info.Disabled -notcontains $r.package) {
            [void]$rows.Add([pscustomobject]@{ Package = $r.package; Group = 'asleep'; Why = 'came with the TV'; Pre = $true })
        }
    }
    foreach ($g in 'asleep', 'stoponly', 'protect') {
        foreach ($p in @(Get-Answer 'background' $g)) { $rows | Where-Object { $_.Package -eq $p } | ForEach-Object { $_.Group = $g } }
    }
    return [pscustomobject]@{ Rows = $rows; Auto = ($auto | Sort-Object -Unique) }
}

function New-ConfText($c) {
    $l = @('# Written by the omae-wa-mou-debloated setup wizard. Re-run the wizard to change it.')
    foreach ($k in 'ANIM_SCALE', 'INCLUDE_USER_APPS', 'PREINSTALLED_APPS', 'STOP_ONLY', 'EXTRA_PROTECT', 'XIAOMI_ANIM_KEY', 'UNWHITELIST_NETFLIX', 'START_KEYMAPPER_EXPERT_MODE', 'QUIET_APPS', 'DAILY_REFRESH', 'DAILY_REFRESH_HOUR', 'WIFI_STAY_CONNECTED') {
        if ($null -eq $c.$k) { continue }
        $l += ('{0}="{1}"' -f $k, $c.$k)
    }
    return ($l -join "`n") + "`n"
}

function Publish-Agent {
    $c = $script:State.conf
    $f = Join-Path $script:Dir 'tv-apply.conf'
    Write-Text $f (New-ConfText $c)
    $hadOwn = Sh1 '[ -f /data/local/tmp/tv-apply.sh ] && [ ! -f /data/local/tmp/tv-apply.conf ] && echo yes'
    if ($hadOwn -eq 'yes') { [void](Sh 'cp /data/local/tmp/tv-apply.sh /data/local/tmp/tv-apply.sh.bak'); Info 'Kept a copy of the existing tv-apply.sh as tv-apply.sh.bak' }
    foreach ($s in 'tv-apply.sh', 'tv-refresh.sh', 'tv-daily.sh') { Send-File ([IO.Path]::Combine($script:Root, 'scripts', 'device', $s)) "/data/local/tmp/$s" }
    Send-File $f /data/local/tmp/tv-apply.conf
    [void](Sh 'chmod 755 /data/local/tmp/tv-apply.sh /data/local/tmp/tv-refresh.sh /data/local/tmp/tv-daily.sh; chmod 644 /data/local/tmp/tv-apply.conf')
    $out = Sh 'sh /data/local/tmp/tv-apply.sh; tail -n 40 /data/local/tmp/tv-apply.log'
    return $out
}

function Step-Background {
    Title 'Step 2' $StepNames.background
    if ($script:Info.Sdk -lt 28) { Warn 'Needs Android 9 or newer. Skipping.'; return }
    Info 'Apps you installed stop running in the background until you open them. They still work normally.'
    Info 'Your launcher, keyboard, accessibility apps and whatever is on screen are never touched.'
    $g = Get-BackgroundGroups
    $rows = $g.Rows
    $labels = @{ asleep = 'asleep until opened'; stoponly = 'stopped at startup, may run after you open it'; protect = 'left alone' }
    while (-not $script:AutoMode) {
        Say ''
        $i = 0
        foreach ($grp in 'asleep', 'stoponly', 'protect') {
            $members = @($rows | Where-Object { $_.Group -eq $grp })
            if (-not $members.Count) { continue }
            Say "  $($labels[$grp].ToUpper()):" White
            foreach ($r in $members) { $i++; $r | Add-Member -Force Num $i; Say ("    {0,2}) {1}{2}" -f $i, $r.Package, $(if ($r.Why) { "  ($($r.Why))" } else { '' })) }
        }
        Info 'Music players and VPNs are "stopped at startup" so they can keep playing/connected after you start them.'
        $a = Read-Host '  Press Enter if this looks right, or type numbers to move apps to the next group'
        if ([string]::IsNullOrWhiteSpace($a)) { break }
        foreach ($t in ($a -split '[\s,]+' | Where-Object { $_ -match '^\d+$' })) {
            $r = $rows | Where-Object { $_.Num -eq [int]$t }
            if ($r) { $r.Group = @{ asleep = 'stoponly'; stoponly = 'protect'; protect = 'asleep' }[$r.Group] }
        }
    }
    $anim = Get-Answer 'animation' $null
    if ($null -eq $anim) {
        $choice = Ask-Choice 'Menu animation speed?' @('Normal (1x)', 'Faster (0.5x) - feels snappier', 'Off') 1
        $anim = @('1.0', '0.5', '0')[$choice]
    }
    $daily = Get-Answer 'dailyRefresh' $null
    if ($null -eq $daily) { $daily = Ask-YesNo 'Also refresh once a day by itself, like pressing the refresh button? (from 4:00, never while something plays)' $true }
    $dailyHour = Get-Answer 'dailyRefreshHour' $null
    if ($null -eq $dailyHour) { $dailyHour = 4 }
    $helpers = 'com.termux com.termux.boot com.tpn.adbautoenable'
    $c = [pscustomobject]@{
        ANIM_SCALE                  = "$anim"
        INCLUDE_USER_APPS           = '1'
        PREINSTALLED_APPS           = (@($rows | Where-Object { $_.Pre -and $_.Group -eq 'asleep' } | ForEach-Object { $_.Package }) -join ' ')
        STOP_ONLY                   = (@($rows | Where-Object { $_.Group -eq 'stoponly' } | ForEach-Object { $_.Package }) -join ' ')
        EXTRA_PROTECT               = ((@($helpers -split ' ') + @($rows | Where-Object { $_.Group -eq 'protect' } | ForEach-Object { $_.Package }) | Sort-Object -Unique) -join ' ')
        XIAOMI_ANIM_KEY             = $(if ((Sh1 'settings get global mitv_fast_cur_anim_scale') -notmatch 'null') { '1' } else { '0' })
        UNWHITELIST_NETFLIX         = $(if ($script:Info.All -contains 'com.netflix.ninja') { '1' } else { '0' })
        START_KEYMAPPER_EXPERT_MODE = $(if ($script:Info.All -contains 'io.github.sds100.keymapper') { '1' } else { '0' })
        QUIET_APPS                  = 'io.github.sds100.keymapper com.tpn.adbautoenable'
        DAILY_REFRESH               = $(if ($daily) { '1' } else { '0' })
        DAILY_REFRESH_HOUR          = "$([int]$dailyHour)"
        WIFI_STAY_CONNECTED         = '1'
    }
    $script:State.conf = $c; Save-State
    Add-Change @{ type = 'deploy'; anim = "$anim"; stopOnly = $c.STOP_ONLY; protect = $c.EXTRA_PROTECT; preinstalled = $c.PREINSTALLED_APPS }
    $out = Publish-Agent
    $last = $out | Where-Object { $_ -match 'apply start' } | Select-Object -Last 1
    $stopped = @($out | Where-Object { $_ -match 'force-stopped running' }).Count
    if ($last -match '(\d+) blocked \+ (\d+) stop-only') { Ok "$($matches[1]) apps kept asleep, $($matches[2]) stopped at startup, $stopped were running and got put to sleep just now" }
    else { Warn 'Could not read the result. Log:'; $out | Select-Object -Last 5 | ForEach-Object { Info $_ } }
    Info 'Note: Android forgets these background limits when the TV restarts. Step 3 makes the TV re-apply them by itself.'
    Set-StepDone 'background'
}

# ====================================================================== step 3: make it stick
function Wait-TermuxShell {
    for ($i = 0; $i -lt 90; $i++) {
        $user = ((Sh1 'ps -A -o USER,NAME | grep -m1 -E '' com\.termux$''') -split '\s+')[0]
        if ($user -and [int](Sh1 "ps -A -o USER,NAME | grep -cE '^$user +(bash|login)$'") -gt 0) { return $true }
        if ($i -eq 3) { Info 'Termux is setting itself up on the TV (the first start takes a minute)...' }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Step-Stick {
    Title 'Step 3' $StepNames.stick
    if ($script:Info.Sdk -lt 24) { Warn "Android $($script:Info.Android) is too old for Termux. Skipping."; return }
    if (-not (Test-StepDone 'background')) { Warn 'Do step 2 first (this step re-applies what step 2 sets up).'; return }
    Info 'Installs Termux + Termux:Boot (free, open source). At every restart they quietly re-apply your settings,'
    Info 'put background apps back to sleep, then close themselves. Downloads come from the official GitHub releases'
    Info 'and are checked against their published SHA-256 before installing.'
    if (-not (Ask-YesNo 'Install them?' $true)) { return }
    try { [void](Install-Helper 'termux') } catch {
        Bad $_.Exception.Message
        if ("$_" -match 'INCOMPATIBLE|SHARED_USER|signatures') { Info 'Termux is already installed from another store. Uninstall it on the TV and run this step again.' }
        return
    }
    try { [void](Install-Helper 'termux-boot') } catch { Bad $_.Exception.Message; return }
    [void](Sh 'pm grant com.termux android.permission.READ_EXTERNAL_STORAGE; pm grant com.termux android.permission.WRITE_EXTERNAL_STORAGE')
    # Keep power saving from cutting the boot script short (removed again when Termux is uninstalled).
    [void](Sh 'cmd deviceidle whitelist +com.termux >/dev/null 2>&1')
    $st = '/sdcard/Download/tvagent'
    [void](Sh "mkdir -p $st; rm -f $st/status $st/setup.log")
    foreach ($s in 'setup.sh', '10-tv-agent.sh') { Send-File ([IO.Path]::Combine($script:Root, 'scripts', 'termux', $s)) "$st/$s" }
    # Termux:Boot must be opened once, or Android never tells it about restarts.
    [void](Sh 'monkey -p com.termux.boot -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1; am start -n com.termux.boot/.BootActivity >/dev/null 2>&1')
    Start-Sleep -Seconds 3
    [void](Sh 'am start -n com.termux/.app.TermuxActivity')
    if (-not (Wait-TermuxShell)) { Bad 'Termux did not finish starting. Open Termux on the TV once, then run this step again.'; return }
    Start-Sleep -Seconds 2
    Info 'Typing the setup command into Termux on the TV...'
    $typed = 0
    $status = ''
    $seen = 0
    for ($i = 0; $i -lt 400; $i++) {
        if ($typed -eq 0 -or ($i -eq 15 -and -not $status)) {
            if ($typed) { [void](Sh 'am start -n com.termux/.app.TermuxActivity; input keyevent 66') }
            [void](Sh "input text 'bash%s$st/setup.sh'"); [void](Sh 'input keyevent 66'); $typed++
        }
        if ($i -eq 40 -and -not $status) {
            OnTv "Termux didn't pick up the command. Open Termux on the TV and type:  bash $st/setup.sh  then press Enter."
        }
        Start-Sleep -Seconds 3
        $status = Sh1 "cat $st/status 2>/dev/null"
        $log = @(Sh "cat $st/setup.log 2>/dev/null")
        foreach ($l in ($log | Select-Object -Skip $seen)) {
            if ($l -match '^>>> (.*)') {
                if ($l -match 'Approve') { OnTv 'If a box "Allow USB debugging?" appears, tick "Always allow from this computer" and choose Allow. (No box = already allowed.)' }
                else { Info $matches[1] }
            }
        }
        $seen = $log.Count
        if ($status -eq 'ok' -or $status -match '^fail') { break }
    }
    [void](Sh 'input keyevent KEYCODE_HOME; am force-stop com.termux')
    if ($status -ne 'ok') {
        Bad "Termux setup did not finish: $status"
        Info 'Most common causes: no internet on the TV, or the Allow box was dismissed. Run this step again.'
        return
    }
    [void](Sh "rm -rf $st")
    Ok 'Boot helper installed. At every restart the TV re-applies everything by itself, no PC needed.'
    if ($script:Info.Sdk -ge 34) { Step-AdbAutoEnable }
    Set-StepDone 'stick'
}

function Invoke-AaePair([string]$code, [int]$port) {
    $ip = $script:Dev -replace ':\d+$', ''
    if (-not $port) { $port = Find-MdnsPort $ip 'pairing' 20 }
    if (-not $port) { $port = [int](Read-Host '  Type the port number from the pairing box (the digits after the colon)') }
    try {
        $r = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:19093/api/pair' -Body @{ port = "$port"; code = $code } -TimeoutSec 30
        if ($r.success) {
            [void](Sh 'pm grant com.tpn.adbautoenable android.permission.WRITE_SECURE_SETTINGS')
            Ok 'ADB Auto Enable is paired. Debugging now switches itself back on after restarts and Wi-Fi drops.'
            return $true
        }
        Bad "Pairing failed: $($r.error)"
    } catch { Bad "Pairing failed: $($_.Exception.Message)" }
    return $false
}

function Connect-AaeApi([bool]$launch = $true) {
    [void](Invoke-Adb -s $script:Dev forward tcp:19093 tcp:9093)
    try { return (Invoke-RestMethod -Uri 'http://127.0.0.1:19093/api/status' -TimeoutSec 5) } catch { }
    if (-not $launch) { return $null }
    # Not running yet: opening the app once starts its service. (Never while the TV shows a pairing box:
    # switching apps closes it.)
    [void](Sh 'am start -n com.tpn.adbautoenable/.MainActivity >/dev/null 2>&1')
    Start-Sleep -Seconds 4
    [void](Sh 'input keyevent KEYCODE_HOME')
    for ($i = 0; $i -lt 6; $i++) {
        try { return (Invoke-RestMethod -Uri 'http://127.0.0.1:19093/api/status' -TimeoutSec 5) } catch { Start-Sleep -Seconds 3 }
    }
    return $null
}

function Step-AdbAutoEnable {
    Say ''
    Info 'Android 14+ switches wireless debugging off after a restart or a Wi-Fi drop, which would stop the boot helper.'
    Info 'ADB Auto Enable (free, open source) switches it back on by itself.'
    try { $aae = Install-Helper 'adb-auto-enable' } catch { Bad $_.Exception.Message; return }
    # Only open the app right after installing it (that starts its service once; after that it starts at boot).
    $s = Connect-AaeApi (-not $aae.Already)
    Set-Quiet 'com.tpn.adbautoenable'
    if ($null -eq $s -or $null -eq $s.isPaired) {
        Warn "Couldn't reach ADB Auto Enable's setup page (some TVs, like Xiaomi, already use its port)."
        Info 'Often that''s fine: those TVs keep debugging on port 5555 by themselves. The test restart at the end will tell.'
        Info "If the restart test fails, open http://$($script:Dev -replace ':\d+$', ''):9093 in a phone or PC browser and use 'Pair Device'."
        return
    }
    if ($s.isPaired) { Ok 'ADB Auto Enable is already paired.'; return }
    OnTv 'Settings > System > Developer options > Wireless debugging > "Pair device with pairing code". Leave the box open.'
    if ($script:AutoMode) {
        Info "Then run:  .\setup\Wizard.ps1 -Device $script:Dev -PairCode <6-digit code>"
        return
    }
    Wait-Enter 'Press Enter when you see the code'
    $code = (Read-Host '  Type the 6-digit code').Trim()
    [void](Invoke-AaePair $code 0)
    [void](Invoke-Adb -s $script:Dev forward --remove tcp:19093)
}

# The TV log trims itself to 300 lines, so mark it and look for lines after the mark.
function Add-LogMark {
    $id = 'wizard-mark-' + (Get-Random)
    [void](Sh "echo $id >> /data/local/tmp/tv-apply.log")
    return $id
}
function Get-LogAfterMark([string]$id, [string]$pattern) {
    return Sh1 "sed -n '/$id/,`$p' /data/local/tmp/tv-apply.log 2>/dev/null | grep -m1 '$pattern'"
}

# ====================================================================== step 4: remote button
function Add-AccessibilityService([string]$component) {
    $cur = Sh1 'settings get secure enabled_accessibility_services'
    if ($cur -split ':' -contains $component) { return }
    $new = if ($cur -in '', 'null') { $component } else { "${cur}:$component" }
    [void](Sh "settings put secure enabled_accessibility_services $new")
    [void](Sh 'settings put secure accessibility_enabled 1')
    Add-Change @{ type = 'accessibility-add'; component = $component }
}

# Helper apps post permanent "running" notifications (they show up as a badge on the home screen). Hide them
# by snoozing them for 30 days: the apps keep working, nothing restarts. The boot script repeats this every run.
function Set-Quiet([string]$pkg) {
    $n = [int](Sh1 "c=0; for k in `$(cmd notification list 2>/dev/null | grep '|$pkg|'); do cmd notification snooze --for 2592000000 `$k >/dev/null 2>&1 && c=`$((c+1)); done; echo `$c")
    if ($n -gt 0) { Ok "Hid $n permanent notification(s) from $pkg (no badge on your home screen)" }
}

function Step-Remote {
    Title 'Step 4' $StepNames.remote
    if ($script:Info.Sdk -lt 26) { Warn 'Key Mapper needs Android 8 or newer. Skipping.'; return }
    if (-not (Test-StepDone 'background')) { Warn 'Do step 2 first (the button runs what step 2 sets up).'; return }
    Info 'Key Mapper (free, open source) lets any remote button do anything. You will set up:'
    Info '  double press a button  ->  every background app goes to sleep, then the volume bar flashes when done.'
    if (-not (Ask-YesNo 'Set it up?' $true)) { return }
    $km = 'io.github.sds100.keymapper'
    if (Sh1 "pm path $km") { Ok 'Key Mapper is already installed.' } else { try { [void](Install-Helper 'keymapper') } catch { Bad $_.Exception.Message; return } }
    Add-AccessibilityService "$km/$km.system.accessibility.MyAccessibilityService"
    Ok 'Key Mapper can now see remote buttons (its accessibility service is on).'
    $script:State.conf.START_KEYMAPPER_EXPERT_MODE = '1'; Save-State
    $script:Info.All = @($script:Info.All) + $km
    [void](Publish-Agent)
    $starter = "/storage/emulated/0/Android/data/$km/starter"
    if (-not (Sh1 "ls $starter 2>/dev/null")) {
        [void](Sh "am start -n $km/.MainActivity")
        OnTv 'Key Mapper is open. Skip its intro screens (if it asks to send notifications, choose Don''t allow), then open its menu and choose "Expert Mode". Just opening it is enough.'
        for ($i = 0; $i -lt 100 -and -not (Sh1 "ls $starter 2>/dev/null"); $i++) { Start-Sleep -Seconds 3 }
    }
    if (Sh1 "ls $starter 2>/dev/null") {
        [void](Sh 'sh /data/local/tmp/tv-apply.sh')
        if ((Sh1 'ps -A -o NAME | grep -cx keymapper_sysbridge') -ne '0') { Ok 'Expert Mode is running (the boot helper restarts it after every restart).' }
        else { Warn 'Expert Mode did not start. See docs/05-key-mapper.md.' }
    } else { Warn 'Expert Mode screen was not opened. The button needs it; run this step again later.' }
    Say ''
    Say '  Now make the button, in Key Mapper on the TV:' White
    Info '1. Press + (new key map) > Record trigger > press a spare button twice quickly (for example a colour button).'
    Info '   Set the trigger to "Double press".'
    Info '2. Actions > + > "Shell command". Script:  sh /data/local/tmp/tv-refresh.sh   Execution mode: ADB'
    Info '3. Add a second action: "Show volume dialog" (this is the "done" signal).'
    Info '4. Save. Tip: in Key Mapper''s Expert Mode settings, turn off "Auto start" (the boot helper starts it instead).'
    if (-not (Test-StepDone 'stick')) { Warn 'Without step 3, Expert Mode stops at every restart and the button needs the wizard again. Step 3 fixes that.' }
    Info 'Bonus idea: map a button you never use (Live TV, the brand button) to "Launch app" for your favourite app.'
    $mark = Add-LogMark
    Say ''
    Info 'When it is saved, press Home, then your new button on the remote. Already have this key map? Just press it.'
    Info 'Waiting up to 5 minutes...'
    for ($i = 0; $i -lt 100; $i++) {
        if (Get-LogAfterMark $mark 'refresh done') { Ok 'It works! The refresh ran from your remote button.'; Set-Quiet $km; Set-StepDone 'remote'; return }
        Start-Sleep -Seconds 3
    }
    Warn "Didn't see the button being pressed. You can finish the key map later; everything else is ready."
    Set-Quiet $km
    Set-StepDone 'remote'
}

# ====================================================================== step 5: launcher
# Wallhaven query for the plugin's "Booru URL" field. The plugin appends /api/v1/search?q=...&sorting=random to
# whatever is typed there; ending our URL with # turns that into a fragment that is never sent, so these filters win.
function Get-WallhavenUrl {
    $phys = Sh1 'wm size 2>/dev/null | grep -m1 Physical'
    $res = if ($phys -match '(\d+)x(\d+)' -and [int]$matches[1] -ge 3840) { '3840x2160' } else { '1920x1080' }
    return "https://wallhaven.cc/api/v1/search?sorting=toplist&topRange=6M&resolutions=$res#"
}

function Step-Launcher {
    Title 'Step 5' $StepNames.launcher
    if ($script:Info.Platform -eq 'Fire TV') { Warn 'Fire TV does not allow other home screens. Skipping.'; return }
    $pj = 'com.spocky.projengmenu'
    if (-not (Sh1 "pm path $pj")) {
        OnTv 'The Play Store opens on "Projectivy Launcher". Press Install.'
        [void](Sh "am start -a android.intent.action.VIEW -d 'market://details?id=$pj'")
        for ($i = 0; $i -lt 200 -and -not (Sh1 "pm path $pj"); $i++) { Start-Sleep -Seconds 3 }
        if (-not (Sh1 "pm path $pj")) { Warn 'Projectivy was not installed. Skipping this step.'; return }
    }
    Ok 'Projectivy Launcher is installed.'
    Info 'Projectivy takes over the Home button with a small accessibility helper. The stock launcher stays installed;'
    Info 'Projectivy needs it in the background on Google TV.'
    $pjA11y = "$pj/$pj.services.ProjectivyAccessibilityService"
    if ((Sh1 'settings get secure enabled_accessibility_services') -split ':' -contains $pjA11y) { Ok 'Projectivy''s Home-button helper is already on.' }
    elseif (Ask-YesNo 'Switch on Projectivy''s Home-button helper for you?' $true) {
        Add-AccessibilityService $pjA11y
        Ok 'Projectivy''s Home-button helper is on.'
    }
    if ($script:Info.Home -eq $pj) { Ok 'Projectivy is already your home screen.' }
    else {
        [void](Sh "monkey -p $pj -c android.intent.category.LEANBACK_LAUNCHER 1 >/dev/null 2>&1")
        OnTv 'Projectivy is open. In its settings, turn on the option to override the default launcher if it asks.'
    }
    if (Ask-YesNo 'Add rotating wallpapers from Wallhaven (Booru Wallpaper Provider plugin)?' $true) {
        try {
            $bw = Install-Helper 'booru-wallpapers'
            if ($bw.Already) { Info 'Keeping your existing wallpaper settings.'; Set-StepDone 'launcher'; return }
            $url = Get-WallhavenUrl
            Say '  Set it up on the TV:' White
            Info '1. Open "Booru Wallpaper Provider" from your apps. Booru type: Wallhaven. Booru URL (type it exactly, # included):'
            Say "       $url" Cyan
            Info '   That is the most popular wallpapers of the last 6 months in your TV''s exact resolution.'
            Info '2. Projectivy settings > Appearance > Wallpaper > Launcher wallpaper: scroll to the bottom and pick'
            Info '   "Booru Wallpaper Provider". (Changed the URL later? Pick another source and then Booru again, so'
            Info '   Projectivy fetches the new list right away.)'
            Info 'Keep wallpaper blur and colour effects off: on small TVs they cost smoothness.'
        } catch { Bad $_.Exception.Message }
    }
    Set-StepDone 'launcher'
}

# ====================================================================== step 6: extras
function Step-Extras {
    Title 'Step 6' $StepNames.extras
    # Ad and tracker blocking for every app via Android's Private DNS (AdGuard DNS).
    $mode = Sh1 'settings get global private_dns_mode'; $dnsHost = Sh1 'settings get global private_dns_specifier'
    if ($script:Info.Sdk -lt 28) { Info 'Private DNS needs Android 9 or newer. Skipping.' }
    elseif ($dnsHost -eq 'dns.adguard-dns.com' -and $mode -eq 'hostname') { Ok 'Ad-blocking DNS (AdGuard) is already on.' }
    else {
        $want = Get-Answer 'privateDns' $null
        if ($null -eq $want) {
            Info 'AdGuard DNS blocks ad and tracking domains in every app. It cannot block YouTube''s own video ads.'
            Info 'Trade-offs: AdGuard sees which sites the TV looks up, and a rare app may refuse to work (one switch undoes it).'
            $want = Ask-YesNo 'Turn on ad-blocking DNS for the whole TV?' $false
        }
        if ($want) {
            Add-Change @{ type = 'private-dns'; oldMode = $mode; oldHost = $dnsHost }
            [void](Sh 'settings put global private_dns_specifier dns.adguard-dns.com; settings put global private_dns_mode hostname')
            Start-Sleep -Seconds 8
            $ad = Sh1 'ping -c1 -W2 doubleclick.net 2>&1 | grep -m1 -oE ''\([0-9.]+\)'''
            $ok = Sh1 'ping -c1 -W2 www.google.com 2>&1 | grep -m1 -oE ''\([0-9.]+\)'''
            if ($ok -and $ad -match '\((0\.0\.0\.0|127\.0\.0\.1)\)') { Ok 'Ad-blocking DNS is on: ad domains are blocked, normal sites work.' }
            else {
                $restoreMode = if ($mode -in '', 'null') { 'opportunistic' } else { $mode }
                [void](Sh "settings put global private_dns_mode $restoreMode")
                Warn 'AdGuard DNS did not answer as expected on this network, so it was switched back off.'
            }
        }
    }
    # YouTube links straight into SmartTube instead of an "open with" box every time.
    $st = @('org.smarttube.stable', 'org.smarttube.beta', 'com.liskovsoft.smarttubetv.beta') | Where-Object { $script:Info.All -contains $_ } | Select-Object -First 1
    if ($st -and $script:Info.Sdk -ge 31) {
        $want = Get-Answer 'smartTubeLinks' $null
        if ($null -eq $want) { $want = Ask-YesNo 'Open YouTube links in SmartTube? (the YouTube app, its remote button and casting are not affected)' $true }
        if ($want) {
            [void](Sh "pm set-app-links-user-selection --user 0 --package $st true youtube.com www.youtube.com m.youtube.com youtu.be")
            [void](Sh 'pm set-app-links-allowed --user 0 --package com.google.android.youtube.tv false')
            Add-Change @{ type = 'app-links'; package = $st }
            Ok 'YouTube links now open in SmartTube.'
        }
    }
    Set-StepDone 'extras'
}

# ====================================================================== finish, test restart, report
# Android saves package and permission changes to disk about 10 seconds after they happen. Restarting sooner
# can silently lose them, so always give it time first.
function Wait-ForSave {
    Info 'Letting Android save the changes...'
    Start-Sleep -Seconds 15
}

function Test-Restart {
    $ip = $script:Dev -replace ':\d+$', ''
    $mark = Add-LogMark
    Wait-ForSave
    Info 'Restarting the TV. Please don''t touch it for about 5 minutes.'
    [void](Invoke-Adb -s $script:Dev reboot)
    Start-Sleep -Seconds 40
    $back = $false
    for ($i = 0; $i -lt 60 -and -not $back; $i++) {
        [void](Invoke-Adb connect "${ip}:5555")
        if ((Get-AdbState "${ip}:5555") -eq 'device') { $back = $true; $script:Dev = "${ip}:5555" } else { Start-Sleep -Seconds 5 }
    }
    if (-not $back) {
        Warn 'The PC could not reconnect after the restart. Usually debugging switched itself off.'
        Info 'The boot helper keeps trying for 20 minutes, so switching debugging back on in that time still works.'
        return $false
    }
    Ok 'TV is back. Waiting for the boot helper...'
    for ($i = 0; $i -lt 80; $i++) {
        $l = Get-LogAfterMark $mark 'apply start (uptime'
        if ($l -match 'uptime ([\d.]+)s') {
            Ok ("Re-applied by itself {0:N0} seconds after the TV started. No PC needed." -f [double]$matches[1])
            # The helper runs a second pass ~2 minutes later for apps that start late; measure after that.
            Info 'Waiting for its second pass (about 2 minutes) before measuring...'
            for ($j = 0; $j -lt 40; $j++) {
                if (@(Sh "sed -n '/$mark/,`$p' /data/local/tmp/tv-apply.log 2>/dev/null | grep 'apply done'").Count -ge 2) { break }
                Start-Sleep -Seconds 6
            }
            Start-Sleep -Seconds 15
            return $true
        }
        Start-Sleep -Seconds 6
    }
    Warn 'The boot helper did not run within 8 minutes. See docs/08-troubleshooting.md, "Boot helper".'
    return $false
}

function Write-Report($before, $after, [bool]$restartOk) {
    $ch = Get-Changes
    $off = @($ch | Where-Object { $_.type -eq 'pm-disable' } | ForEach-Object { $_.package } | Sort-Object -Unique)
    $back = @($ch | Where-Object { $_.type -eq 'pm-enable' } | ForEach-Object { $_.package })
    $off = @($off | Where-Object { $back -notcontains $_ })
    $inst = @($ch | Where-Object { $_.type -eq 'install' } | ForEach-Object { $_.name } | Sort-Object -Unique)
    $r = @("# Your TV: $($script:Info.Maker) $($script:Info.Model)", '', "Set up on $(Get-Date -Format 'yyyy-MM-dd HH:mm') with omae-wa-mou-debloated.", '',
        '| | At the start | Now |', '|---|---|---|',
        "| Free memory | $($before.FreeMb) MB | $($after.FreeMb) MB |",
        "| App processes running | $($before.AppProcesses) | $($after.AppProcesses) |", '',
        "Steps done: $(@($script:State.stepsDone) -join ', ')", '',
        "Switched off ($($off.Count)): $($off -join ', ')", '',
        "Helper apps installed: $(if ($inst.Count) { $inst -join ', ' } else { 'none' })", '',
        "Survives restarts: $(if ($restartOk) { 'yes, tested' } elseif (Test-StepDone 'stick') { 'installed, not tested' } else { 'no (step 3 not done)' })", '',
        '## Undo', '', 'Double-click Start-Here.cmd and choose "Undo everything", or run:', '',
        '```', ".\setup\Wizard.ps1 -Device $script:Dev -Undo", '```')
    $f = Join-Path $script:Dir 'report.md'
    Write-Text $f ($r -join "`n")
    return $f
}

function Finish-Setup {
    Title 'Done' 'Checking the result'
    $restartOk = $false
    if ((Test-StepDone 'stick') -and -not $NoReboot) {
        $doIt = Get-Answer 'testRestart' $null
        if ($null -eq $doIt) { $doIt = Ask-YesNo 'Restart the TV now to test that it re-applies everything by itself? (recommended, ~5 min)' $true }
        if ($doIt) { $restartOk = Test-Restart }
    }
    $before = $script:State.before
    $after = Get-Measure
    Say ''
    $when = if ($restartOk) { 'after a restart' } else { 'now' }
    Ok ("Free memory: {0} MB at the start, {1} MB {2}" -f $before.FreeMb, $after.FreeMb, $when)
    Ok ("App processes running: {0} at the start, {1} {2}" -f $before.AppProcesses, $after.AppProcesses, $when)
    $f = Write-Report $before $after $restartOk
    Ok "Report saved: $f"
    Say ''
    Say '  Omae wa mou debloated. Enjoy the snappier TV.' Cyan
    Info 'Something off? Double-click Start-Here.cmd again and choose "Undo everything".'
}

# ====================================================================== undo / status / plan
function Invoke-Undo {
    Title 'Undo' 'Putting the TV back the way it was'
    $ch = @(Get-Changes)
    if (-not $ch.Count) { Info 'Nothing recorded for this TV.'; return }
    if (-not (Ask-YesNo 'Undo everything the wizard changed on this TV?' $true)) { return }
    $snapF = Join-Path $script:Dir 'snapshot-original.json'
    $snap = if (Test-Path $snapF) { Get-Content $snapF -Raw | ConvertFrom-Json } else { $null }
    [array]::Reverse($ch)
    $enabled = @{}
    foreach ($c in $ch) {
        if ($c.type -eq 'pm-disable' -and -not $enabled[$c.package]) {
            if (-not ($snap -and @($snap.disabled) -contains $c.package)) { [void](Sh "pm enable --user 0 $($c.package)"); Ok "back on: $($c.package)" }
            $enabled[$c.package] = $true
        }
        if ($c.type -eq 'notifications-off') { [void](Sh "pm grant $($c.package) android.permission.POST_NOTIFICATIONS"); Ok "notifications back on: $($c.package)" }
        if ($c.type -eq 'private-dns') {
            $m = if ("$($c.oldMode)" -in '', 'null') { 'opportunistic' } else { $c.oldMode }
            [void](Sh "settings put global private_dns_mode $m")
            if ("$($c.oldHost)" -in '', 'null') { [void](Sh 'settings delete global private_dns_specifier') } else { [void](Sh "settings put global private_dns_specifier $($c.oldHost)") }
            Ok 'ad-blocking DNS off'
        }
        if ($c.type -eq 'app-links') {
            [void](Sh "pm set-app-links-user-selection --user 0 --package $($c.package) false youtube.com www.youtube.com m.youtube.com youtu.be")
            [void](Sh 'pm set-app-links-allowed --user 0 --package com.google.android.youtube.tv true')
            Ok 'YouTube links back to normal'
        }
        if ($c.type -eq 'accessibility-add') {
            $cur = (Sh1 'settings get secure enabled_accessibility_services') -split ':' | Where-Object { $_ -and $_ -ne $c.component }
            [void](Sh ('settings put secure enabled_accessibility_services ' + $(if ($cur) { $cur -join ':' } else { "''" })))
            Ok "accessibility helper off: $($c.component -replace '/.*', '')"
        }
    }
    if ($ch | Where-Object { $_.type -eq 'deploy' }) {
        $conf = $script:State.conf
        $pk = @($script:Info.User) + @("$($conf.PREINSTALLED_APPS)" -split ' ') + @("$($conf.STOP_ONLY)" -split ' ') | Where-Object { $_ } | Sort-Object -Unique
        foreach ($p in $pk) { [void](Sh "cmd appops set $p RUN_ANY_IN_BACKGROUND allow; am set-standby-bucket $p active") }
        Ok "background limits removed from $($pk.Count) apps"
        foreach ($k in 'window_animation_scale', 'transition_animation_scale', 'animator_duration_scale', 'mitv_fast_cur_anim_scale') {
            $v = if ($snap) { $snap.anim.$k } else { '1.0' }
            if ($v -and $v -ne 'null') { [void](Sh "settings put global $k $v") } elseif ($k -ne 'mitv_fast_cur_anim_scale') { [void](Sh "settings put global $k 1.0") }
        }
        Ok 'animation speed restored'
        [void](Sh "for k in `$(cmd notification snoozed 2>/dev/null | grep -oE '^[^ ]+' | grep -E 'keymapper|adbautoenable'); do cmd notification unsnooze `$k; done")
        [void](Sh 'kill $(cat /data/local/tmp/tv-daily.pid 2>/dev/null) 2>/dev/null; rm -f /data/local/tmp/tv-daily.sh /data/local/tmp/tv-daily.pid /data/local/tmp/tv-daily.last')
        [void](Sh 'cmd wifi set-ipreach-disconnect enabled >/dev/null 2>&1')
        [void](Sh 'rm -f /data/local/tmp/tv-apply.sh /data/local/tmp/tv-refresh.sh /data/local/tmp/tv-apply.conf /data/local/tmp/tv-apply.log; [ -f /data/local/tmp/tv-apply.sh.bak ] && mv /data/local/tmp/tv-apply.sh.bak /data/local/tmp/tv-apply.sh')
        Ok 'scripts removed from the TV'
    }
    $inst = @($ch | Where-Object { $_.type -eq 'install' })
    if ($inst.Count -and (Ask-YesNo "Also uninstall the helper apps the wizard installed ($(($inst | ForEach-Object { $_.name }) -join ', '))?" $true)) {
        foreach ($i in $inst) { [void](Invoke-Adb -s $script:Dev uninstall $i.package); Ok "uninstalled $($i.name)" }
    }
    $stillOff = @($enabled.Keys | Where-Object { (Sh 'pm list packages -d') -contains "package:$_" -and -not ($snap -and @($snap.disabled) -contains $_) })
    if ($stillOff.Count) { Warn "Still switched off: $($stillOff -join ', '). Run Undo again, or: adb shell pm enable --user 0 <package>" }
    Add-Change @{ type = 'undo' }
    Move-Item (Join-Path $script:Dir 'changes.jsonl') (Join-Path $script:Dir ("changes-undone-{0}.jsonl" -f (Get-Date -Format 'yyyyMMdd-HHmmss')))
    $script:State.stepsDone = @(); $script:State.conf = $null; Save-State
    if (Ask-YesNo 'Restart the TV now to finish? (recommended)' $true) { Wait-ForSave; [void](Invoke-Adb -s $script:Dev reboot) }
    Ok 'Undo complete.'
}

function Show-Status {
    Title 'Status' "$($script:Info.Maker) $($script:Info.Model)"
    Info "Steps done: $(if (@($script:State.stepsDone).Count) { @($script:State.stepsDone) -join ', ' } else { 'none yet' })"
    $off = @(Get-Changes | Where-Object { $_.type -eq 'pm-disable' } | ForEach-Object { $_.package } | Sort-Object -Unique)
    $still = @($off | Where-Object { $script:Info.Disabled -contains $_ })
    Info "Switched off by the wizard: $($off.Count) (still off: $($still.Count))"
    foreach ($p in 'com.termux', 'com.termux.boot', 'com.tpn.adbautoenable', 'io.github.sds100.keymapper', 'com.spocky.projengmenu') {
        Info ("{0,-32} {1}" -f $p, $(if ($script:Info.All -contains $p) { 'installed' } else { '-' }))
    }
    Say '  Last lines of the TV''s own log:' White
    Sh 'tail -n 6 /data/local/tmp/tv-apply.log 2>/dev/null' | ForEach-Object { Info $_ }
}

function Write-Plan {
    $matched = @(Get-CatalogMatches (Read-Catalog))
    $med = @($matched | Where-Object { $_.Risk -eq 'medium' -and -not $_.AlreadyOff })
    $qs = @()
    foreach ($q in (Read-Questions)) {
        $pk = @($med | Where-Object { $_.Feature -eq $q.id })
        if (-not $pk.Count) { continue }
        $rec = $q.recommend; if ($rec -eq 'keep-if-ram-over-3gb') { $rec = if ($script:Info.RamMb -gt 3200) { 'keep' } else { 'off' } }
        $qs += [ordered]@{ id = $q.id; ask = $q.ask; help = $q.help; recommend = $rec; unsure = $q.unsure; affects = @($pk | ForEach-Object { [ordered]@{ package = $_.Package; what = $_.What } }) }
    }
    $g = Get-BackgroundGroups
    $plan = [ordered]@{
        device     = [ordered]@{ maker = $script:Info.Maker; model = $script:Info.Model; platform = $script:Info.Platform; android = $script:Info.Android; sdk = $script:Info.Sdk; ramMb = $script:Info.RamMb; abi = $script:Info.Abi; home = $script:Info.Home; serial = $script:Dev }
        debloat    = [ordered]@{
            low          = @($matched | Where-Object { $_.Risk -eq 'low' -and -not $_.AlreadyOff } | ForEach-Object { [ordered]@{ package = $_.Package; what = $_.What; verified = $_.Verified } })
            questions    = $qs
            keepPresent  = @($matched | Where-Object { $_.Risk -eq 'keep' } | ForEach-Object { [ordered]@{ package = $_.Package; why = $_.What } })
            alreadyOff   = @($matched | Where-Object { $_.AlreadyOff } | ForEach-Object { $_.Package })
            appCandidates = @(Get-AppCandidates $matched)
            notInCatalog = ($script:Info.System.Count - $matched.Count)
        }
        background = [ordered]@{
            asleep        = @($g.Rows | Where-Object { $_.Group -eq 'asleep' } | ForEach-Object { $_.Package })
            stopOnly      = @($g.Rows | Where-Object { $_.Group -eq 'stoponly' } | ForEach-Object { $_.Package })
            protect       = @($g.Rows | Where-Object { $_.Group -eq 'protect' } | ForEach-Object { $_.Package })
            autoProtected = @($g.Auto)
        }
        helpers    = [ordered]@{ termuxSupported = ($script:Info.Sdk -ge 24); adbAutoEnableNeeded = ($script:Info.Sdk -ge 34); keyMapperSupported = ($script:Info.Sdk -ge 26); launcherSupported = ($script:Info.Platform -ne 'Fire TV') }
        stepsDone  = @($script:State.stepsDone)
    }
    $template = [ordered]@{
        steps        = [ordered]@{ debloat = $true; background = $true; stick = $true; remote = $false; launcher = $false; extras = $false }
        features     = [ordered]@{}
        disableApps  = @()
        skipPackages = @()
        background   = [ordered]@{ asleep = @(); stopOnly = @(); protect = @() }
        animation    = '0.5'
        dailyRefresh = $true
        privateDns   = $false
        smartTubeLinks = $true
        testRestart  = $true
    }
    foreach ($q in $qs) { $template.features[$q.id] = $q.recommend }
    $pf = Join-Path $script:Dir 'plan.json'; $af = Join-Path $script:Dir 'answers-template.json'
    Write-Text $pf ($plan | ConvertTo-Json -Depth 8)
    Write-Text $af ($template | ConvertTo-Json -Depth 5)
    Ok "Plan written (nothing changed on the TV): $pf"
    Ok "Answers template: $af"
    Info ("{0} junk packages, {1} questions, {2} preinstalled apps to choose from, {3} apps for the sleep list" -f $plan.debloat.low.Count, $qs.Count, $plan.debloat.appCandidates.Count, $plan.background.asleep.Count)
}

# ====================================================================== main
function Select-Steps {
    if ($Steps) { return $Steps }  # already split and checked in Start-Wizard
    $preset = Get-Answer 'steps' $null
    if ($preset) { return @($StepNames.Keys | Where-Object { $preset.$_ }) }
    $sel = @{}
    foreach ($k in $StepNames.Keys) { $sel[$k] = ($Recommended -contains $k) -and -not (Test-StepDone $k) }
    if ($script:AutoMode) { return @($StepNames.Keys | Where-Object { $sel[$_] }) }
    while ($true) {
        Say ''
        Say '  Here is the plan. [x] = will do,  (done) = already done on this TV:' White
        $i = 0
        foreach ($k in $StepNames.Keys) {
            $i++
            $box = if ($sel[$k]) { '[x]' } else { '[ ]' }
            $tag = if (Test-StepDone $k) { ' (done)' } elseif ($Recommended -contains $k) { ' (recommended)' } else { ' (optional)' }
            Say ("    {0}) {1} {2}{3}" -f $i, $box, $StepNames[$k], $tag)
        }
        $a = Read-Host '  Press Enter to start, or type numbers to tick/untick (e.g. 4 5)'
        if ([string]::IsNullOrWhiteSpace($a)) { break }
        $keys = @($StepNames.Keys)
        foreach ($t in ($a -split '[\s,]+' | Where-Object { $_ -match '^\d+$' })) { $n = [int]$t - 1; if ($n -ge 0 -and $n -lt $keys.Count) { $sel[$keys[$n]] = -not $sel[$keys[$n]] } }
    }
    return @($StepNames.Keys | Where-Object { $sel[$_] })
}

function Start-Wizard {
    Clear-Host
    Say ''
    Say '  omae-wa-mou-debloated  |  make a slow Google TV fast, and keep it that way' Cyan
    Say '  No root. Nothing deleted. Everything can be undone.' DarkCyan
    $script:AutoMode = [bool]$Auto
    # -Steps a,b arrives as one string through powershell -File / Start-Here.cmd: split it ourselves.
    if ($Steps) {
        $script:Steps = @($Steps | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ })
        $bad = @($script:Steps | Where-Object { @($StepNames.Keys) -notcontains $_ })
        if ($bad.Count) { Bad "Unknown step(s): $($bad -join ', '). Use: $(@($StepNames.Keys) -join ', ')"; return }
    }
    if ($Answers) { $script:Ans = Get-Content $Answers -Raw | ConvertFrom-Json }
    if ($script:AutoMode -and -not $Device) { Bad '-Auto needs -Device <TV IP>.'; return }

    $script:Adb = Find-Adb
    if (-not $script:Adb) {
        Warn 'The tool that talks to the TV (Android platform-tools) is not on this PC yet.'
        if (-not (Ask-YesNo 'Download it into this folder now? (about 7 MB, from Google)' $true)) { return }
        $script:Adb = Install-Adb
        if (-not $script:Adb) { Bad 'Download failed. See README, "Stuck?".'; return }
    }
    Ok "Using adb: $script:Adb"

    try { Connect-Tv } catch { Bad $_.Exception.Message; return }
    $script:Info = Get-TvInfo
    Open-DeviceFolder
    Show-TvCard

    if ($Plan) { Write-Plan; return }
    if ($Status) { Show-Status; return }
    if ($PairCode) {
        if (Connect-AaeApi) { [void](Invoke-AaePair $PairCode $PairPort) } else { Bad 'ADB Auto Enable is not reachable.' }
        [void](Invoke-Adb -s $script:Dev forward --remove tcp:19093)
        return
    }
    if ($Undo) { Invoke-Undo; return }

    if (@($script:State.stepsDone).Count -and -not $script:AutoMode -and -not $Steps) {
        $c = Ask-Choice 'This TV was set up before. What now?' @('Continue / change the setup', 'Show status', 'Undo everything', 'Quit') 0
        if ($c -eq 1) { Show-Status; return }
        if ($c -eq 2) { Invoke-Undo; return }
        if ($c -eq 3) { return }
    }

    Save-Snapshot
    if (-not $script:State.before) { $script:State.before = Get-Measure; Save-State }
    $todo = Select-Steps
    foreach ($s in $todo) {
        try {
            switch ($s) {
                'debloat' { Step-Debloat }
                'background' { Step-Background }
                'stick' { Step-Stick }
                'remote' { Step-Remote }
                'launcher' { Step-Launcher }
                'extras' { Step-Extras }
            }
        } catch {
            Bad "This step stopped: $($_.Exception.Message)"
            Info 'Nothing half-done is left behind that Undo can''t fix. Run Start-Here.cmd again to retry.'
        }
    }
    Finish-Setup
}

# Dot-sourcing (tests) loads the functions without running the wizard.
if ($MyInvocation.InvocationName -ne '.') { Start-Wizard }
