# Builds WallhavenShuffle-Projectivy.apk with the Android SDK's own tools (aidl, aapt2, d8, zipalign, apksigner) and
# a JDK: no Gradle, no libraries.
#   powershell -ExecutionPolicy Bypass -File build.ps1          # -> bin\WallhavenShuffle-Projectivy.apk
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Test    # also runs the rotation tests on the PC first
# Needs: JDK 17 (JAVA_HOME, or %USERPROFILE%\.whs-build\jdk-*), Android SDK with platforms;android-34 and
# build-tools;34.0.0 (ANDROID_SDK, or %USERPROFILE%\.whs-build\sdk). The signing key is created on the first build
# in %USERPROFILE%\.whs-build\ (keep it: updates must be signed with the same key) and never goes into the repo.
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$tools = Join-Path $env:USERPROFILE '.whs-build'
$jdk = if ($env:JAVA_HOME) { $env:JAVA_HOME } else { (Get-ChildItem $tools -Directory -Filter 'jdk-*' | Select-Object -First 1).FullName }
$sdk = if ($env:ANDROID_SDK) { $env:ANDROID_SDK } else { Join-Path $tools 'sdk' }
$bt = Join-Path $sdk 'build-tools\34.0.0'
$jar = Join-Path $sdk 'platforms\android-34\android.jar'
foreach ($p in $jdk, $bt, $jar) { if (-not $p -or -not (Test-Path $p)) { throw "Missing: $p (see the top of build.ps1)" } }
$env:JAVA_HOME = $jdk
$javac = Join-Path $jdk 'bin\javac.exe'; $java = Join-Path $jdk 'bin\java.exe'; $keytool = Join-Path $jdk 'bin\keytool.exe'

function Run($exe) { & $exe @args; if ($LASTEXITCODE -ne 0) { throw "$(Split-Path $exe -Leaf) failed ($LASTEXITCODE)" } }

$work = Join-Path $env:TEMP 'whs-apk'   # short path: the tools trip over Windows' 260-character limit
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force "$work\gen", "$work\classes", "$work\dex", "$work\test" | Out-Null

# The sources too, since this folder may sit deep enough for the tools to fail on its paths.
$in = Join-Path $work 'in'
foreach ($d in 'aidl', 'src', 'res', 'test') { Copy-Item (Join-Path $here $d) (Join-Path $in $d) -Recurse }
Copy-Item (Join-Path $here 'AndroidManifest.xml') $in

if ($Test) {
    Run $javac -encoding UTF-8 -d "$work\test" "$in\src\io\github\muknan\wallhavenshuffle\Shuffle.java" "$in\src\io\github\muknan\wallhavenshuffle\TvSettings.java" "$in\test\ShuffleTest.java"
    Run $java -cp "$work\test" io.github.muknan.wallhavenshuffle.ShuffleTest
}

# 1. The Binder interface Projectivy calls, from its AIDL.
Run (Join-Path $bt 'aidl.exe') "-I$in\aidl" "-p$sdk\platforms\android-34\framework.aidl" "-o$work\gen" "$in\aidl\tv\projectivy\plugin\wallpaperprovider\api\IWallpaperProviderService.aidl"
# 2. Resources and manifest.
Run (Join-Path $bt 'aapt2.exe') compile --dir "$in\res" -o "$work\res.zip"
Run (Join-Path $bt 'aapt2.exe') link -o "$work\base.apk" -I $jar --manifest "$in\AndroidManifest.xml" --java "$work\gen" "$work\res.zip"
# 3. Java to classes to dex.
$sources = @(Get-ChildItem "$in\src", "$work\gen" -Recurse -Filter *.java | ForEach-Object { $_.FullName })
Run $javac --release 11 -nowarn -encoding UTF-8 -classpath $jar -d "$work\classes" @sources
$classes = @(Get-ChildItem "$work\classes" -Recurse -Filter *.class | ForEach-Object { $_.FullName })
Run (Join-Path $bt 'd8.bat') --release --min-api 26 --lib $jar --output "$work\dex" @classes
# 4. Into the APK, aligned and signed.
Copy-Item "$work\base.apk" "$work\unsigned.apk"
Push-Location "$work\dex"; try { Run (Join-Path $jdk 'bin\jar.exe') uMf "$work\unsigned.apk" classes.dex } finally { Pop-Location }
Run (Join-Path $bt 'zipalign.exe') -f -p 4 "$work\unsigned.apk" "$work\aligned.apk"
$ks = Join-Path $tools 'whs-plugin.jks'; $pwFile = Join-Path $tools 'whs-plugin.pass'
if (-not (Test-Path $ks)) {
    $pw = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ })
    Set-Content -NoNewline $pwFile $pw
    Run $keytool -genkeypair -keystore $ks -alias plugin -keyalg RSA -keysize 3072 -validity 36500 -storepass $pw -keypass $pw -dname 'CN=Wallhaven Shuffle'
    Write-Host "Created the signing key $ks (keep it, with $pwFile, for updates)"
}
$pw = Get-Content $pwFile -Raw
$out = Join-Path $here 'bin'; New-Item -ItemType Directory -Force $out | Out-Null
$apk = Join-Path $out 'WallhavenShuffle-Projectivy.apk'
Run (Join-Path $bt 'apksigner.bat') sign --ks $ks --ks-pass "pass:$pw" --out $apk "$work\aligned.apk"
Run (Join-Path $bt 'apksigner.bat') verify $apk
Write-Host "Built $apk ($([math]::Round((Get-Item $apk).Length / 1KB)) KB)"
