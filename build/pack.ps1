# DSH-App packaging script  (ASCII only -- see AGENTS.md rule 1)
#
# Turns dist\DSH-App into something a non-technical person can receive:
#
#   dist\package\DSH-App-setup.exe   +  DSH-App.dat   (two files, IExpress bundle)
#   dist\package\DSH-App-standalone.exe               (one file, payload embedded)
#
# Uses only tools that ship with Windows: makecab.exe and iexpress.exe.
#
#   powershell -ExecutionPolicy Bypass -File build\pack.ps1

$ErrorActionPreference = 'Stop'

$root     = Split-Path -Parent $PSScriptRoot
$distApp  = Join-Path $root 'dist\DSH-App'
$pkgDir   = Join-Path $root 'dist\package'
$work     = Join-Path $root '.cache\pack'
$instDir  = Join-Path $root 'installer'

$setupExe = Join-Path $pkgDir 'DSH-App-setup.exe'
$dataFile = Join-Path $pkgDir 'DSH-App.dat'
$soloExe  = Join-Path $pkgDir 'DSH-App-standalone.exe'

function Write-Step($t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }
$gbk = [System.Text.Encoding]::GetEncoding(936)

# ---------------------------------------------------------------- 0. checks
Write-Step "Preflight"
if (-not (Test-Path (Join-Path $distApp 'DSH-App.exe'))) {
    throw "dist\DSH-App\DSH-App.exe not found -- run build\build.ps1 first."
}

# Every file the payload must contain, checked before anything is built.
#
# Uninstaller.exe is in this list because leaving it out produced a package that installed
# a copy with no way to uninstall it -- and nothing said so. The payload is assembled from
# whatever is in dist, so a build that skipped one of its steps silently shipped a package
# missing that file. A missing file here is always a build mistake, so fail instead.
$required = @(
    'DSH-App.exe',
    'Uninstaller.exe',          # without it "Apps & features" has nothing to run
    'dsh.ico',
    'WebView2Loader.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.WinForms.dll',
    'ui\app.html'
)
$missing = @()
foreach ($rel in $required) {
    if (-not (Test-Path (Join-Path $distApp $rel))) { $missing += $rel }
}
if ($missing.Count -gt 0) {
    Write-Host ("missing from dist\DSH-App: " + ($missing -join ', ')) -ForegroundColor Red
    throw "the payload is incomplete -- run build\build.ps1 (in full) before pack\pack.ps1."
}
Write-Host ("payload has all " + $required.Count + " required files")

$makecab = Join-Path $env:SystemRoot 'System32\makecab.exe'
$iexpress = Join-Path $env:SystemRoot 'System32\iexpress.exe'
foreach ($t in @($makecab, $iexpress)) {
    if (-not (Test-Path $t)) { throw "required Windows tool missing: $t" }
}
Write-Host "makecab / iexpress present"

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null
if (Test-Path $pkgDir) { Remove-Item $pkgDir -Recurse -Force }
New-Item -ItemType Directory -Path $pkgDir -Force | Out-Null

# ---------------------------------------------------------------- 1. payload cab
Write-Step "Building payload cabinet"
# A directive file keeps exact relative paths (ui\app.html) and avoids depending
# on makecab's recursive behaviour. MSZIP is chosen deliberately: the installer's
# built-in reader understands it, so extraction can still work if the system
# expand.exe is unavailable.
$files = Get-ChildItem $distApp -Recurse -File | Sort-Object FullName
if ($files.Count -eq 0) { throw "dist\DSH-App is empty" }

