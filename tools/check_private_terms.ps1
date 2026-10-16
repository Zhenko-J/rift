# Maintainer-only privacy check (pre-commit / pre-push).
# Reads a regular expression from a file that lives OUTSIDE the repository and
# refuses the commit when any staged file matches it. The file is never committed.
#
# Location of the private regex (first found wins):
#   $env:RIFT_PRIVATE_TERMS_FILE
#   $env:LOCALAPPDATA\rift\private-terms.txt
#   $env:LOCALAPPDATA\zenrvt\private-terms.txt   (legacy location from before the rename, ADR-028)
#
# Usage: pwsh -NoProfile -File tools/check_private_terms.ps1 <files...>
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Files)

$ErrorActionPreference = 'Stop'
$candidates = @(
    $env:RIFT_PRIVATE_TERMS_FILE,
    (Join-Path $env:LOCALAPPDATA 'rift\private-terms.txt'),
    # Legacy fallback (ADR-028): keeps the guard working until the file is moved to the rift folder.
    (Join-Path $env:LOCALAPPDATA 'zenrvt\private-terms.txt')
) | Where-Object { $_ }
$termsFile = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $termsFile) {
    Write-Error "private-terms file not found (looked in: $($candidates -join '; ')). Refusing to commit from an unconfigured machine."
    exit 1
}

$pattern = (Get-Content -LiteralPath $termsFile -Raw).Trim()
if (-not $pattern) { exit 0 }

$hits = @()
foreach ($f in $Files) {
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { continue }
    $m = Select-String -LiteralPath $f -Pattern $pattern -CaseSensitive:$false
    if ($m) { $hits += $m }
}

if ($hits.Count -gt 0) {
    foreach ($h in $hits) { Write-Host ("{0}:{1}: private term" -f $h.Path, $h.LineNumber) }
    Write-Error "$($hits.Count) private term(s) found. Remove them before committing."
    exit 1
}
exit 0
