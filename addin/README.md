# RIFT add-in (Revit side)

Standalone .NET `IExternalApplication` for Autodesk Revit. It exposes the local, authenticated endpoint that the [rift-mcp server](../server/README.md) talks to, runs scripts inside a guarded transaction group, enforces the document policy and ships the audit presets. It depends on nothing but Revit (ADR-011).

## Layout (ADR-021)

| Project | Role | References Revit? |
|---|---|---|
| `src/Rift.Addin` | bootstrap named in the `.addin` manifest: creates the isolated `AssemblyLoadContext`, loads `Rift.Revit`, relays `OnStartup`/`OnShutdown` | RevitAPIUI (reference only) |
| `src/Rift.Core` | protocol, named-pipe transport, HMAC, discovery, jobs, policy, JSON, logging | no |
| `src/Rift.Scripting` | `IScriptEngine`, embedded IronPython 3.4 | no |
| `src/Rift.Revit` | dispatcher, run guard, tools, presets, ribbon, self-tests; one configuration per Revit year | yes |
| `tests/Rift.Tests` | xUnit, no Revit | no |
| `tests/Rift.PipeHost` | development host: the real listener with a fake dispatcher, used by CI with the real CPython client | no |

## Build

```powershell
dotnet build addin/rift.sln -c "Release R26"     # no Revit installation needed (reference packages)
dotnet test  addin/tests/Rift.Tests -c Release
```

Staged output: `addin/dist/2026/rift.addin` + `rift/*` + `rift/SHA256SUMS.txt` (written by `tools/write_sha256sums.ps1` from the `Stage` target, verified by CI with `tools/verify_sha256sums.ps1`). Install with `install.ps1` from the repository root (Revit must be closed); it refuses a staged folder whose files do not match the sums.

In Revit the add-in shows one panel, `RIFT`, in the ribbon tab `NORN` shared by the maintainer's suite of Revit plugins: the first plugin of the suite to load creates the tab, the others reuse it. Product names live in one file, `src/Rift.Core/Brand.cs`, compiled into `Rift.Core` and linked into the bootstrap (ADR-028).

The manifest carries a new `AddInId` since the rename (ADR-028): an add-in installed under the former name keeps its own id, pipe and data folder and can stay side by side until it is removed (`install.ps1 -RemoveLegacy`).

## Status

Milestone M1 (read-only) code complete: listener, authentication, lease, dispatcher, `context`, `compile`, `run` in query mode under the read guard, the six audit presets and `audit.all`, the activity card, the ribbon panel with its Status dialog (activity card switch remembered in `config.json`, log path). Tranche 1 accepted inside Revit 2026.3; tranches 2-6 await their acceptance on a SANDBOX copy (`docs/plans/m1-read-only/00-status.md`). See `docs/DECISIONS.md` (ADR-001, 016, 021, 022, 025) and `prototypes/` for the feasibility checks that preceded M1.