$ddf = Join-Path $work 'payload.ddf'
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('.Set CabinetNameTemplate=DSH-App.dat')
$lines.Add('.Set DiskDirectory1=' + $pkgDir)
$lines.Add('.Set Cabinet=on')
$lines.Add('.Set Compress=on')
$lines.Add('.Set CompressionType=MSZIP')
$lines.Add('.Set CompressionMemory=21')
$lines.Add('.Set MaxDiskSize=0')
$lines.Add('.Set UniqueFiles=off')
$lines.Add('.Set RptFileName=nul')
$lines.Add('.Set InfFileName=nul')
foreach ($f in $files) {
    $rel = $f.FullName.Substring($distApp.Length).TrimStart('\')
    $lines.Add('"' + $f.FullName + '" "' + $rel + '"')
    Write-Host ("  + {0,9:N0}  {1}" -f $f.Length, $rel)
}
[System.IO.File]::WriteAllLines($ddf, $lines, [System.Text.Encoding]::ASCII)

$p = Start-Process -FilePath $makecab -ArgumentList @('/F', $ddf) -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $work 'makecab.out') -RedirectStandardError (Join-Path $work 'makecab.err')
$mo = [System.IO.File]::ReadAllBytes((Join-Path $work 'makecab.out'))
if ($mo.Length -gt 0) { Write-Host $gbk.GetString($mo) }
if ($p.ExitCode -ne 0 -or -not (Test-Path $dataFile)) { throw "makecab failed (exit $($p.ExitCode))" }
Write-Host ("cabinet: {0:N0} bytes" -f (Get-Item $dataFile).Length) -ForegroundColor Green

$rawTotal = ($files | Measure-Object Length -Sum).Sum
Write-Host ("raw payload {0:N0} bytes -> cabinet {1:N0} bytes" -f $rawTotal, (Get-Item $dataFile).Length)

# ---------------------------------------------------------------- 2. installer exe
Write-Step "Compiling installer"
# Same icon as the product so the installer does not look like a blank file either.
# Optional: a missing or unreadable icon is refused, never fatal.
$ico = $null
$icoSrc = Join-Path $root 'ui\dsh.ico'
if (Test-Path $icoSrc) {
    try {
        Add-Type -AssemblyName System.Drawing
        $probe = New-Object System.Drawing.Icon($icoSrc)
        $probe.Dispose()
        $ico = $icoSrc
        Write-Host "installer icon: $icoSrc"
    } catch {
        Write-Host ("installer icon ignored, it does not load as an .ico: " + $_.Exception.Message) -ForegroundColor Yellow
    }
}
$csc = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\csc.exe" }

$utf8Raw = New-Object System.Text.UTF8Encoding($false)

# The same encoding rule build.ps1 enforces, from the same implementation: the installer
# sources ship inside the payload, so a missing BOM would corrupt their Chinese text just
# as surely as it would in the app.
. (Join-Path $PSScriptRoot 'encoding.ps1')
$encViolations = Invoke-EncodingCheck -Directories @($instDir)
if ($encViolations -eq 2) {
    throw "encoding rule violated: a file is not valid UTF-8 (listed above) and was left untouched on purpose -- adding a BOM would have destroyed its text. Convert it to UTF-8 by hand, then pack again."
}
if ($encViolations -gt 0) {
    throw "encoding rule violated: a file holding non-ASCII text had no UTF-8 BOM (listed above). The BOM has been restored, so simply re-running pack will succeed."
}

# Uninstaller.cs has its own entry point and is compiled separately by build.ps1;
# including it here would give this assembly two Main methods.
$sources = Get-ChildItem $instDir -Filter '*.cs' -File |
    Where-Object { $_.Name -ne 'Uninstaller.cs' } | Sort-Object Name

# Two variants so both distribution shapes can be offered:
#   -setup.exe      small, needs DSH-App.dat beside it
#   -standalone.exe one file, cabinet embedded as a resource
$variants = @(
    @{ Name = 'DSH-App-installer.exe';  Embed = $false; Out = (Join-Path $pkgDir 'DSH-App-installer.exe') },
    @{ Name = 'DSH-App-standalone.exe'; Embed = $true;  Out = $soloExe }
)

