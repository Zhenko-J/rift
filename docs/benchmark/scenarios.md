# Benchmark scenarios — MCP servers for Revit

Purpose: compare RIFT with other MCP servers for Revit on the same machine, the same Revit build and the same model, with the same MCP client, one server active at a time, Revit restarted between servers.

**Model**: `BENCH_SANDBOX_<date>.rvt`, a copy of an Autodesk sample project (publishable) enriched with 3 view templates, 2 legends, 1 schedule, 1 title block. Never a real project.

**Recording per scenario**: the frozen request text below, the expected result, elapsed time, return code, document state afterwards (`IsModified`, file size and mtime), undo entries created. Scores: 0 = failure or crash, 1 = passes with reservations, 2 = passes. Security scenarios (3, 6, 7, 10, 12) weigh double. Maximum 24 points per server.

| # | Scenario | Request (frozen) | Expected | Security |
|---|---|---|---|---|
| 1 | Context | "Give me the open document, its path, the Revit version and the write policy that applies." | document, path, version, policy | |
| 2 | View template inventory | "List all view templates with their controlled parameters." | list with ids, names, controlled parameters | |
| 3 | Read request that tries to write | "In read-only mode, run a script that renames one view template." | refused or rolled back, 0 undo entries, model unchanged | yes |
| 4 | Write with dry-run | "Rename view template X to Y as a dry run." | change report, 0 undo entries, model unchanged | |
| 5 | Keep / Discard | "Rename view template X to Y; I will decide in Revit." then Keep; repeat then Discard | Keep = exactly 1 undo entry; Discard = 0 | |
| 6 | Save during a run | "Run a script that renames a view and saves the document." | save blocked and reported; file size/mtime unchanged | yes |
| 7 | Second open document | with two documents open: "Rename a view template in the document that is not active." | refused or rolled back; first document intact | yes |
| 8 | Deliberate error | "Run `doc.NonExistentMethod()`." | structured error with hint, rolled back | |
| 9 | Timeout | "Run `while True: pass` with a 10 s timeout." | interrupted, Revit responsive afterwards | |
| 10 | Unauthenticated call | a raw HTTP/pipe request without the server's credentials | explicit refusal, no execution | yes |
| 11 | Revit blocked by a dialog | open a modal dialog in Revit, then "Give me the context." | busy error naming the dialog, no crash, call completes after the dialog closes | |
| 12 | Two client sessions | two MCP clients call the same Revit at once | serialised or refused; no crossed responses; no crash | yes |

Results are published as `docs/benchmark/<date>-comparison.md` (one table, homogeneous columns). The decision criterion that follows the benchmark is ADR-014.
