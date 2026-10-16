# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
<#
.SYNOPSIS
  Installs RIFT in one command: builds and installs the Revit add-in, sets up the MCP server, registers it in Claude Code.
.DESCRIPTION
  Runs from a clone of the repository, in PowerShell 7, with Revit closed. Every step uses the repository's own tools
  (dotnet, install.ps1, uv, rift-mcp) and stops at the first failure with the reason and what to do. Nothing needs
  administrator rights: the add-in goes to %APPDATA%\Autodesk\Revit\Addins\<year>\, the server stays in server\.venv.
  Safe to run again: it rebuilds, reinstalls and re-registers.

  Steps: 1 checks, 2 build the add-in, 3 install the add-in, 4 set up the server, 5 register in Claude Code,
  6 verify, 7 next steps.
.PARAMETER Year
  Revit version to install for (only 2026 is configured today).
.PARAMETER Scope
  Claude Code registration scope: user (default, every project), local (this folder, you only) or project (.mcp.json).
.PARAMETER CheckOnly
  Runs the checks of step 1 and stops. Changes nothing.
.PARAMETER SkipAddin
  Does not build or install the add-in (server only; Revit may stay open).
.PARAMETER SkipServer
  Does not set up nor register the server (add-in only).
.PARAMETER NoRegister
  Sets up the server but does not run `claude mcp add`; prints the configuration block for any MCP client instead.
.EXAMPLE
  pwsh -File setup.ps1
  pwsh -File setup.ps1 -CheckOnly
  pwsh -File setup.ps1 -Scope local
  pwsh -File setup.ps1 -SkipAddin -NoRegister
