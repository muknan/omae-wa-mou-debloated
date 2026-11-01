# Builds WallhavenShuffle.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-Test]
# -Test also compiles test\Tests.cs with the app and runs it (logic, sync, playlists, and the menus' clicks).
# It compiles in a short temporary folder (the compiler can't cope with paths near Windows' 260-character limit)
# and copies the result to .\bin.
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'The .NET Framework 4 C# compiler was not found.' }
$work = Join-Path $env:TEMP 'whs-build'
New-Item -ItemType Directory -Force $work | Out-Null
foreach ($f in 'WallhavenShuffle.cs', 'Sync.cs', 'Library.cs', 'app.ico', 'app.manifest') { Copy-Item (Join-Path $here $f) $work -Force }
Push-Location $work
try {
    & $csc /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 /win32icon:app.ico /win32manifest:app.manifest `
        /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Web.dll `
        /out:WallhavenShuffle.exe WallhavenShuffle.cs Sync.cs Library.cs
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
} finally { Pop-Location }
$out = Join-Path $here 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $work 'WallhavenShuffle.exe') $out -Force
Write-Host "Built $(Join-Path $out 'WallhavenShuffle.exe')"
if ($Test) {
    $t = Join-Path $env:TEMP 'whs-test'
    if (Test-Path $t) { Remove-Item $t -Recurse -Force }
    New-Item -ItemType Directory -Force $t | Out-Null
    foreach ($f in 'WallhavenShuffle.cs', 'Sync.cs', 'Library.cs') { Copy-Item (Join-Path $here $f) $t -Force }
    Copy-Item (Join-Path $here 'test\Tests.cs') $t -Force
    Push-Location $t
    try {
        & $csc /nologo /target:exe /codepage:65001 /main:WallhavenShuffle.Tests /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
            /reference:System.Web.Extensions.dll /reference:System.Web.dll /out:t.exe WallhavenShuffle.cs Sync.cs Library.cs Tests.cs
        if ($LASTEXITCODE -ne 0) { throw "Test build failed ($LASTEXITCODE)" }
        .\t.exe
        if ($LASTEXITCODE -ne 0) { throw "$LASTEXITCODE test(s) failed" }
    } finally { Pop-Location }
}