# ============================================================
# build.ps1 - Nexus Gateway bootstrapper build orchestration
# Phase 1c-2 (NEXUS retrofit + bootstrapper)
# ------------------------------------------------------------
# Produces:
#   dist\Nexus-Gateway-Setup.msi      (the MSI)
#   dist\Nexus-Gateway-Setup.exe      (the bootstrapper - SHIP THIS)
#
# The .exe bundles the .NET 8 Desktop Runtime + the MSI. It
# installs the runtime first if the target machine lacks it,
# then installs the MSI. This is what fixes the dev-box failure.
#
# Prerequisites on the build box:
#   - .NET 8 SDK
#   - PowerShell, internet (first run downloads WiX + runtime)
# ============================================================

$ErrorActionPreference = "Stop"
$scriptRoot = $PSScriptRoot
if (-not $scriptRoot) { $scriptRoot = (Get-Location).Path }
Set-Location $scriptRoot

Write-Host "[nexus-gateway-build] scriptRoot = $scriptRoot" -ForegroundColor Cyan

# ---- 0. CLEAN. Wipe all prior build artifacts so a fresh expand of the
# source zip over an old folder can NEVER link against a stale compiled
# DLL (this caused the build constant to lag: publish reused a cached
# BEVGateway.Shared.dll in bin\Release instead of recompiling). We remove
# every obj/bin/dist plus the generated harvest fragments.
Write-Host "[nexus-gateway-build] Cleaning prior build artifacts..." -ForegroundColor Cyan
Get-ChildItem -Path $scriptRoot -Recurse -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @('obj','bin','dist') } |
    ForEach-Object { Remove-Item -Recurse -Force $_.FullName -ErrorAction SilentlyContinue }
Remove-Item -Force (Join-Path $scriptRoot "installer\ServiceRuntimeFiles.wxs") -ErrorAction SilentlyContinue
Remove-Item -Force (Join-Path $scriptRoot "installer\TrayRuntimeFiles.wxs") -ErrorAction SilentlyContinue

# ---- 1. Tool restore ----
Write-Host "[nexus-gateway-build] Restoring wix tool..." -ForegroundColor Cyan
if (-not (Test-Path ".\.config\dotnet-tools.json")) {
    dotnet new tool-manifest --force | Out-Null
    dotnet tool install wix --version 5.0.2 | Out-Null
} else {
    dotnet tool restore | Out-Null
}

# ---- 1b. WiX Util extension ----
Write-Host "[nexus-gateway-build] Ensuring WiX Util extension..." -ForegroundColor Cyan
& dotnet wix extension add WixToolset.Util.wixext/5.0.2 2>&1 | Out-Host

# ---- 2. Publish Service ----
$serviceProj = Join-Path $scriptRoot "src\BEVGateway.Service\BEVGateway.Service.csproj"
$servicePub  = Join-Path $scriptRoot "dist\publish-service"
Write-Host "[nexus-gateway-build] Publishing Service..." -ForegroundColor Cyan
if (Test-Path $servicePub) { Remove-Item -Recurse -Force $servicePub }
# SELF-CONTAINED: bundle the .NET 8 runtime into the publish output
# so the target box needs NO separate runtime install. The harvest
# step below sweeps these runtime files into the MSI. This is the
# permanent fix for "service failed to start" on a box lacking the
# .NET 8 Desktop Runtime.
dotnet publish $serviceProj -c Release -o $servicePub -r win-x64 --self-contained true /p:PublishSingleFile=false | Out-Host

# ---- 3. Publish Tray ----
$trayProj = Join-Path $scriptRoot "src\BEVGateway.Tray\BEVGateway.Tray.csproj"
$trayPub  = Join-Path $scriptRoot "dist\publish-tray"
Write-Host "[nexus-gateway-build] Publishing Tray..." -ForegroundColor Cyan
if (Test-Path $trayPub) { Remove-Item -Recurse -Force $trayPub }
# SELF-CONTAINED: same as the service - runtime bundled in.
dotnet publish $trayProj -c Release -o $trayPub -r win-x64 --self-contained true /p:PublishSingleFile=false | Out-Host

# ---- 4. Harvest runtime files into WiX fragments ----
$serviceHarvest = Join-Path $scriptRoot "installer\ServiceRuntimeFiles.wxs"
$trayHarvest    = Join-Path $scriptRoot "installer\TrayRuntimeFiles.wxs"
Write-Host "[nexus-gateway-build] Harvesting runtime files..." -ForegroundColor Cyan

