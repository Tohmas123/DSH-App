# DSH-App build script  (ASCII only on purpose -- see AGENTS.md, encoding rule 1)
#
# Compiles the app with the C# compiler that ships inside Windows, so neither the
# .NET SDK nor NuGet is required on the build machine. The only external asset is
# the WebView2 SDK, downloaded once and cached in .cache\.
#
#   powershell -ExecutionPolicy Bypass -File build\build.ps1
#
# Output: dist\DSH-App\  (a runnable folder)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$srcDir  = Join-Path $root 'src'
$uiDir   = Join-Path $root 'ui'
$instDir = Join-Path $root 'installer'
$cache   = Join-Path $root '.cache'
$dist    = Join-Path $root 'dist\DSH-App'
$lib     = Join-Path $cache 'wv2\lib\net45'
$native  = Join-Path $cache 'wv2\runtimes\win-x64\native'

$wv2Version = '1.0.2210.55'
$wv2Url = "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$wv2Version/microsoft.web.webview2.$wv2Version.nupkg"

function Write-Step($text) { Write-Host ""; Write-Host "=== $text ===" -ForegroundColor Cyan }

# ---------------------------------------------------------------- 1. WebView2 SDK
Write-Step "WebView2 SDK"
$coreDll = Join-Path $lib 'Microsoft.Web.WebView2.Core.dll'
$wfmDll  = Join-Path $lib 'Microsoft.Web.WebView2.WinForms.dll'
$loadDll = Join-Path $native 'WebView2Loader.dll'

# ---------------------------------------------------------------------------
# What these three files are, and why they are checked rather than trusted:
# they are copied into dist\ and from there INTO WHAT USERS INSTALL. A cached
# copy is reused on every later build, so one tampered file (a shared or CI
# checkout, a writable .cache, a download that was poisoned the one time it ran)
# ends up inside every package this machine produces, with nothing to notice it.
#
# The expected SHA-256 values are the exact bytes of the pinned SDK version.
# Bumping $wv2Version therefore requires updating them -- the build says so in
# plain words instead of quietly shipping a different DLL.
# ---------------------------------------------------------------------------
$wv2Expected = @{
    'Microsoft.Web.WebView2.Core.dll'     = '5E172B4F558723B7DBB7F568F301077C84D6571436FBE5A5F45BFA621C020403'
    'Microsoft.Web.WebView2.WinForms.dll' = 'BEF507A4CE7B6A848993BC504AF7E2273CEC22E77469787CB1D47D3F362164ED'
    'WebView2Loader.dll'                  = '31423E905E29C8A40A483E81DAE1491990805FA066634D218B35BB96692BEF0D'
}
$wv2Stamp = Join-Path $cache 'wv2\version.txt'
$wv2Extract = Join-Path $cache 'wv2'
$wv2Nupkg = Join-Path $cache 'webview2.nupkg'

# Needed by the stamp write below, and normally defined by encoding.ps1 -- which is not
# dot-sourced until step 2, so it must exist here first.
$utf8Raw = New-Object System.Text.UTF8Encoding($false)

# Returns one line per problem found; empty means the cache is exactly right.
function Get-Wv2AssetProblems {
    $bad = @()
    if (-not (Test-Path -LiteralPath $wv2Stamp)) {
        return @('the cached SDK is not stamped with a version')
    }
    $stamped = ''
    try { $stamped = ([System.IO.File]::ReadAllText($wv2Stamp)).Trim() } catch { }
    if ($stamped -ne $wv2Version) {
        return @('the cached SDK is version ' + $stamped + ', this build wants ' + $wv2Version)
    }

    foreach ($f in @($coreDll, $wfmDll, $loadDll)) {
        $leaf = Split-Path -Leaf $f
        if (-not (Test-Path -LiteralPath $f)) { $bad += ($leaf + ': missing'); continue }
        $hash = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash
        if ($hash -ne $wv2Expected[$leaf]) { $bad += ($leaf + ': sha256 is ' + $hash); continue }
        # Independent of the pin: the file has to still carry a valid Microsoft
        # signature. Either check alone can be fooled by a mistake; together they
        # are a real answer to "is this the file we think it is".
        try {
            $sig = Get-AuthenticodeSignature -FilePath $f
            $subject = ''
            if ($sig.SignerCertificate) { $subject = $sig.SignerCertificate.Subject }
            if ($sig.Status -ne 'Valid' -or ($subject -notlike '*Microsoft*')) {
                $bad += ($leaf + ': signature is ' + $sig.Status + ' (' + $subject + ')')
            }
        } catch {
            $bad += ($leaf + ': signature could not be checked')
        }
    }
    return $bad
}