foreach ($v in $variants) {
    $rsp = Join-Path $work ('rsp-' + $v.Name + '.txt')
    $rspLines = New-Object System.Collections.Generic.List[string]
    $rspLines.Add('/nologo')
    $rspLines.Add('/target:winexe')
    $rspLines.Add('/optimize+')
    if ($ico) { $rspLines.Add('/win32icon:"' + $ico + '"') }
    $rspLines.Add('/out:"' + $v.Out + '"')
    $rspLines.Add('/reference:System.dll')
    $rspLines.Add('/reference:System.Core.dll')
    $rspLines.Add('/reference:System.Drawing.dll')
    $rspLines.Add('/reference:System.Windows.Forms.dll')
    if ($v.Embed) {
        $rspLines.Add('/resource:"' + $dataFile + '",payload.cab')
    }
    foreach ($s in $sources) { $rspLines.Add('"' + $s.FullName + '"') }
    [System.IO.File]::WriteAllLines($rsp, $rspLines, [System.Text.Encoding]::ASCII)

    $cp = Start-Process -FilePath $csc -ArgumentList ('@' + $rsp) -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput (Join-Path $work 'csc.out') -RedirectStandardError (Join-Path $work 'csc.err')
    $co = [System.IO.File]::ReadAllBytes((Join-Path $work 'csc.out'))
    $ce = [System.IO.File]::ReadAllBytes((Join-Path $work 'csc.err'))
    if ($co.Length -gt 0) { Write-Host $gbk.GetString($co) }
    if ($ce.Length -gt 0) { Write-Host $gbk.GetString($ce) -ForegroundColor Yellow }
    if ($cp.ExitCode -ne 0 -or -not (Test-Path $v.Out)) { throw "installer compile failed: $($v.Name)" }
    Write-Host ("  {0}  {1:N0} bytes" -f $v.Name, (Get-Item $v.Out).Length)
}

# ---------------------------------------------------------------- 3. IExpress bundle
Write-Step "Building single-file bundle (IExpress)"
$sed = Join-Path $work 'bundle.sed'
# IExpress runs the install command from its temporary extraction folder, so a
# bare executable name is correct and avoids any path-length or space issues.
#
# IExpress runs the install command from its temporary extraction folder, so a
# bare executable name is correct and avoids any path-length or space issues.
#
# Two traps handled here:
#   * Interpolated strings ("Key=$var") are used instead of concatenation
#     ('Key=' + $var). Inside an array literal, a leading + is parsed as ARRAY
#     concatenation, not string concatenation, which silently splits the entry
#     into two array elements -- exactly two extra lines in the SED, which
#     IExpress rejects with a bare exit code 1 and no message.
#   * Explicit CRLF: IExpress is a legacy parser.
$sedLines = @(
    '[Version]',
    'Class=IEXPRESS',
    'SEDVersion=3',
    '[Options]',
    'PackagePurpose=InstallApp',
    'ShowInstallProgramWindow=1',
    'HideExtractAnimation=0',
    'UseLongFileName=1',
    'InsideCompressed=0',
    'CAB_FixedSize=0',
    'CAB_ResvCodeSigning=0',
    'RebootMode=N',
    'InstallPrompt=',
    'DisplayLicense=',
    'FinishMessage=',
    "TargetName=$setupExe",
    'FriendlyName=DSH-App Setup',
    'AppLaunched=DSH-App-installer.exe',
    'PostInstallCmd=<None>',
    'AdminQuietInstCmd=',
    'UserQuietInstCmd=',
    'SourceFiles=SourceFiles',
    '[Strings]',
    'FILE0="DSH-App-installer.exe"',
    'FILE1="DSH-App.dat"',
    '[SourceFiles]',
    "SourceFiles0=$pkgDir",
    '[SourceFiles0]',
    '%FILE0%=',
    '%FILE1%='
)
[System.IO.File]::WriteAllText($sed, ($sedLines -join "`r`n") + "`r`n", [System.Text.Encoding]::ASCII)
Write-Host "sed: $($sedLines.Count) lines, CRLF"

$ip = Start-Process -FilePath $iexpress -ArgumentList @('/N', '/Q', $sed) -NoNewWindow -Wait -PassThru
if ($ip.ExitCode -ne 0) { throw "iexpress failed (exit $($ip.ExitCode))" }