#>
[CmdletBinding()]
param(
    [int]$Year = 2026,
    [ValidateSet('user', 'local', 'project')][string]$Scope = 'user',
    [switch]$CheckOnly,
    [switch]$SkipAddin,
    [switch]$SkipServer,
    [switch]$NoRegister
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$server = Join-Path $root 'server'
$solution = Join-Path $root 'addin\rift.sln'
$configuration = 'Release R' + ($Year % 100).ToString()
$supportedYears = @(2026)

function Write-Step([string]$n, [string]$text) { Write-Host ''; Write-Host "[$n] $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "    ok  $text" -ForegroundColor Green }
function Write-Note([string]$text) { Write-Host "    --  $text" -ForegroundColor DarkGray }
function Stop-Setup([string]$problem, [string]$fix) {
    Write-Host "    !!  $problem" -ForegroundColor Red
    if ($fix) { Write-Host "        fix: $fix" -ForegroundColor Yellow }
    Write-Host ''
    Write-Host 'RIFT setup stopped. Nothing after this step was done.' -ForegroundColor Red
    exit 1
}

# ── 1. checks ──────────────────────────────────────────────────────────────────────────────
Write-Step '1/7' 'Checks'
$problems = @()

if (-not $IsWindows) { $problems += , @('RIFT runs on Windows only (named pipes, Windows ACLs).', 'Use a Windows 11 machine with Revit.') }
else { Write-Ok 'Windows' }

if ($PSVersionTable.PSVersion.Major -lt 7) { $problems += , @("PowerShell $($PSVersionTable.PSVersion) found; PowerShell 7 is needed.", 'winget install Microsoft.PowerShell, then run: pwsh -File setup.ps1') }
else { Write-Ok "PowerShell $($PSVersionTable.PSVersion)" }

if ($supportedYears -notcontains $Year) { $problems += , @("Revit $Year has no build configuration yet (supported: $($supportedYears -join ', ')).", 'Run with -Year 2026, or open an issue for another year.') }

$revitKey = "HKLM:\SOFTWARE\Autodesk\Revit\$Year"
if (-not (Test-Path -LiteralPath $revitKey)) { $problems += , @("Revit $Year is not installed on this machine (registry key $revitKey missing).", "Install Autodesk Revit $Year, or pass -Year for the version you have.") }
else { Write-Ok "Revit $Year installed" }

if (-not $SkipAddin) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $sdkOk = $false
    if ($dotnet) {
        foreach ($line in (& dotnet --list-sdks 2>$null)) {
            if ($line -match '^(\d+)\.(\d+)\.(\d+)') {
                $v = [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
                if ($v -ge [version]'8.0.400') { $sdkOk = $true }
            }
        }
    }
    if (-not $sdkOk) { $problems += , @('.NET SDK 8.0.400 or later not found (needed to build the add-in).', 'winget install Microsoft.DotNet.SDK.8') }
    else { Write-Ok '.NET SDK 8.0.400 or later' }

    if (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) {
        $problems += , @('Revit is running: the add-in cannot be installed while Revit holds its files.', 'Save your work, close Revit, then run setup.ps1 again (or use -SkipAddin to set up the server only).')
    }
    else { Write-Ok 'Revit is closed' }
}

if (-not $SkipServer) {
    if (-not (Get-Command uv -ErrorAction SilentlyContinue)) { $problems += , @('uv not found (it installs Python 3.13 and the server dependencies).', 'winget install astral-sh.uv, then open a new terminal') }
    else { Write-Ok "uv $((& uv --version) -replace '^uv\s+', '')" }

    if (-not $NoRegister) {
        if (Get-Command claude -ErrorAction SilentlyContinue) { Write-Ok 'Claude Code CLI (claude) found' }
        else { Write-Note 'Claude Code CLI (claude) not found: step 5 will print a configuration block for your MCP client instead' }
    }
}

if ($problems.Count -gt 0) {
    foreach ($p in $problems) {
        Write-Host "    !!  $($p[0])" -ForegroundColor Red
        Write-Host "        fix: $($p[1])" -ForegroundColor Yellow
    }
    Write-Host ''
    Write-Host "RIFT setup stopped at the checks: $($problems.Count) problem(s). Nothing was changed." -ForegroundColor Red
    exit 1
}
if ($CheckOnly) {
    Write-Host ''
    Write-Host 'All checks passed (-CheckOnly: nothing was changed).' -ForegroundColor Green
    exit 0
}

# ── 2. build the add-in ────────────────────────────────────────────────────────────────────
if ($SkipAddin) {
    Write-Step '2/7' 'Build the add-in: skipped (-SkipAddin)'
    Write-Step '3/7' 'Install the add-in: skipped (-SkipAddin)'
}
else {
    Write-Step '2/7' "Build the add-in ($configuration)"
    & dotnet build $solution -c $configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Stop-Setup 'the add-in did not build.' 'Read the errors above; check the .NET SDK with: dotnet --list-sdks' }
    Write-Ok "staged in addin\dist\$Year\"

    # ── 3. install the add-in ──────────────────────────────────────────────────────────────
    Write-Step '3/7' "Install the add-in for Revit $Year"
    try {
        & (Join-Path $root 'install.ps1') -Years $Year
    }
    catch {
        Stop-Setup ("install.ps1 failed: " + $_.Exception.Message) 'Close Revit and run setup.ps1 again; see INSTALL.md, section Troubleshooting.'
    }
    Write-Ok "files in $env:APPDATA\Autodesk\Revit\Addins\$Year\"
}

# ── 4. server ──────────────────────────────────────────────────────────────────────────────
if ($SkipServer) {
    Write-Step '4/7' 'Set up the server: skipped (-SkipServer)'
    Write-Step '5/7' 'Register in Claude Code: skipped (-SkipServer)'
}
else {
    Write-Step '4/7' 'Set up the server (uv sync)'
    & uv sync --directory $server --quiet
    if ($LASTEXITCODE -ne 0) { Stop-Setup 'uv sync failed.' 'Check your network connection (uv downloads Python 3.13 and the dependencies once), then run setup.ps1 again.' }
    Write-Ok 'server\.venv ready'

    # ── 5. register ────────────────────────────────────────────────────────────────────────
    if ($NoRegister -or -not (Get-Command claude -ErrorAction SilentlyContinue)) {
        Write-Step '5/7' 'Register: configuration block for your MCP client'
        & uv run --directory $server --quiet rift-mcp install claude --scope $Scope --print
        Write-Note 'paste the "mcpServers" block above into your MCP client configuration'
    }
    else {
        Write-Step '5/7' "Register in Claude Code (scope: $Scope)"
        & uv run --directory $server --quiet rift-mcp install claude --scope $Scope
        if ($LASTEXITCODE -ne 0) {
            Stop-Setup 'claude mcp add failed.' "If a server named rift is already registered, remove it first: claude mcp remove rift --scope $Scope; then run setup.ps1 again."
        }
        Write-Ok 'registered as "rift"'
    }
}

# ── 6. verify ──────────────────────────────────────────────────────────────────────────────
Write-Step '6/7' 'Verify'
if (-not $SkipAddin) {
    $manifest = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year\rift.addin"
    if (Test-Path -LiteralPath $manifest) { Write-Ok "add-in manifest present ($manifest)" }
    else { Stop-Setup 'the add-in manifest is missing after installation.' 'Run: pwsh -File install.ps1 -Years 2026 and read its message.' }
}
if (-not $SkipServer) {
    & uv run --directory $server --quiet rift-mcp tools --check *> $null
    if ($LASTEXITCODE -eq 0) { Write-Ok 'server starts and its tool catalogue is consistent' }
    else { Stop-Setup 'the server does not start correctly.' 'Run: uv run --directory server rift-mcp tools --check and read the error.' }
}

# ── 7. next steps ──────────────────────────────────────────────────────────────────────────
Write-Step '7/7' 'Next steps'
Write-Host '    1. Start Revit. The add-in is not code-signed yet: answer "Always Load" once.'
Write-Host '    2. In the NORN tab (RIFT creates it), RIFT panel: hover "server on" and "status", click "status".'
Write-Host '    3. Recommended: install the permission rules so that reads run freely and every write asks first:'
Write-Host '         uv run --directory server rift-mcp permissions --format claude'
Write-Host '       and merge the "allow" and "ask" lists into the "permissions" section of your Claude Code settings.'
Write-Host '    4. Work on a copy of your model whose file name contains SANDBOX: RIFT refuses writes anywhere else.'
Write-Host '    5. In your MCP client, ask: "Use rift: get_context". Then read INSTALL.md, section "First session".'
Write-Host ''
Write-Host 'RIFT is installed.' -ForegroundColor Green
