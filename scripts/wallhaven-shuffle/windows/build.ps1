# Builds WallhavenShuffle.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
#   powershell -ExecutionPolicy Bypass -File build.ps1
# It compiles in a short temporary folder (the compiler can't cope with paths near Windows' 260-character limit)
# and copies the result to .\bin.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'The .NET Framework 4 C# compiler was not found.' }
$work = Join-Path $env:TEMP 'whs-build'
New-Item -ItemType Directory -Force $work | Out-Null
foreach ($f in 'WallhavenShuffle.cs', 'app.ico', 'app.manifest') { Copy-Item (Join-Path $here $f) $work -Force }
Push-Location $work
try {
    & $csc /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 /win32icon:app.ico /win32manifest:app.manifest `
        /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll `
        /out:WallhavenShuffle.exe WallhavenShuffle.cs
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
} finally { Pop-Location }
$out = Join-Path $here 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $work 'WallhavenShuffle.exe') $out -Force
Write-Host "Built $(Join-Path $out 'WallhavenShuffle.exe')"
