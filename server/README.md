# rift-mcp (server)

The CPython side of [RIFT](../README.md): an MCP server over stdio that talks to the RIFT Revit add-in through a local, authenticated channel. See the repository README for the overall design and `docs/DECISIONS.md` for the architecture decisions.

```powershell
uv sync --all-extras
uv run rift-mcp --version
uv run pytest -q
```

## Commands

| Command | What it does |
|---|---|
| `rift-mcp serve` | the MCP server over stdio (what the MCP client launches) |
| `rift-mcp install claude [--scope user\|local\|project] [--print]` | registers the server through `claude mcp add`; prints the command and the config block for other MCP clients; never writes a file itself |
| `rift-mcp doctor` | the Revit instances seen (discovery files), their health, and which one would be used — or why none |
| `rift-mcp tools [--format md\|json] [--check\|--write]` | the tool catalogue generated from the registry (README table, `docs/pamphlet/tools.json`) |
| `rift-mcp permissions [--check\|--write]` | the Claude Code permission rules of the shipped tools (`docs/pamphlet/permissions.claude.json`) |
| `rift-mcp schemas [--check\|--write]` | the input schemas of the shipped tools as the server advertises them (`docs/pamphlet/schemas.json`) |
| `rift-mcp selftest [--write] [--keep]` | acceptance checks of M1-M3 against the open Revit: pure reads on any document; the read-guard and timeout checks, and with `--write` dry runs of write tools, on a SANDBOX copy only; `--keep` keeps one change to check its single undo entry |
| `rift-mcp purge [--days N]` | deletes run journals under `%LOCALAPPDATA%\rift\runs` older than N days (all of them by default) |

Every generated file is checked in CI (`--check`) and regenerated with `--write`. Pause and resume are commanded from the RIFT panel of the Revit ribbon (ADR-029), not from the command line.
