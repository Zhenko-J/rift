# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
<#
.SYNOPSIS
  Verifies SHA256SUMS.txt against the files of a staged (or installed) RIFT add-in folder (ADR-022).
.DESCRIPTION
  Every line of SHA256SUMS.txt is "<sha256 hex>  <path relative to the folder, forward slashes>".
  Fails when a listed file is missing or differs, and when a file of the folder is not listed
  (SHA256SUMS.txt itself excepted). Used by CI after Stage and by install.ps1 before copying.
.PARAMETER Folder
  The folder holding SHA256SUMS.txt (addin/dist/<year>/rift).
.EXAMPLE
  pwsh -NoProfile -File tools/verify_sha256sums.ps1 -Folder addin/dist/2026/rift
#>
param([Parameter(Mandatory = $true)][string]$Folder)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Folder).Path
$sums = Join-Path $root 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $sums)) { Write-Error "SHA256SUMS.txt missing in $root"; exit 1 }

$listed = @{}
$problems = @()
foreach ($line in Get-Content -LiteralPath $sums) {
    if (-not $line.Trim()) { continue }
    if ($line -notmatch '^([0-9a-fA-F]{64})\s{2}(.+)$') { $problems += "malformed line: $line"; continue }
    $hash = $Matches[1].ToLowerInvariant()
    $rel = $Matches[2].Trim()
    $listed[$rel.ToLowerInvariant()] = $true
    $path = Join-Path $root ($rel -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $path)) { $problems += "missing: $rel"; continue }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $hash) { $problems += "differs: $rel" }
}

foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
    $rel = $file.FullName.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'
    if ($rel -eq 'SHA256SUMS.txt') { continue }
    if (-not $listed.ContainsKey($rel.ToLowerInvariant())) { $problems += "not listed: $rel" }
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "SHA256SUMS: $_" }
    Write-Error "SHA256SUMS.txt does not match $root ($($problems.Count) problem(s))"
    exit 1
}
Write-Host "SHA256SUMS.txt verified: $($listed.Count) file(s) in $root"
exit 0