# IExpress consumes DSH-App-installer.exe as a source file, so recompile the loose
# copy afterwards to leave a pristine one in the package folder. It writes to a
# DIFFERENT name: overwriting $setupExe here would replace the finished bundle
# with the small uncompressed installer.
$loosePath = Join-Path $pkgDir 'DSH-App-installer.exe'
$rsp = Join-Path $work 'rsp-rebuild.txt'
$rspLines = New-Object System.Collections.Generic.List[string]
$rspLines.Add('/nologo')
$rspLines.Add('/target:winexe')
$rspLines.Add('/optimize+')
if ($ico) { $rspLines.Add('/win32icon:"' + $ico + '"') }
$rspLines.Add('/out:"' + $loosePath + '"')
$rspLines.Add('/reference:System.dll')
$rspLines.Add('/reference:System.Core.dll')
$rspLines.Add('/reference:System.Drawing.dll')
$rspLines.Add('/reference:System.Windows.Forms.dll')
foreach ($s in $sources) { $rspLines.Add('"' + $s.FullName + '"') }
[System.IO.File]::WriteAllLines($rsp, $rspLines, [System.Text.Encoding]::ASCII)
$cp = Start-Process -FilePath $csc -ArgumentList ('@' + $rsp) -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $work 'csc2.out') -RedirectStandardError (Join-Path $work 'csc2.err')
if ($cp.ExitCode -ne 0) {
    Write-Host "note: could not refresh the loose installer copy" -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 4. verify
Write-Step "Verifying packages"
$bundle = Get-Item $setupExe -ErrorAction SilentlyContinue
if (-not $bundle) { throw "the bundle was not produced" }

# A bundle that is barely larger than the installer itself means the payload was
# not embedded -- the exact failure mode that a silent IExpress error produces.
$sizeOk = $bundle.Length -gt 100000
Write-Host ("bundle {0:N0} bytes, payload-in-bundle check: {1}" -f $bundle.Length, $(if ($sizeOk) { "PASS" } else { "FAIL" }))
if (-not $sizeOk) { throw "bundle looks wrong: only $($bundle.Length) bytes, the payload does not appear to be inside" }

# Confirm the cabinet really contains every product file.
$expand = Join-Path $env:SystemRoot 'System32\expand.exe'
$listDir = Join-Path $work 'cablist'
if (Test-Path $listDir) { Remove-Item $listDir -Recurse -Force }
New-Item -ItemType Directory -Path $listDir -Force | Out-Null
$lp = Start-Process -FilePath $expand -ArgumentList @('-D', $dataFile) -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $work 'cablist.txt') -RedirectStandardError (Join-Path $work 'cablist.err')
$cabList = $gbk.GetString([System.IO.File]::ReadAllBytes((Join-Path $work 'cablist.txt')))
$expected = @('DSH-App.exe', 'Uninstaller.exe', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll', 'app.html')
# The product icon travels with the payload so the installed copy can show it too.
if ($ico) { $expected += 'dsh.ico' }
foreach ($e in $expected) {
    $found = $cabList -match [regex]::Escape($e)
    Write-Host ("  {0,-40} {1}" -f $e, $(if ($found) { "in cabinet" } else { "MISSING" }))
    if (-not $found) { throw "cabinet is missing $e" }
}

# ---------------------------------------------------------------- 5. summary
Write-Step "Packages"
Get-ChildItem $pkgDir -File | Sort-Object Name | ForEach-Object {
    "{0,12:N0}  {1}" -f $_.Length, $_.Name
}
Write-Host ""
Write-Host ("HAND THIS OUT : {0}  ({1:N0} KB, one file)" -f $setupExe, ($bundle.Length / 1KB)) -ForegroundColor Green
$solo = Get-Item $soloExe -ErrorAction SilentlyContinue
if ($solo) {
    Write-Host ("no-IExpress alternative : {0}  ({1:N0} KB, one file)" -f $soloExe, ($solo.Length / 1KB)) -ForegroundColor Green
}
Write-Host ("two-file alternative    : DSH-App-installer.exe + DSH-App.dat")
Write-Host ""

# The published checksums. The installer is handed out as a single file by whatever channel
# the user chooses (a chat message, a cloud drive, a USB stick), and the only thing that
# tells a recipient their copy arrived intact -- and is the copy that was built here -- is a
# hash printed somewhere they can compare it against. Free to produce, worth pasting next to
# the download link.
Write-Step "SHA-256"
Get-ChildItem $pkgDir -File | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.Name
}
Write-Host ""
Write-Host ("payload cabinet : {0} bytes" -f (Get-Item $dataFile).Length)
