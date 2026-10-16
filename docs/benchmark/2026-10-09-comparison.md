# Comparison — MCP servers for Revit, 2026-10-09 (baseline before M1)

> Dated record: written when RIFT was still called zenrvt (renamed on 2026-10-13, ADR-028); names are kept as they were on that day.

Scope: the four servers present on the maintainer's workstation on 2026-10-09 (Revit 2026.3, build 26.3.0.37, Windows 11, Claude Code as the MCP client). Static review of code, binaries and documentation by five agents, then dynamic checks on a throwaway `SANDBOX` copy of the maintainer's reference template, one server at a time. This is **not** the ADR-014 benchmark (the 12 scenarios of `scenarios.md` need a build of zenrvt that does not exist yet); it is the baseline that benchmark will be measured against. Scores are 1–5 per criterion; zenrvt is scored on its current state, except security which is scored on the design.

| Server | Version | Licence | Transport to Revit | Script language | Typed tools |
|---|---|---|---|---|---|
| `mcp-server-for-revit-python` (pyRevit extension) | 0.1.0 | MIT | HTTP loopback, pyRevit Routes, **no authentication** | IronPython 2.7 (`exec`) | 20 |
| `mcp-server-for-revit` (npm + C# plugin) | 1.0.0 | MIT | raw TCP `localhost:8080`, no authentication, no framing | C# (Roslyn) | 26 declared, 23 alive (SQLite binding missing) |
| `rvt-mcp` (bimwright) | 1.1.0 | Apache-2.0 | named pipe per Revit instance, per-session token, discovery files | C# (Roslyn), baked tools | 233 in 23 toolsets, 95 annotated read-only |
| zenrvt | 0.1.0 (M0) | Apache-2.0 | named pipe with ACL, HMAC-signed requests (designed; no listener yet) | IronPython 3.4 (embedded) | 0 (≈15 planned) |

## Scores

| Criterion | pyRevit ext | npm | rvt-mcp | zenrvt |
|---|:---:|:---:|:---:|:---:|
| Functional coverage (today) | 2 | 2 | 4 | 1 |
| Security (zenrvt: design) | 1 | 1 | 4 | 4 |
| Robustness | 2 | 2 | 3 | 2 |
| Extensibility | 4 | 3 | 5 | 4 |
| Maturity | 2 | 2 | 3 | 1 |
| Fit for template work (`.rte`) | 2 | 1 | 3 | 3 |
| **Total / 30** | **13** | **11** | **22** | **15** |

## Dynamic checks (subset of the 12 scenarios)

| # | Scenario | pyRevit ext | npm | rvt-mcp | zenrvt |
|---|---|---|---|---|---|
| 1 | Context (document, path, version) | ok | fails (plugin disabled: "connect to revit client failed") | ok (`send_code_to_revit`, `get_current_target`) | not testable |
| 2 | View-template inventory with controlled parameters | ok via free script (24 templates, 0.01 s) | — | ok via `list_view_templates`; with `includeSettings=false` the counters read 0 instead of null | — |
| 3 | Read-only request that tries to write | no read-only mode; protection is a client-side hook | — | `--read-only` exists, not enabled in the tested configuration | — |
| 4 | Dry-run | none; manual transaction rollback works (1 / 2 / 1 levels, `IsModified` false) | — | partial (purge, workflows, sheet layout); absent on `set_*`, rename, delete | — |
| 8 | Deliberate error `doc.NonExistentMethod()` | structured HTTP 500, traceback, hint (misleading hint) | — | compile error CS1061 with a line number offset by the class template (+12) | — |
| 10 | Unauthenticated call | **fails**: `GET /revit_mcp/status/` answers 200 without any token | n/a (nothing listening) | token required; token stored in clear in the per-instance discovery file (user ACL) | — |
| — | Write + journal | transaction + rollback ok | — | `create_level` → `delete_element` ok, `_changes` and `_history` receipts, `get_change_records` lists both calls | — |
| — | Non-ASCII round trip | ok | — | ok | — |
| — | Latency of a trivial call | ~0.2 s HTTP + 0.01 s script | 5 s (connect timeout) | ~4 ms in-process | — |

Scenarios 9 (infinite loop), 11 (modal dialog) and 12 (two clients) were not played on the user's live Revit session.

## What each server does best (to be converged in zenrvt, ADR-026)

| Strength | Found in | Carry into zenrvt as |
|---|---|---|
| Tool annotations `ReadOnly / Destructive / Idempotent`, generated client allow-list, `--read-only` by annotation | rvt-mcp | risk levels of ADR-012 on every tool, `permissions --format claude` generated |
| `_changes` + `_history` receipts on every mutating call, `record_change`, per-model SQLite history | rvt-mcp | run journal (ADR-005) extended to element-level receipts; `record_change` as a fixed tool |
| `survey_change_impact` before a type change, `revit_change` prompt | rvt-mcp | fixed tool `survey` + MCP prompt (v1.1) |
| Per-instance discovery, `switch_target`, structured `TARGET_*` errors | rvt-mcp | discovery file (ADR-003) + `NO_TARGET / AMBIGUOUS_TARGET / TARGET_CHANGED` (already planned) |
| Response size guard with file spill, `*_truncated` fields | rvt-mcp | `result_ref` + preview (PROTOCOL.md) |
| Baked tools: a script frozen into a compiled command with ribbon slots, after human acceptance | rvt-mcp | learning mode of ADR-013 (repeated scripts → tools), v1.2 |
| In-Revit activity card, History window, ribbon with ON/OFF | rvt-mcp | ADR-023 card (M1), History window and ribbon in the UI work stream (ADR-026) |
| Typed template-centric tools: view templates, filters, shared parameters, schedules, sheets, materials, families | rvt-mcp | typed library wave 1 (ADR-026) |
| C# free code through Roslyn in-process with a failures pre-processor | rvt-mcp, npm | `language: csharp` behind `IScriptEngine` (ADR-016, v1.x) |
| IronPython access, pyRevit-style `revit.query` helpers, hints on errors | pyRevit ext | IronPython 3.4 engine (ADR-016) + error hints in `run` envelope |
| Route ↔ tool symmetry, tests without Revit (`MockMCP`) | pyRevit ext | `fake_revit` + PipeHost tests (already planned) |

## Gaps shared by all three existing servers

- No server-side document policy (SANDBOX by file name, reference files refused): only zenrvt plans it (ADR-009).
- No dry-run by default, no transaction group with unconditional rollback for queries (ADR-006/007).
- No cooperative script timeout inside Revit (pyRevit, npm) or non-preemptive 60 s (rvt-mcp).
- No typed tools for annotation types (text, dimension, line styles and patterns, fill patterns), units, browser organisation, family editing.

## Gaps found in zenrvt itself (to close before any listener ships)

- `__import__` guard absent and full standard library shipped (SECURITY.md says otherwise).
- Timeout invariant not enforced by the engine; `except KeyboardInterrupt` and tuple forms bypass the deadline.
- stdout/stderr unbounded; results above 1 MiB refused by the client without `result_ref`.
- Installed binary older than the timeout fix; `SHA256SUMS.txt` never produced; ADR-002/004/008 still written for HTTP.
