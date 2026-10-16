# Installing RIFT — procedure for an AI agent

You are an AI agent (Claude Code, or another coding agent with a shell) asked to install RIFT on the user's Windows machine. This file tells you the safest way to do it. The human version is [`../INSTALL.md`](../INSTALL.md); read it if the user asks a question it answers.

**The short version:** check, ask the user to close Revit, run `pwsh -File setup.ps1`, report. Everything below is about doing exactly that without surprising the user.

## Ground rules

1. **Revit is the user's session.** Never close, kill or restart Revit yourself, never save or close a document in it. The add-in can only be installed while Revit is closed: when it is open, **ask the user** to save their work and close it, then wait for their answer.
2. **Use the repository's own tools.** `setup.ps1` wraps `dotnet build`, `install.ps1`, `uv sync` and `rift-mcp install claude`. Do not copy files into `%APPDATA%\Autodesk\Revit\Addins\` by hand, do not skip the checksum verification (`-SkipHashCheck`) unless the user asks for it, do not edit Revit's or the MCP client's configuration files directly.
3. **Ask before changing the user's environment outside the repository:** installing software (`winget …`), registering the server in their MCP client at a scope other than the default, merging permission rules into their settings. Proposing the command is fine; running it needs a yes.
4. **No administrator rights are needed.** If something asks for elevation, stop and report: it is not part of RIFT's installation.
5. **Never touch a real project to test.** Writes through RIFT only happen on a file whose name contains a SANDBOX marker. Do not run `rift-mcp selftest --write` unless the user confirms the active document is a SANDBOX copy.
6. **Report what happened, not what should have happened.** Quote the failing step and its message.

## Procedure

Run every command from the repository root, in PowerShell 7 (`pwsh`).

### 1. Check

```powershell
pwsh -File setup.ps1 -CheckOnly
```

Expected: `All checks passed (-CheckOnly: nothing was changed).` with exit code 0. Otherwise each problem is printed with a `fix:` line:

| Problem printed | What you do |
|---|---|
| `Revit is running` | ask the user to save and close Revit; wait; run the check again |
| `.NET SDK 8.0.400 or later not found` | propose `winget install Microsoft.DotNet.SDK.8`; after a yes, run it, then use a **new** shell (PATH) |
| `uv not found` | propose `winget install astral-sh.uv`; same as above |
| `PowerShell … found; PowerShell 7 is needed` | propose `winget install Microsoft.PowerShell`; rerun with `pwsh` |
| `Revit 2026 is not installed` | stop: RIFT is built for Revit 2026 only today; tell the user |
| `Claude Code CLI (claude) not found` (note, not a problem) | fine: step 5 will print a block to paste into their MCP client |

If the user wants the server only while they keep working in Revit, use `-SkipAddin` and tell them the add-in still has to be installed later with Revit closed.

### 2. Install

```powershell
pwsh -File setup.ps1
```

Default registration is `--scope user` (every project of this user). If the user wants RIFT for this folder only, add `-Scope local`; for a shared `.mcp.json`, `-Scope project`. If they use another MCP client, add `-NoRegister` and give them the printed `mcpServers` block.

Expected end: `RIFT is installed.`, exit code 0. Known failures:

| Message | Meaning | What you do |
|---|---|---|
| `the add-in did not build` | compiler error above it | report the first error line; check `dotnet --list-sdks` |
| `install.ps1 failed: Revit is running` | Revit was reopened meanwhile | ask the user to close it; rerun |
| `SHA256SUMS.txt does not match` | the staged build is incomplete or altered | rerun `setup.ps1` (it rebuilds); never pass `-SkipHashCheck` on your own |
| `uv sync failed` | usually no network | report; rerun when online |
| `claude mcp add failed` | a server named `rift` already exists at that scope | tell the user; after a yes, `claude mcp remove rift --scope <scope>` and rerun |

### 3. Verify with Revit running

Ask the user to start Revit and answer **Always Load** to the add-in prompt (the add-in is not code-signed yet). Then:

```powershell
uv run --directory server rift-mcp doctor
```

Expected: one instance with `"problems": []` and a `target` with a pid. If the MCP client was already open during the installation, it may need a restart to see the new `rift` server.

Then, through the MCP tools (`mcp__rift__get_context` in Claude Code), call `get_context`. Expected: `"ok": true`, the Revit version, the active document with its `class`, and `policy.write_policy`. On `TARGET_LOST` just after Revit started, call once more.

### 4. Recommend the permission rules

`rift-mcp doctor` reports under `permissions` whether `run_query`, `run_modify` and `batch` ask first (`writes_ask`). If they do not, show the user the rules (`uv run --directory server rift-mcp permissions --format claude`: 31 read tools in `allow`; scripts, writes, history and job control in `ask`) and, **after a yes**, merge them:

```powershell
uv run --directory server rift-mcp permissions --install
```

It writes a backup of the settings file first and keeps every other rule. Use `--scope project` or `--scope local` if the user wants them for the current folder only. If the user's settings are generated by another tool, tell them instead of merging.

### 5. Report

Give the user a short report:

- what was installed: add-in version (from `doctor` or `%APPDATA%\Autodesk\Revit\Addins\2026\rift\version.json`), server registration scope;
- what you checked: `doctor`, `get_context`;
- what is left to them: permission rules (if not merged), working on a SANDBOX copy for any write, the **server on** button in the RIFT panel that pauses RIFT at once;
- anything that failed, with the step and the message.

## Facts you can rely on

- RIFT needs nothing but Revit. It creates the `NORN` ribbon tab when missing and adds its `RIFT` panel; it needs no other add-in, and coexists with pyRevit and other MCP add-ins.
- The add-in lives in `%APPDATA%\Autodesk\Revit\Addins\2026\rift\` with the manifest `rift.addin`; logs, run journals and history in `%LOCALAPPDATA%\rift\`.
- The server never listens on the network: the MCP client starts it over stdio, and it talks to Revit through a signed named pipe restricted to the user's account.
- Uninstall: Revit closed, `pwsh -File install.ps1 -Uninstall -Years 2026` (add `-Purge` to delete `%LOCALAPPDATA%\rift`), then `claude mcp remove rift`.
- Using RIFT once installed: [`first-session.md`](first-session.md).