function Get-Wv2Sdk {
    Write-Host "downloading $wv2Version ..."
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $zip = Join-Path $cache 'webview2.zip'
    if (-not (Test-Path -LiteralPath $wv2Nupkg)) {
        Invoke-WebRequest -Uri $wv2Url -OutFile $wv2Nupkg -UseBasicParsing -TimeoutSec 300
    }
    # PS 5.1 Expand-Archive only accepts a .zip extension.
    Copy-Item -LiteralPath $wv2Nupkg -Destination $zip -Force
    if (Test-Path $wv2Extract) { Remove-Item $wv2Extract -Recurse -Force }
    Expand-Archive -LiteralPath $zip -DestinationPath $wv2Extract -Force
    [System.IO.File]::WriteAllText($wv2Stamp, $wv2Version, $utf8Raw)
    Write-Host "extracted to $wv2Extract"
}

$wv2Problems = @(Get-Wv2AssetProblems)
if ($wv2Problems.Count -gt 0) {
    Write-Host "the cached WebView2 SDK is unusable:" -ForegroundColor Yellow
    foreach ($p in $wv2Problems) { Write-Host ("  " + $p) -ForegroundColor Yellow }
    Write-Host "discarding the cache and downloading it again ..."
    if (Test-Path $wv2Extract) { Remove-Item $wv2Extract -Recurse -Force }
    if (Test-Path -LiteralPath $wv2Nupkg) { Remove-Item -LiteralPath $wv2Nupkg -Force }
    Get-Wv2Sdk

    $wv2Problems = @(Get-Wv2AssetProblems)
    if ($wv2Problems.Count -gt 0) {
        $detail = ($wv2Problems -join '; ')
        throw ("the WebView2 SDK still does not verify after a fresh download: " + $detail + ". " +
               "These three files are copied into what users install, so the build stops here. " +
               "If $wv2Version was bumped on purpose, update the pinned SHA-256 values in this script.")
    }
}
Write-Host ("verified: $wv2Version (sha256 + Microsoft signature)")

foreach ($f in @($coreDll, $wfmDll, $loadDll)) {
    if (-not (Test-Path $f)) { throw "missing SDK asset: $f" }
}

# ---------------------------------------------------------------- 2. encoding rule
Write-Step "Encoding rule (UTF-8 with BOM)"
# PowerShell 5.1 and csc.exe both fall back to the ANSI code page for files without a BOM.
# For valid UTF-8 without a BOM that is worse than an error: the decode succeeds, the
# compile succeeds, and the Chinese text in the product quietly becomes mojibake. The rule
# lives in encoding.ps1, shared with pack.ps1 so it has exactly one implementation.
. (Join-Path $PSScriptRoot 'encoding.ps1')
$encViolations = Invoke-EncodingCheck -Directories @($srcDir, $uiDir, $instDir)
if ($encViolations -eq 2) {
    throw "encoding rule violated: a file is not valid UTF-8 (listed above) and was left untouched on purpose -- adding a BOM would have destroyed its text. Convert it to UTF-8 by hand, then build again."
}
if ($encViolations -gt 0) {
    throw "encoding rule violated: a file holding non-ASCII text had no UTF-8 BOM (listed above). The BOM has been restored, so simply re-running the build will succeed."
}

