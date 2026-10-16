# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
<#
.SYNOPSIS
  Installs (or removes) the RIFT Revit add-in for the current user (ADR-022).
.DESCRIPTION
  Copies addin/dist/<year>/rift.addin and addin/dist/<year>/rift/* into
  %APPDATA%\Autodesk\Revit\Addins\<year>\ — a stable path, so Revit asks its
  unsigned-publisher question only once. Refuses to run while Revit is running.
  The add-in places its RIFT panel in the NORN ribbon tab, shared by the
  maintainer's suite of Revit plugins (ADR-028).
.PARAMETER Years
  Revit versions to install (default: every version detected in the registry
  for which a staged build exists).
.PARAMETER Source
  Folder containing <year>\rift.addin (default: addin\dist next to this script).
.PARAMETER Uninstall
  Removes the manifest and the rift folder. %LOCALAPPDATA%\rift is kept unless -Purge.
.PARAMETER RemoveLegacy
  Also removes the add-in installed under its former name (zenrvt.addin and the
  zenrvt folder, ADR-028) from the Addins folder of each year. Off by default:
  both add-ins can stay side by side (different AddInId, pipe and data folder)
  until the maintainer chooses to remove the old one. %LOCALAPPDATA%\zenrvt is
  never touched by this switch.
.PARAMETER SkipHashCheck
  Installs without verifying addin/dist/<year>/rift/SHA256SUMS.txt (ADR-022). Off by default:
  a staged folder whose files are missing, extra or different from the sums is refused.
.EXAMPLE
  pwsh -File install.ps1 -Years 2026
  pwsh -File install.ps1 -Years 2026 -RemoveLegacy
  pwsh -File install.ps1 -Uninstall -Years 2026
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [int[]]$Years,
    [string]$Source = (Join-Path $PSScriptRoot 'addin\dist'),
    [switch]$Uninstall,
    [switch]$Purge,
    [switch]$RemoveLegacy,
    [switch]$SkipHashCheck
)

$ErrorActionPreference = 'Stop'

function Get-InstalledRevitYears {
    $years = @()
    foreach ($key in Get-ChildItem 'HKLM:\SOFTWARE\Autodesk\Revit' -ErrorAction SilentlyContinue) {
        if ($key.PSChildName -match '^\d{4}$') { $years += [int]$key.PSChildName }
    }
    return $years | Sort-Object -Unique
}

function Remove-LegacyAddin([string]$addins, [int]$year) {
    # Former product name, before ADR-028. Only the manifest and its folder in the Addins directory.
    $legacyManifest = Join-Path $addins 'zenrvt.addin'
    $legacyTarget = Join-Path $addins 'zenrvt'
    if (-not (Test-Path -LiteralPath $legacyManifest) -and -not (Test-Path -LiteralPath $legacyTarget)) { return }
    if ($PSCmdlet.ShouldProcess("Revit $year", 'remove the legacy zenrvt add-in')) {
        if (Test-Path -LiteralPath $legacyManifest) { Remove-Item -LiteralPath $legacyManifest -Force }
        if (Test-Path -LiteralPath $legacyTarget) { Remove-Item -LiteralPath $legacyTarget -Recurse -Force }
        Write-Host "legacy zenrvt add-in removed from Revit $year"
    }
}

if (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) {
    throw 'Revit is running. Close it before installing or removing the add-in.'
}

if (-not $Years) {
    $Years = Get-InstalledRevitYears | Where-Object { Test-Path -LiteralPath (Join-Path $Source "$_\rift.addin") }
    if (-not $Years) { throw "No staged build found under $Source for an installed Revit version. Build first: dotnet build addin/rift.sln -c 'Release R26'." }
}

foreach ($year in $Years) {
    $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    $manifest = Join-Path $addins 'rift.addin'
    $target = Join-Path $addins 'rift'

    if ($RemoveLegacy) { Remove-LegacyAddin $addins $year }

    if ($Uninstall) {
        if ($PSCmdlet.ShouldProcess("Revit $year", 'remove RIFT add-in')) {
            if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest -Force }
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
            if ($Purge) {
                $data = Join-Path $env:LOCALAPPDATA 'rift'
                if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
            }
            Write-Host "RIFT removed from Revit $year"
        }
        continue
    }

    $stage = Join-Path $Source "$year"
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'rift.addin'))) { throw "No staged build for Revit $year under $stage" }

    # The staged folder must match its SHA256SUMS.txt (ADR-022): a truncated download or a tampered file never gets installed.
    if (-not $SkipHashCheck) {
        $verify = Join-Path $PSScriptRoot 'tools\verify_sha256sums.ps1'
        if (-not (Test-Path -LiteralPath $verify)) { throw "tools\verify_sha256sums.ps1 not found next to install.ps1 (use -SkipHashCheck to install anyway)" }
        & pwsh -NoProfile -File $verify -Folder (Join-Path $stage 'rift')
        if ($LASTEXITCODE -ne 0) { throw "SHA256SUMS.txt does not match the staged build for Revit $year; nothing installed (use -SkipHashCheck to override)" }
    }

    if ($PSCmdlet.ShouldProcess("Revit $year", "install RIFT add-in into $addins")) {
        # Downloaded archives carry the mark of the web; local builds do not, Unblock-File is harmless either way.
        Get-ChildItem -LiteralPath $stage -Recurse -File | Unblock-File
        New-Item -ItemType Directory -Force -Path $addins | Out-Null
        Copy-Item -LiteralPath (Join-Path $stage 'rift.addin') -Destination $manifest -Force
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        # -LiteralPath everywhere: repository paths may contain brackets, which Copy-Item -Path treats as wildcards.
        Copy-Item -LiteralPath (Join-Path $stage 'rift') -Destination $addins -Recurse -Force
        $version = (Get-Item (Join-Path $target 'Rift.Addin.dll')).VersionInfo.ProductVersion
        @{ version = $version; installed_utc = (Get-Date).ToUniversalTime().ToString('o'); source = $stage } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target 'version.json') -Encoding UTF8
        Write-Host "RIFT $version installed for Revit $year → $target"
    }
}
