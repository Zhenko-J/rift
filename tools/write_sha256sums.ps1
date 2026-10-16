# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
<#
.SYNOPSIS
  Writes SHA256SUMS.txt for a staged RIFT add-in folder (ADR-022), called by the Stage target.
.DESCRIPTION
  One line per file under the folder (SHA256SUMS.txt excepted): "<sha256 hex, lower case>  <path
  relative to the folder, forward slashes>", sorted by path with ordinal comparison, LF line
  endings, UTF-8 without BOM — the format `sha256sum -c` and tools/verify_sha256sums.ps1 read.
  .NET calls only, no cmdlet: Windows PowerShell started from PowerShell 7 (as MSBuild's Exec does
  in a pwsh session or on CI) inherits a PSModulePath that breaks module autoloading.
.PARAMETER Folder
  The folder to hash (addin/dist/<year>/rift).
#>
param([Parameter(Mandatory = $true)][string]$Folder)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($Folder).TrimEnd('\', '/')
if (-not [System.IO.Directory]::Exists($root)) {
    [Console]::Error.WriteLine("write_sha256sums: folder not found: $root")
    exit 1
}
$sha = [System.Security.Cryptography.SHA256]::Create()
$entries = New-Object 'System.Collections.Generic.List[string[]]'
foreach ($file in [System.IO.Directory]::EnumerateFiles($root, '*', [System.IO.SearchOption]::AllDirectories)) {
    $rel = $file.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
    if ($rel -eq 'SHA256SUMS.txt') { continue }
    $stream = [System.IO.File]::OpenRead($file)
    try { $bytes = $sha.ComputeHash($stream) } finally { $stream.Dispose() }
    $hex = [System.BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
    $entries.Add([string[]]@($rel, $hex))
}
$entries.Sort([System.Comparison[string[]]] { param($a, $b) [string]::CompareOrdinal($a[0], $b[0]) })
$builder = New-Object System.Text.StringBuilder
foreach ($e in $entries) { [void]$builder.Append($e[1]).Append('  ').Append($e[0]).Append("`n") }
[System.IO.File]::WriteAllText([System.IO.Path]::Combine($root, 'SHA256SUMS.txt'), $builder.ToString(), (New-Object System.Text.UTF8Encoding($false)))
[Console]::Out.WriteLine("SHA256SUMS.txt written: $($entries.Count) file(s) in $root")