# ---------------------------------------------------------------- 3. application icon
Write-Step "Application icon"
# An icon in ui\dsh.ico is embedded into the exe (so Explorer, the taskbar and the
# desktop shortcut all show it) and copied next to the exe (so the window and the
# tray icon can load it at runtime). Optional by design: a failure here means the
# build continues without an icon, and a file that does not load as an .ico is
# refused rather than handed to the compiler.
$icoSrc = Join-Path $uiDir 'dsh.ico'
if (-not (Test-Path $icoSrc)) {
    $maker = Join-Path $PSScriptRoot 'make-icon.ps1'
    if (Test-Path $maker) {
        try {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $maker
        } catch {
            Write-Host ("icon generation failed: " + $_.Exception.Message) -ForegroundColor Yellow
        }
    }
}
$ico = $null
if (Test-Path $icoSrc) {
    try {
        Add-Type -AssemblyName System.Drawing
        $probe = New-Object System.Drawing.Icon($icoSrc)
        $probe.Dispose()
        $ico = $icoSrc
        Write-Host ("icon: " + $icoSrc + " (" + (Get-Item $icoSrc).Length + " bytes, loads OK)")
    } catch {
        Write-Host ("icon ignored, it does not load as an .ico: " + $_.Exception.Message) -ForegroundColor Yellow
    }
} else {
    Write-Host "no ui\dsh.ico -- building without an application icon"
}

# ---------------------------------------------------------------- 4. staging dist
Write-Step "Staging dist"
# A previous instance may still be running from dist (the app parks itself in the
# tray on close, by design). Ask it to exit, then clear the folder.
$running = @(Get-Process -Name 'DSH-App' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "stopping $($running.Count) running DSH-App instance(s) ..."
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

if (Test-Path $dist) {
    $attempts = 0
    while ($true) {
        try { Remove-Item $dist -Recurse -Force -ErrorAction Stop; break }
        catch {
            $attempts++
            if ($attempts -ge 5) { throw "cannot clear $dist -- close any running DSH-App and retry. $($_.Exception.Message)" }
            Write-Host "  dist is busy, retrying ($attempts/5) ..."
            Get-Process -Name 'DSH-App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 700
        }
    }
}
New-Item -ItemType Directory -Path $dist -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dist 'ui') -Force | Out-Null

Copy-Item $coreDll $dist -Force
Copy-Item $wfmDll  $dist -Force
Copy-Item $loadDll $dist -Force
Copy-Item (Join-Path $uiDir 'app.html') (Join-Path $dist 'ui') -Force
Get-ChildItem $uiDir -Filter '*.css' -File -ErrorAction SilentlyContinue |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $dist 'ui') -Force }
Get-ChildItem $uiDir -Filter '*.js' -File -ErrorAction SilentlyContinue |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $dist 'ui') -Force }
Get-ChildItem $uiDir -Filter '*.ico' -File -ErrorAction SilentlyContinue |
    ForEach-Object {
        # Next to the exe: the window and the tray icon load it from there.
        Copy-Item $_.FullName $dist -Force
        # And inside ui\ as well, because UiServer only ever serves files from
        # that directory -- this is what makes the page's <link rel="icon"> work.
        Copy-Item $_.FullName (Join-Path $dist 'ui') -Force
    }

Write-Host "staged $(@(Get-ChildItem $dist -Recurse -File).Count) files"

# ---------------------------------------------------------------- 5. compile
Write-Step "Compile"
$csc = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = "$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) { throw "csc.exe not found" }
Write-Host "compiler: $csc"

$sources = Get-ChildItem $srcDir -Filter '*.cs' -File | Sort-Object Name
$outExe  = Join-Path $dist 'DSH-App.exe'

# A response file keeps the command line short and quoting unambiguous.
$rsp = Join-Path $cache 'build.rsp'
$rspLines = New-Object System.Collections.Generic.List[string]
$rspLines.Add('/nologo')
$rspLines.Add('/target:winexe')          # GUI subsystem: console control events must not kill us
$rspLines.Add('/platform:anycpu')
$rspLines.Add('/optimize+')
$rspLines.Add('/warn:4')
# The exe's own icon. Without this, Explorer, the taskbar and the shortcut all fall
# back to the blank default file icon no matter what sits next to the exe.
if ($ico) { $rspLines.Add('/win32icon:"' + $ico + '"') }
$rspLines.Add('/out:"' + $outExe + '"')
$rspLines.Add('/reference:System.dll')
$rspLines.Add('/reference:System.Core.dll')
$rspLines.Add('/reference:System.Drawing.dll')
$rspLines.Add('/reference:System.Windows.Forms.dll')
$rspLines.Add('/reference:System.Management.dll')
$rspLines.Add('/reference:System.Web.dll')          # HttpListener lives here on .NET Framework
$rspLines.Add('/reference:"' + $coreDll + '"')
$rspLines.Add('/reference:"' + $wfmDll + '"')
foreach ($s in $sources) { $rspLines.Add('"' + $s.FullName + '"') }
[System.IO.File]::WriteAllLines($rsp, $rspLines, $utf8Raw)

