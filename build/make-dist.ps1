# Assemble the folder that actually gets distributed.  (ASCII only)
#
#   powershell -ExecutionPolicy Bypass -File build\make-dist.ps1
#
# dist\DSH-App\ is the BUILD output and contains sources; this script produces
# dist\release\, which holds only what an end user needs -- run it from there, or
# zip it, or hand over the single setup executable beside it.

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$appDir  = Join-Path $root 'dist\DSH-App'
$release = Join-Path $root 'dist\release'
$pkgDir  = Join-Path $root 'dist\package'

function Write-Step($t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }

Write-Step "Preflight"
$required = @(
    'DSH-App.exe',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.WinForms.dll',
    'WebView2Loader.dll',
    'ui\app.html'
)
foreach ($f in $required) {
    if (-not (Test-Path (Join-Path $appDir $f))) {
        throw "missing from the build output: $f -- run build\build.ps1 first"
    }
}
Write-Host "build output looks complete"

Write-Step "Assembling dist\release"
if (Test-Path $release) { Remove-Item $release -Recurse -Force }
New-Item -ItemType Directory -Path $release -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $release 'ui') -Force | Out-Null

# Copy the product files only. Everything in this list is needed at runtime;
# nothing else belongs in a user-facing folder.
$files = @(
    @{ From = 'DSH-App.exe';                          To = 'DSH-App.exe' },
    @{ From = 'Microsoft.Web.WebView2.Core.dll';      To = 'Microsoft.Web.WebView2.Core.dll' },
    @{ From = 'Microsoft.Web.WebView2.WinForms.dll';  To = 'Microsoft.Web.WebView2.WinForms.dll' },
    @{ From = 'WebView2Loader.dll';                   To = 'WebView2Loader.dll' },
    @{ From = 'ui\app.html';                          To = 'ui\app.html' }
)
foreach ($f in $files) {
    Copy-Item (Join-Path $appDir $f.From) (Join-Path $release $f.To) -Force
    Write-Host ("  + " + $f.To)
}

# Optional icon, only if the project has one.
$ico = Join-Path $root 'ui\dsh.ico'
if (Test-Path $ico) {
    Copy-Item $ico (Join-Path $release 'dsh.ico') -Force
    Copy-Item $ico (Join-Path $release 'ui\dsh.ico') -Force
    Write-Host "  + dsh.ico (next to the exe and inside ui\)"
} else {
    Write-Host "  (no ui\dsh.ico yet -- the app falls back to a default icon)"
}

Write-Step "Copying the installers alongside"
$copied = 0
foreach ($n in @('DSH-App-setup.exe', 'DSH-App-standalone.exe', 'DSH-App-installer.exe', 'DSH-App.dat')) {
    $src = Join-Path $pkgDir $n
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $release $n) -Force
        Write-Host ("  + " + $n)
        $copied++
    }
}
if ($copied -eq 0) { Write-Host "  (no packages found -- run build\pack.ps1 to create them)" }

Write-Step "Sanity check"
# Nothing from the build tree may leak into the release folder.
$leaks = @(Get-ChildItem $release -Recurse -File |
           Where-Object { $_.Extension -in @('.cs', '.ps1', '.ddf', '.sed', '.rsp', '.zip', '.nupkg') })
if ($leaks.Count -gt 0) {
    $leaks | ForEach-Object { Write-Host ("  LEAK: " + $_.FullName) -ForegroundColor Red }
    throw "build artifacts leaked into dist\release"
}
Write-Host "no source or build artifacts present" -ForegroundColor Green

$stray = @(Get-ChildItem $release -Recurse -File | Where-Object { $_.Name -like '*.log' })
if ($stray.Count -gt 0) { $stray | ForEach-Object { Write-Host ("  stray log: " + $_.Name) -ForegroundColor Yellow } }

Write-Step "Ready to distribute"
Get-ChildItem $release -Recurse -File | Sort-Object FullName | ForEach-Object {
    "{0,10:N0}  {1}" -f $_.Length, $_.FullName.Substring($release.Length).TrimStart('\')
}
$total = (Get-ChildItem $release -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ""
Write-Host ("total {0:N0} KB in {1}" -f ($total / 1KB), $release) -ForegroundColor Green
Write-Host ""
Write-Host "To hand this to someone, send ONE file:" -ForegroundColor Green
Write-Host ("  " + (Join-Path $release 'DSH-App-setup.exe'))
