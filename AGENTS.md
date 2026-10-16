# AGENTS.md

Guidance for AI coding agents opening this repository.

- **Asked to install RIFT?** Follow [`agents/install.md`](agents/install.md): check with `pwsh -File setup.ps1 -CheckOnly`, ask the user to close Revit, run `pwsh -File setup.ps1`, verify with `rift-mcp doctor` and `get_context`, report. Never close Revit or touch a real project yourself.
- **About to use RIFT's tools in Revit?** Read [`agents/first-session.md`](agents/first-session.md): `get_context` first, SANDBOX copies only, dry run and receipt before keeping a change.
- **Changing the code?** Read [`CONTRIBUTING.md`](CONTRIBUTING.md). Build with `dotnet build addin/rift.sln -c "Release R26"`, test with `dotnet test addin/tests/Rift.Tests` and `uv run --directory server pytest -q`; the tool table, schemas and permission list are generated (`rift-mcp tools --check`). This repository is public: no names of organisations, clients, projects, templates or workstation paths in any file.