function Emit-HarvestWxs {
    param(
        [string]$Source,
        [string]$Output,
        [string]$ComponentGroupId,
        [string]$Bindpath,
        [string]$ExcludeFile,
        [string[]]$ExcludeFilesByName = @()
    )
    $files = Get-ChildItem -Path $Source -Recurse -File |
             Where-Object {
                 $_.Name -ne $ExcludeFile -and
                 $_.Extension -ne ".pdb" -and
                 ($ExcludeFilesByName -notcontains $_.Name)
             }

    $sb = New-Object System.Text.StringBuilder
    $null = $sb.AppendLine("<" + "?xml version=`"1.0`" encoding=`"UTF-8`"?" + ">")
    $null = $sb.AppendLine("<Wix xmlns=`"http://wixtoolset.org/schemas/v4/wxs`">")
    $null = $sb.AppendLine("  <Fragment>")
    $null = $sb.AppendLine("    <ComponentGroup Id=`"$ComponentGroupId`" Directory=`"INSTALLDIR`">")

    foreach ($f in $files) {
        $relative = $f.FullName.Substring($Source.Length).TrimStart('\','/')
        $guid = [System.Guid]::NewGuid().ToString().ToUpper()
        $id   = ("F_" + $Bindpath + "_" + ($relative -replace '[^A-Za-z0-9]','_'))
        if ($id.Length -gt 70) { $id = $id.Substring(0, 60) + ($id.GetHashCode() -band 0x7FFFFFFF) }
        $null = $sb.AppendLine("      <Component Id=`"$id`" Guid=`"$guid`">")
        $null = $sb.AppendLine("        <File Id=`"$id`_F`" Source=`"!(bindpath.$Bindpath)\$relative`" KeyPath=`"yes`" />")
        $null = $sb.AppendLine("      </Component>")
    }

    $null = $sb.AppendLine("    </ComponentGroup>")
    $null = $sb.AppendLine("  </Fragment>")
    $null = $sb.AppendLine("</Wix>")
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Output, $sb.ToString(), $utf8NoBom)
}

$serviceFileNames = Get-ChildItem -Path $servicePub -Recurse -File | Select-Object -ExpandProperty Name | Select-Object -Unique

Emit-HarvestWxs -Source $servicePub -Output $serviceHarvest `
    -ComponentGroupId "ServiceRuntimeFiles" -Bindpath "service" `
    -ExcludeFile "BEVGateway.Service.exe"

Emit-HarvestWxs -Source $trayPub -Output $trayHarvest `
    -ComponentGroupId "TrayRuntimeFiles" -Bindpath "tray" `
    -ExcludeFile "BEVGateway.Tray.exe" `
    -ExcludeFilesByName $serviceFileNames

# ---- 5. Build MSI ----
Write-Host "[nexus-gateway-build] Compiling MSI..." -ForegroundColor Cyan
$mainWxs = Join-Path $scriptRoot "installer\NexusGateway.wxs"
$installerDir = Join-Path $scriptRoot "installer"
$outMsi  = Join-Path $scriptRoot "dist\Nexus-Gateway-Setup.msi"
if (Test-Path $outMsi) { Remove-Item -Force $outMsi }

dotnet wix build `
    $mainWxs $serviceHarvest $trayHarvest `
    -ext WixToolset.Util.wixext `
    -bindpath "service=$servicePub" `
    -bindpath "tray=$trayPub" `
    -bindpath "installer=$installerDir" `
    -arch x64 `
    -out $outMsi | Out-Host

if (-not (Test-Path $outMsi)) {
    Write-Host "[nexus-gateway-build] FAILED -- no MSI emitted." -ForegroundColor Red
    exit 1
}
$msiSizeMb = [math]::Round((Get-Item $outMsi).Length / 1MB, 2)
Write-Host ""
Write-Host "[nexus-gateway-build] DONE." -ForegroundColor Green
Write-Host "[nexus-gateway-build] MSI:           $outMsi" -ForegroundColor Green
Write-Host "[nexus-gateway-build] MSI size:      $msiSizeMb MB" -ForegroundColor Green
Write-Host "[nexus-gateway-build] Install script: installer\install-nexus-gateway.ps1" -ForegroundColor Green
Write-Host ""
Write-Host "[nexus-gateway-build] Next: .\upload-msi.ps1 to publish both for beta testers." -ForegroundColor DarkGray
