# Installing RIFT

This guide is for a person installing RIFT on their own Windows machine. If you want an AI agent to do it for you, point it to [`agents/install.md`](agents/install.md); it follows the same steps with extra checks.

RIFT needs nothing but Revit: no pyRevit and no other add-in. In Revit it shows up as the `RIFT` panel of a ribbon tab named `NORN`, which RIFT creates when it is missing.

## 1. Before you start

| You need | Why | How to get it |
|---|---|---|
| Windows 11 | RIFT talks to Revit through a Windows named pipe | — |
| Autodesk Revit 2026 | the only version built today | — |
| PowerShell 7 (`pwsh`) | runs the setup script | `winget install Microsoft.PowerShell` |
| .NET SDK 8.0.400 or later | builds the add-in (Revit does not need to be installed for the build) | `winget install Microsoft.DotNet.SDK.8` |
| uv | installs Python 3.13 and the server dependencies, once | `winget install astral-sh.uv` |
| Git | to clone the repository | `winget install Git.Git` |
| An MCP client | the assistant that uses RIFT; Claude Code is set up automatically, any stdio client works | [Claude Code](https://docs.anthropic.com/claude-code) |

No administrator rights are needed: the add-in is installed for your Windows account only, and the server lives inside the repository folder. After installing PowerShell, .NET or uv, **open a new terminal** so that they are on your PATH.

## 2. Install in one command

Close Revit (save your work first), open PowerShell 7 and run:

```powershell
git clone <repository-url> RIFT
cd RIFT
pwsh -File setup.ps1
```

The script prints each step and stops at the first problem with what to do. It:

1. checks the requirements above and that Revit is closed;
2. builds the add-in (`dotnet build addin/rift.sln -c "Release R26"`);
3. installs it into `%APPDATA%\Autodesk\Revit\Addins\2026\`, after checking every file against `SHA256SUMS.txt`;
4. sets up the server (`uv sync`);
5. registers it in Claude Code as `rift` (`claude mcp add … --scope user`), or prints a configuration block when Claude Code is not installed;
6. checks that the add-in files are in place and that the server starts;
7. tells you what to do next.

Useful options:

| Option | Effect |
|---|---|
| `-CheckOnly` | runs the checks only, changes nothing |
| `-Scope local` or `-Scope project` | registers RIFT for this folder only, or in a shared `.mcp.json`, instead of for every project |
| `-NoRegister` | sets everything up but registers nothing; prints the block for any MCP client |
| `-SkipAddin` | server only (Revit may stay open) |
| `-SkipServer` | add-in only |

Running `setup.ps1` again is safe: it rebuilds, reinstalls and registers again.

## 3. Check that it works

1. **Start Revit.** The add-in is not code-signed yet, so Revit asks once whether to load it: answer **Always Load**.
2. Open the **NORN** tab, find the **RIFT** panel. Hover **server on** and **status** to read what they do, then click **status**: it shows the add-in version, the server state, the pipe and the script engine.
3. With Revit running, check the connection from the repository folder:

   ```powershell
   uv run --directory server rift-mcp doctor
   ```

   It lists the Revit instances it found and which one the server will use.

4. In Claude Code, ask: *"Use rift: get_context"*. The answer names the active document, its class (`sandbox`, `reference`, `unsaved` or `unmarked`) and the write policy that applies. A small card appears in the corner of Revit for each call.

## 4. Recommended settings

**Permission rules.** By default your client decides when to ask before a tool runs. RIFT ships a generated list that lets the 31 read tools run freely and asks before each of the 30 others (scripts, writes, history, job control). Merge it into your Claude Code settings:

```powershell
uv run --directory server rift-mcp permissions --install
```

`--install` adds RIFT's rules to `~/.claude/settings.json` (`--scope project` or `--scope local` for `.claude/settings.json` or `.claude/settings.local.json` in the current folder), moves a RIFT rule that sits in the wrong list, keeps every other rule, and writes a backup of the file first. `setup.ps1 -WithPermissions` does the same during installation. `rift-mcp doctor` reports, under `permissions`, whether scripts and writes ask first.

**Or decide once per session, in Revit.** At the start of each assistant session RIFT asks you in Revit where the assistant may change the model (SANDBOX copies only, or the active project) and how (supervised: Keep/Discard in Revit before a change is kept; full auto). If that is the confirmation you want, let Claude Code stop asking for each RIFT tool:

```powershell
uv run --directory server rift-mcp permissions --install --profile session
```

(`setup.ps1 -WithPermissions -PermissionProfile session` during installation; `--profile ask` to go back.) `doctor` then reports the profile `session`. Turning the question off (`"session_prompt": false` in `config.json`) brings back the SANDBOX-only rule. The rules alone: `rift-mcp permissions --format claude` or [`docs/pamphlet/permissions.claude.json`](docs/pamphlet/permissions.claude.json).

**Work on a SANDBOX copy.** RIFT refuses every write unless the active file name contains a SANDBOX marker as a separate word (by default `SANDBOX`, `JETABLE`, `_INJ_`, `TESTRUN`, `COPIE`, `COPY` or `SCRATCH`; see `sandbox_markers` in the [README, Configuration](README.md#configuration)). Save a copy of your model or template as, for example, `MyTemplate_SANDBOX.rte` and work in that copy. Even there, every write is a dry run unless the assistant asks to keep it, and a kept change is one undo entry named `Agent: <title>`.

**The pause.** The large **server on** button in the RIFT panel pauses RIFT at once: every call is refused with `PAUSED` until you click again. Use it whenever you want the assistant out of your model.

## 5. First session

Ask your assistant, for example:

- "With RIFT, tell me which document is active, its class and its write policy."
- "List the view templates and the views that use each one."
- "Run the RIFT model audit prompt." (In Claude Code, MCP prompts are slash commands such as `/mcp__rift__model_audit`.)
- "Duplicate the view template *Floor Plan 1:100* as *Floor Plan 1:100 - draft*. Dry run first and show me the receipt, then keep it."

To check the whole installation, run `uv run --directory server rift-mcp selftest`. It only reads, unless the active document is a SANDBOX copy and you add `--write` (write tools as dry runs).

## 6. Update

```powershell
git pull
pwsh -File setup.ps1
```

with Revit closed.

## 7. Uninstall

With Revit closed:

```powershell
pwsh -File install.ps1 -Uninstall -Years 2026
claude mcp remove rift
```

Add `-Purge` to the first command to delete `%LOCALAPPDATA%\rift` (logs, run journals, change history) as well. Then delete the repository folder.

## 8. Manual installation

The same steps without `setup.ps1`, from the repository folder, Revit closed:

```powershell
dotnet build addin/rift.sln -c "Release R26"
pwsh -File install.ps1 -Years 2026
uv sync --directory server
uv run --directory server rift-mcp install claude
```

`install claude` runs `claude mcp add --transport stdio --scope user rift -- <server command>` and prints an `mcpServers` block for other clients; add `--print` to see the command without running it. It never edits a client configuration file itself.

**Other MCP clients** (Claude Desktop, Cursor…): run `uv run --directory server rift-mcp install claude --print` and paste the printed `mcpServers` block into the client's configuration.

## 9. Troubleshooting

| Symptom | Cause | What to do |
|---|---|---|
| `setup.ps1` stops at "Revit is running" | the add-in files are in use | save, close Revit, run again; or `-SkipAddin` for the server only |
| `.NET SDK 8.0.400 or later not found` | no SDK, or only a runtime | `winget install Microsoft.DotNet.SDK.8`, new terminal |
| `uv not found` | uv not installed or not on PATH | `winget install astral-sh.uv`, new terminal |
| `claude mcp add failed` | a server named `rift` is already registered | `claude mcp remove rift --scope user`, run again |
| no `NORN` tab in Revit | the add-in did not load | check `%APPDATA%\Autodesk\Revit\Addins\2026\rift.addin` and the log in `%LOCALAPPDATA%\rift\logs\` |
| `NO_TARGET` in the assistant | Revit is not running, or not with the add-in | start Revit, then `rift-mcp doctor` |
| `TARGET_LOST` once after restarting Revit | the server remembered the previous Revit | ask again: the second call finds the new instance |
| `LEASE_HELD` | another assistant session holds RIFT | wait about a minute, or close the other session |
| `PAUSED` | you paused RIFT in Revit | click the button again in the RIFT panel |
| `SESSION_PENDING` | the session window in Revit is not answered yet | answer it (or **status** → `change session`), then ask again |
| `AGENT_CONFLICT` | another assistant add-in is loaded in Revit; RIFT does not keep changes while it is | unload it, or set `"coexist_with_other_agents": true` in `%LOCALAPPDATA%\rift\config.json` and restart Revit |
| `SANDBOX_REQUIRED` | a write on a file that is not a SANDBOX copy | save a SANDBOX copy and make it the active document |
| `REVIT_BUSY` | a Revit dialog is open | close the dialog and ask again |

More codes and their meaning: [README, Troubleshooting](README.md#troubleshooting). Security model and known limitations: [SECURITY.md](SECURITY.md) and [README](README.md#security-status-and-known-limitations).