$p = Start-Process -FilePath $csc -ArgumentList ('@' + $rsp) -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $cache 'csc.out') -RedirectStandardError (Join-Path $cache 'csc.err')
$gbk = [System.Text.Encoding]::GetEncoding(936)   # csc reports in the OEM/ANSI code page
$cout = [System.IO.File]::ReadAllBytes((Join-Path $cache 'csc.out'))
$cerr = [System.IO.File]::ReadAllBytes((Join-Path $cache 'csc.err'))
if ($cout.Length -gt 0) { Write-Host $gbk.GetString($cout) }
if ($cerr.Length -gt 0) { Write-Host $gbk.GetString($cerr) -ForegroundColor Yellow }
if ($p.ExitCode -ne 0 -or -not (Test-Path $outExe)) { throw "compile failed (exit $($p.ExitCode))" }

# ---------------------------------------------------------------- 6. uninstaller
Write-Step "Uninstaller"
# Compiled as its own executable so it can be listed in Windows "Apps & features"
# and so its window can explain what will be removed. It ships in the payload
# cabinet, so the installer drops it into the install folder.
$instDir = Join-Path $root 'installer'
$unSources = @(Join-Path $instDir 'Uninstaller.cs')
$outUninstaller = Join-Path $dist 'Uninstaller.exe'
$unRsp = Join-Path $cache 'uninstaller.rsp'
$gbk = [System.Text.Encoding]::GetEncoding(936)   # csc reports in the OEM/ANSI code page
$unLines = New-Object System.Collections.Generic.List[string]
$unLines.Add('/nologo')
$unLines.Add('/target:winexe')
$unLines.Add('/platform:anycpu')
$unLines.Add('/optimize+')
$unLines.Add('/warn:4')
if ($ico) { $unLines.Add('/win32icon:"' + $ico + '"') }
$unLines.Add('/out:"' + $outUninstaller + '"')
$unLines.Add('/reference:System.dll')
$unLines.Add('/reference:System.Drawing.dll')
$unLines.Add('/reference:System.Windows.Forms.dll')
foreach ($s in $unSources) { $unLines.Add('"' + $s + '"') }
[System.IO.File]::WriteAllLines($unRsp, $unLines, $utf8Raw)

$up = Start-Process -FilePath $csc -ArgumentList ('@' + $unRsp) -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $cache 'un-csc.out') -RedirectStandardError (Join-Path $cache 'un-csc.err')
$uo = [System.IO.File]::ReadAllBytes((Join-Path $cache 'un-csc.out'))
$ue = [System.IO.File]::ReadAllBytes((Join-Path $cache 'un-csc.err'))
if ($uo.Length -gt 0) { Write-Host $gbk.GetString($uo) }
if ($ue.Length -gt 0) { Write-Host $gbk.GetString($ue) -ForegroundColor Yellow }
if ($up.ExitCode -ne 0 -or -not (Test-Path $outUninstaller)) { throw "uninstaller compile failed (exit $($up.ExitCode))" }
Write-Host ("compiled: Uninstaller.exe  ({0:N0} bytes)" -f (Get-Item $outUninstaller).Length)

$exeInfo = Get-Item $outExe
Write-Host ("compiled: {0}  ({1:N0} bytes)" -f $exeInfo.Name, $exeInfo.Length)

# ---------------------------------------------------------------- 7. summary
Write-Step "Done"
Get-ChildItem $dist -Recurse -File |
    Sort-Object FullName |
    ForEach-Object { "{0,9:N0}  {1}" -f $_.Length, $_.FullName.Substring($dist.Length).TrimStart('\') }
Write-Host ""
Write-Host ("total: {0:N0} KB" -f ((Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum).Sum / 1KB))
Write-Host "run:   $outExe" -ForegroundColor Green
