# ============================================================
# install-nexus-gateway.ps1 ? public-facing installer entrypoint
# ------------------------------------------------------------
# What this does, in order:
#   1. Checks if Microsoft.WindowsDesktop.App 8.x is installed
#   2. If missing, downloads the .NET 8 Desktop Runtime (x64)
#      from Microsoft's aka.ms shortlink and installs it silently
#   3. Downloads Nexus-Gateway-Setup.msi from BEV Cloud blob
#   4. Runs the MSI (UAC prompt fires once)
#
# Beta testers run, in an elevated PowerShell:
#   iex (iwr https://bevplatformst5596.blob.core.windows.net/installers/install-nexus-gateway.ps1 -UseBasicParsing).Content
# ============================================================

$ErrorActionPreference = "Stop"

$msiUrl     = "https://bevplatformst5596.blob.core.windows.net/installers/Nexus-Gateway-Setup.msi"
$runtimeUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"

$tmp = Join-Path $env:TEMP ("nexus-gateway-install-" + [Guid]::NewGuid().ToString("N").Substring(0,8))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

Write-Host ""
Write-Host "  NEXUS GATEWAY  -  installer" -ForegroundColor Yellow
Write-Host "  ---------------------------" -ForegroundColor DarkGray
Write-Host ""

# ---- 1. Admin check ----
$isAdmin = ([Security.Principal.WindowsPrincipal] `
            [Security.Principal.WindowsIdentity]::GetCurrent()
          ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "  This installer must run from an elevated PowerShell." -ForegroundColor Red
    Write-Host "  Right-click PowerShell -> Run as Administrator, then re-run." -ForegroundColor DarkGray
    exit 1
}

# ---- 2. .NET 8 Desktop Runtime check ----
Write-Host "  [1/3] Checking for .NET 8 Desktop Runtime..." -ForegroundColor Cyan
$installed = $false
try {
    $runtimes = & dotnet --list-runtimes 2>$null
    if ($runtimes -match "Microsoft\.WindowsDesktop\.App 8\.") {
        $installed = $true
    }
} catch { $installed = $false }

if ($installed) {
    Write-Host "        already present." -ForegroundColor DarkGray
} else {
    Write-Host "        not found. Downloading from Microsoft..." -ForegroundColor Yellow
    $runtimeExe = Join-Path $tmp "windowsdesktop-runtime-8-win-x64.exe"
    try {
        Invoke-WebRequest -Uri $runtimeUrl -OutFile $runtimeExe -UseBasicParsing
    } catch {
        Write-Host "        FAILED to download runtime: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
    Write-Host "        installing (silent)..." -ForegroundColor Yellow
    $rt = Start-Process -FilePath $runtimeExe -ArgumentList "/install","/quiet","/norestart" -Wait -PassThru
    if ($rt.ExitCode -ne 0 -and $rt.ExitCode -ne 3010) {
        Write-Host "        runtime installer exit code $($rt.ExitCode); aborting." -ForegroundColor Red
        exit 1
    }
    Write-Host "        runtime installed." -ForegroundColor Green
}

# ---- 3. Download MSI ----
Write-Host "  [2/3] Downloading Nexus Gateway MSI..." -ForegroundColor Cyan
$msiPath = Join-Path $tmp "Nexus-Gateway-Setup.msi"
try {
    Invoke-WebRequest -Uri $msiUrl -OutFile $msiPath -UseBasicParsing
} catch {
    Write-Host "        FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
$mb = [math]::Round((Get-Item $msiPath).Length / 1MB, 2)
Write-Host "        done ($mb MB)." -ForegroundColor DarkGray

# ---- 4. Run MSI ----
Write-Host "  [3/3] Installing..." -ForegroundColor Cyan
$msi = Start-Process -FilePath "msiexec.exe" -ArgumentList "/i","`"$msiPath`"","/passive","/norestart" -Wait -PassThru
if ($msi.ExitCode -ne 0 -and $msi.ExitCode -ne 3010) {
    Write-Host "        MSI exit code $($msi.ExitCode). See %TEMP% for msiexec log." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "  Nexus Gateway installed." -ForegroundColor Green
Write-Host "  The tray will launch automatically; complete the first-run setup wizard." -ForegroundColor DarkGray
Write-Host ""

Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
