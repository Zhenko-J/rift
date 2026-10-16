# Third-party components

| Component | License | Use in RIFT | Copied / adapted? | Where |
|---|---|---|---|---|
| [bimwright/rvt-mcp](https://github.com/bimwright/rvt-mcp) | Apache-2.0 | Design reference: per-session token in a discovery file, named-pipe transport, read-only tool filtering, response size guard, change capture, disciplined MCP prompts, adaptive tool baking | Ideas; any file adapted carries an `Adapted from bimwright/rvt-mcp … This file has been modified` header | `server/`, `addin/`, `skills/` (when applicable) |
| [mcp-servers-for-revit/mcp-server-for-revit-python](https://github.com/mcp-servers-for-revit/mcp-server-for-revit-python) (pyRevit MCP extension) | MIT | Design reference for the tool surface (status, model, views, families, colours, code, documents); tool names appear in the `origin` field of the registry (ADR-027) | Tool names only; no code | `server/src/rift_mcp/registry.py` |
| [pyRevit](https://github.com/pyrevitlabs/pyRevit) | GPL-3.0 | **None at runtime.** The pyRevit 7 "agent runtime" was studied as a **behavioural specification** (clean-room, see ADR-011); its source was not consulted during implementation and no code, skill text or prompt was copied or translated. RIFT does not load, import, reference or require pyRevit. | No | — |
| [IronPython](https://github.com/IronLanguages/ironpython3) 3.4.x (`IronPython`, `IronPython.StdLib`) | Apache-2.0 | Embedded script engine of the add-in (ADR-016); a closed subset of its standard library is redistributed | No (dependency, redistributed binaries) | `addin/` (`Rift.Scripting`) |
| [Roslyn](https://github.com/dotnet/roslyn) 4.8 (`Microsoft.CodeAnalysis.CSharp`, `Microsoft.CodeAnalysis`) | MIT | Compiles C# scripts in memory (ADR-035) | No (dependency, redistributed binaries) | `addin/` (`Rift.Scripting`) |
| [DynamicLanguageRuntime](https://github.com/IronLanguages/dlr) (`Microsoft.Scripting`, `Microsoft.Dynamic`) | Apache-2.0 | Transitive dependency of IronPython | No (redistributed binaries) | `addin/` |
| [Nice3point.Revit.Api.RevitAPI / RevitAPIUI](https://github.com/Nice3point/RevitApi) | MIT (package); the referenced API is Autodesk's | Reference assemblies to build the add-in without Revit installed; **build-time only**, never redistributed | No | `addin/` |
| [Lucide](https://lucide.dev) | ISC | Ribbon icons `gauge`, `flask-conical`, `power`, `pause`: outline path data rendered at start-up (ADR-029 §4) | Path data copied and rewritten with explicit separators; ISC notice at the top of the file | `addin/src/Rift.Revit/Ui/Icons.cs` |
| [Microsoft.CodeAnalysis.BannedApiAnalyzers](https://github.com/dotnet/roslyn-analyzers) | MIT | Build-time analyzer that forbids process spawning, networking and console use in the add-in | No | `addin/` |
| [mcp](https://github.com/modelcontextprotocol/python-sdk) (Python SDK) | MIT | MCP server framework | No (dependency) | `server/` |
| [pydantic](https://github.com/pydantic/pydantic) | MIT | Tool schemas, message models | No (dependency) | `server/` |
| [psutil](https://github.com/giampaolo/psutil) | BSD-3-Clause | Process verification of the Revit instance | No (dependency) | `server/` |
| [parso](https://github.com/davidhalter/parso) | MIT (PSF for grammar files) | Script lint | No (dependency) | `server/` |
| Autodesk Revit API (`RevitAPI.dll`, `RevitAPIUI.dll`) | Autodesk license | Called at runtime inside Revit; `RevitAPI.xml` documentation used locally to build the offline API index (index artefacts are derived data; no Autodesk file is redistributed) | No | `addin/`, `api-index/` |

Planned (v1.x): `Microsoft.CodeAnalysis.CSharp.Scripting` (MIT) for the optional C# script engine.

Trademarks: Autodesk and Revit are trademarks of Autodesk, Inc. Model Context Protocol is a specification published by Anthropic, PBC.
