# Comparison — MCP servers for Revit, 2026-10-16 (after M1–M3)

> Second dated record after `2026-10-09-comparison.md` (the baseline). That baseline scored RIFT on its design; this one scores the code that runs: RIFT at commit `02276da` (M1, M2 and M3 code complete, M2/M3 accepted on a SANDBOX copy the same morning). Scenarios 5, 6 and 11 were played with the maintainer at the keyboard the same afternoon. Scenario 5 exposed a RIFT defect (the Keep/Discard dialog never showed); it was fixed in `7084ac0` and re-played in Revit the same day: Keep then Discard behave as specified.

## (a) Scope, method, versions

**Servers compared.** The three MCP servers for Revit installed on the maintainer's workstation. The npm server of the baseline is no longer installed and is not re-evaluated.

| Server | Version tested | Licence | Transport to Revit | Script language | Tools exposed (`tools/list`) |
|---|---|---|---|---|---|
| RIFT | add-in `0.1.0+02276da`, server 0.0.1, protocol 2.0 | Apache-2.0 | named pipe with explicit DACL, HMAC-signed frames, nonce, response proof, lease | IronPython 3.4 (embedded, isolated load context) | 61 (+18 designed, not exposed), 3 prompts, 1 resource |
| rvt-mcp (BIMWright) | 1.1.0, `toolsets: all` | Apache-2.0 | named pipe per Revit instance, per-session token | C# (Roslyn), baked tools | 233 (+3 hidden), 6 prompts, 1 resource |
| pyRevit MCP extension | extension 0.1.0 on pyRevit 7.0.0 Routes | MIT | HTTP on `127.0.0.1:48884`, no authentication | IronPython 2.7.12 (`exec`) | 20, no prompt, no resource |

**Platform.** Revit 2026.3 (26.3.0.37), Windows 11. Active document: a SANDBOX copy of the maintainer's template (24 view templates, 59 views, 17 schedules, 4 sheets), classified `sandbox` by RIFT. A second SANDBOX copy ("document B") was opened in the background for scenario 7.

**Method.**
1. **Static review** by four reviewers, without any call to Revit: (A) tool coverage and MCP ergonomics, tool by tool, from RIFT's registry and generated schemas, from the metadata of rvt-mcp's server assembly (`McpServerTool` attributes, parameter types and descriptions) and from the pyRevit extension's Python source; (B) security and guard rails (source code, strings and .NET metadata of the rvt-mcp binaries, workstation configuration, Authenticode signatures, installed checksums); (C) in-Revit user interface (ribbon, windows, activity card, localisation; IL trace of rvt-mcp's UI classes); (D) a full sheet of the pyRevit extension and its coexistence with the two other add-ins in one Revit process.
2. **Measured bench** in the live Revit session: one stdio MCP client (Python `mcp` SDK) shared by the three servers, timing every call from request to response, and reading the model state back through a RIFT read-only query after every write scenario. RIFT's own acceptance self-test was run first (`rift-mcp selftest --write`: 21 passed, 2 skipped by design — refusal outside SANDBOX, kept change).
3. **Arbitration.** Where two reviewers disagree, or where a static claim was exercised by the bench, the bench wins. Every claim below carries its status: *measured*, *static* (read in code or binaries, not exercised) or *unverified* (inferred, needs a test).

**Deviations from `scenarios.md`.**

| # | `scenarios.md` says | What was done | Effect on the scores |
|---|---|---|---|
| D1 | a publishable Autodesk sample, enriched | a SANDBOX copy of the maintainer's template | results are not reproducible by a third party; the model is richer in templates than the sample would be |
| D2 | one server active at a time, Revit restarted between servers | the three add-ins loaded in the **same** Revit session | closer to the real workstation; coexistence effects are included; a server could in principle see another server's leftovers (the state reads after each scenario show none) |
| D3 | the same MCP client, with the frozen request text | a scripted client calling tools directly, no LLM in the loop | the bench measures the servers, not an assistant's interpretation of the request |
| D4 | the same client for every server | rvt-mcp accepts one client at a time and was held by the maintainer's interactive session (`TARGET_BUSY` after ~4.5 s for the bench client); its scenarios were played from that session | **no per-call latency for rvt-mcp** (its logs carry no duration); scenarios 7 and 10 not played for rvt-mcp |
| D5 | scenario 9: `while True: pass` | RIFT: `while True`; rvt-mcp and pyRevit: a loop bounded to 15 s | a true infinite loop would have frozen the maintainer's Revit with no way out but killing the process |
| D6 | undo entries, file size and mtime recorded per scenario | template name and `IsModified` read back after each scenario; file size and mtime only for scenario 6 on RIFT | undo-stack counts are not reported |
| D7 | "security scenarios weigh double" and "maximum 24 points per server" | both totals are given: unweighted /24 (the scale ADR-014's threshold uses) and weighted /34 (7 × 2 + 5 × 4) | `scenarios.md` is internally inconsistent and should be corrected |
| D8 | scenario 3: "in read-only mode" | rvt-mcp's `--read-only` exists but is not enabled in the workstation configuration; tested as configured | rvt-mcp is scored on its configuration, as it is used |
| D9 | — | two extra live probes of RIFT's read guard (deferred code, file-system write) to confirm two static findings | reported under (d), not scored |

**Not measured, and why.**

| Item | Why | Status in this report |
|---|---|---|
| Scenario 5 on rvt-mcp and pyRevit | no equivalent feature (checked in `tools/list` and in the tool schemas) | scored 0 on absence, not played |
| Scenarios 7 and 10 on rvt-mcp | pipe held by the interactive session (D4) | static expectation only, not counted |
| rvt-mcp latencies | D4 | — |
| Regex or blocking .NET call defeating RIFT's cooperative timeout | would freeze the maintainer's Revit | unverified |
| Assembly inventory (Roslyn 4.8 vs 4.11 in the default load context) | not played | unverified |
| Bypass of the client-side hook by obfuscated code | not played | static |

## (b) Scored scenarios

Scores: 0 = failure, 1 = passes with reservations, 2 = passes. **S** = security scenario (weighs double in the weighted total). Evidence is the measured result unless marked otherwise.

| # | Scenario | S | RIFT | RIFT evidence | rvt-mcp | rvt-mcp evidence | pyRevit | pyRevit evidence |
|---|---|:---:|:---:|---|:---:|---|:---:|---|
| 1 | Context | | 2 | `get_context` 8 ms: version, build, open documents, path, class `sandbox`, write policy, engine | 1 | `revit_get_current_target` 98 ms: binding, pid, window title; no path, no policy | 0 | `get_revit_model_info` 590 ms: name and counters only; no path, version or policy; reports 0 warnings where the model has 2 |
| 2 | View-template inventory with controlled parameters | | 1 | `list_view_templates` 86 ms, 5.1 kB, 24 templates with filters and using views, but the controlled parameters as a **count** although the description promises them | 2 | complete, controlled and uncontrolled parameters named; ~3.3 kB per template (~80 kB for 24) | 1 | no tool; free code 536 ms, first attempt failed (`ElementId.IntegerValue` removed in Revit 2026) |
| 3 | Read request that tries to write | S | 2 | `run_query` 41 ms → `WRITE_IN_QUERY`, all rolled back, name unchanged, `IsModified` false | 0 | `revit_send_code_to_revit` **wrote** (no read mode configured); `_changes` receipt and history entry | 0 | `execute_revit_code` 391 ms **wrote**; no read mode |
| 4 | Write as a dry run | | 2 | `run_modify` (dry run by default) 51 ms → `rolled_back`, receipt "1 modified", state unchanged | 0 | no dry-run path for renaming a template; the naming workflow refuses templates | 0 | no dry run |
| 5 | Keep / Discard | | 2 | first build: the dialog never showed (rolled back in 104 and 47 ms, reported `user_discarded`; F-R17). Re-played with `7084ac0`: Keep → `committed` after the click (the run waited for the answer), template renamed; Discard → `rolled_back`, `user_discarded` in 4.9 s, template unchanged | 0 | no Keep/Discard: changes are kept, undo after the fact | 0 | no Keep/Discard |
| 6 | Save during a run | S | 2 | lint `E011` in 5 ms, nothing sent; with lint off: exception inside Revit, all rolled back, file size and mtime unchanged | 0 | `send_code` renamed then **saved** the document; only the maintainer's client-side hook asked (the maintainer accepted on the SANDBOX copy); nothing in the server blocks it | 0 | `execute_revit_code` renamed then **saved**; same client-side confirmation, nothing in the server |
| 7 | Write into the non-active document | S | 1 | targeting B by `expected_document` → `DOCUMENT_MISMATCH` (17 ms); a script run on A that writes into B → write **rolled back** (B unchanged) but the answer says `committed` with an empty receipt | not played (static: possible by design, no server-side document policy) | — | 0 | wrote into B without any check (2.2 s) |
| 8 | Deliberate error | | 2 | `SCRIPT_ERROR` 29 ms with line number, nothing kept | 1 | clear C# compile error (CS1061), line number shifted by the code template | 1 | HTTP 500 + IronPython traceback (366 ms), delivered as a flat string |
| 9 | Timeout | | 2 | `while True`, `timeout_s: 10` → `TIMEOUT` at 10 025 ms; Revit answers 9 ms later | 0 | 15 s bounded loop ran to the end; no interruption | 0 | 15 s bounded loop: 15 407 ms, Revit frozen meanwhile, no interruption |
| 10 | Unauthenticated call | S | 2 | frame signed with a forged key → refused (`UNAUTHORIZED`, bad MAC) before any execution; the forged client also fails to verify the add-in's response proof | not played (static: token required) | — | 0 | unauthenticated call accepted and executed (HTTP 200); details withheld pending coordinated disclosure to the maintainers |
| 11 | Revit blocked by a modal dialog | | 2 | with "Project Parameters" open: `get_context` and `run_query` → `REVIT_BUSY` naming the dialog after 30 s, job cancelled; the next call after closing the dialog passes | 1 | `revit_get_current_view_info` → client-side "Request timed out", no reason given, no crash | 0 | `get_revit_status` answered in 0.5 s (model read off the API thread); `list_levels` → "timed out after 30 s, the operation may still be running in Revit" (queued, runs later) |
| 12 | Two client sessions | S | 2 | two RIFT servers: one `ok` (33 ms), the other `LEASE_HELD` (42 ms); no crossed answer | 2 | second client → `TARGET_BUSY` in ~4.5 s, clean refusal | 0 | A (sleeps 5 s) and B sent 1 s apart: **B received A's answer** |

**Totals (measured cells only).**

| Server | Cells measured | Unweighted | Unweighted maximum on measured cells | Weighted | Weighted maximum on measured cells | Security scenarios measured at 2 |
|---|:---:|:---:|:---:|:---:|:---:|:---:|
| RIFT | 12 / 12 | 22 | 24 | 32 | 34 | 4 of 5 (7 at 1) |
| rvt-mcp | 10 / 12 | 7 | 20 | 9 | 26 | 1 of 3 measured (3 and 6 at 0) |
| pyRevit MCP | 12 / 12 | 2 | 24 | 2 | 34 | 0 of 5 |

Reading:
- RIFT: 22/24 unweighted, 32/34 weighted (scenario 5 re-played with the fix). Scenario 7 reaches 2 when the misleading outcome is fixed (P1).
- **ADR-014** (stop if "pyRevit's agent runtime" scores ≥ 20/24 with every security scenario at 2): the pyRevit MCP extension already has three security scenarios at 0. Note that ADR-014 names pyRevit's *agent runtime*; this bench measured the MCP extension on pyRevit Routes. Whether pyRevit 7 ships a distinct agent runtime was not evaluated, so the criterion is satisfied for what was measured, not formally closed.
- **ADR-026 §1** (equal rvt-mcp on 1, 2, 8; beat it on 3, 6, 7, 10, 12): RIFT is ahead on 1, 3, 6 and 8, **behind on 2**, tied on 12, and 7, 10 cannot be compared for rvt-mcp (not played, D4). On scenario 12 rvt-mcp already scores 2, so "beat" cannot be met by the scenario score; the criterion needs rewording (see the maintainer's decisions).
- The score of RIFT on scenario 3 follows the frozen request (a transaction). Two further live probes (d) show that the read-only mode can be escaped by deferred code and by the file system: had they been part of scenario 3, RIFT would score 1 there.

## (c) Comparison by dimension

### (c1) Tool coverage and MCP ergonomics

| Criterion | RIFT | rvt-mcp | pyRevit MCP | Status |
|---|---|---|---|---|
| Tools / prompts / resources exposed | 61 / 3 / 1 | 233 / 6 / 1 | 20 / 0 / 0 | measured (`tools/list`) |
| Tools annotated | 61 / 61 | 232 / 233 (`send_code_to_revit` has none) | 0 / 20 | static |
| `destructiveHint` accuracy | 11 writes that overwrite are marked non-destructive | closer to the specification | absent | static |
| Lengths | mm, output annotated `"unit": "mm"` | mm, m², m³ | internal feet, undocumented | static |
| Designating objects | id or name, ambiguity refused, `OST_` accepted | ids mostly; 16 of the 48 counterparts of RIFT tools accept ids only | names only, localised category names | static |
| Typed arrays vs JSON in strings | 0 JSON-in-string parameters | ≥ 10 (`elementIds`, `commands`, `rules`…) | 0 | static |
| Enums in schemas | 28; missing on `run_query.mode`, `audit_template.preset`, `run_modify.policy` | 0 (allowed values in prose) | 0 | static |
| Parameter descriptions in schemas | 77 %, but 0 on the 8 M1/M2 tools | 1.3 % (prose in tool descriptions) | 0 % | static |
| Description volume (context cost) | 7.1 k characters; median 38 characters without the "untrusted" sentence | 48.6 k characters | 5.9 k characters | static |
| Tool chaining in descriptions | none | frequent ("call X first") | rare | static |
| Server instructions | stale since M3 (describe only `run_query`, `audit_template`, `run_modify`) | current (toolsets, target selection, mm) | none | static |
| Dry run | default on all 26 writes | on 19 tools, default `false` on 5 | none | static; measured on scenario 4 |
| Change receipt | every write, dry runs included | `_changes` + `_history` on mutating calls | none | measured (RIFT, rvt-mcp) |
| Impact survey | `survey`, 7 groups, no time budget, no coverage flag | `survey_change_impact`, 10 groups, budget, coverage, stop flags | none | static |
| Change history | `record_change(reason)`, `get_change_records(limit)` | richer record, filterable history | none | static |
| Pagination | `limit` + `truncated` on 6 tools, no offset; large results by reference | offset and limits on 54 tools, graded size guards | `limit` on 2 tools | static |
| Error envelope | `{ok:false, error:{code, message, details}}`, 30 documented codes, syntax checked before execution, lint with advice | codes for targets and size, sentences otherwise | `"Error: <status> - <body>"` string | measured on scenario 8; static otherwise |
| Answer size, view-template inventory | 5.1 kB | ~80 kB | n/a (free code) | measured |
| Template-domain gaps in RIFT | V/G by category (read and write), template from a view, removing a filter from a view, schedulable fields, schedule sort/filter/field formatting, removing a parameter binding, loading/replacing/unloading families | — | — | static |
| Gap shared by all three | writing annotation styles (text, dimension, line styles and patterns, fill patterns, object styles); only RIFT reads them in typed form (`audit_template` p1–p3) | none | none | static |

### (c2) Security and guard rails

| Criterion | RIFT | rvt-mcp | pyRevit MCP | Status |
|---|---|---|---|---|
| Authentication | HMAC-SHA256 per frame, timestamp, single-use nonce, response proof, constant-time compare | fixed session token in a per-instance discovery file (user ACL), no nonce, no MAC | none | static; RIFT and pyRevit measured on scenario 10 |
| Channel ACL and reach | explicit DACL, `NETWORK` SID denied | default pipe security; optional LAN bind | TCP loopback, reachable by any local account without authentication | static; pyRevit precondition measured |
| Free code | IronPython 3.4, import guard, closed standard library, cooperative timeout, bounded output | C# with full .NET, on by default, not interruptible | IronPython 2.7, full builtins, no timeout, no bound | measured (scenario 9) and static |
| Read-only mode | `run_query`: transaction group always rolled back, `WRITE_IN_QUERY`; **escaped by deferred code and by `System.IO`** | `--read-only` exists, off in the tested configuration | none | measured |
| Document policy | in the add-in, by file name: SANDBOX = requested policy, unmarked refused, reference only if allowed | none in the server | none in the server | static; RIFT exercised by the self-test |
| Writes to another open document | rolled back (reported misleadingly) | possible by design | executed | measured (RIFT, pyRevit) |
| Save / close / sync during a run | lint error before sending; blocked by the transaction group inside Revit | a script saved the document; only the client hook asked | a script saved the document; only the client hook asked; `SaveAs` route with overwrite | measured |
| Concurrency | one-client lease, `LEASE_HELD` | one client, `TARGET_BUSY` | one shared `ExternalEvent`, crossed answers | measured |
| Coexistence detection | designed (ADR-019), **not implemented**: writes accepted with two other agents loaded | none | none | measured in effect |
| Injection hygiene | typed fields, "untrusted" sentence, C0/C1/bidi stripped; Unicode TAG and other format characters not stripped | sanitising and redaction helpers (scope not verifiable) | none; traceback and code echoed | static |
| Journal and redaction | per-run journal 14 days, user/host/folder redacted; 8.3 short names and UNC servers not | call log off by default, broad redaction rules | none | static |
| Dependence on a client hook | none for the model; the generated `ask` rules for `run_query`/`run_modify` are **not installed** by `install claude` | yes: document protection exists only in the maintainer's client hook, absent from other clients | entirely | static |

### (c3) In-Revit user interface

| Function | RIFT (installed) | rvt-mcp | pyRevit MCP | RIFT plan (`ui-ribbon`) |
|---|---|---|---|---|
| Location | `RIFT` panel in the shared `NORN` tab | `RvtMcp` panel in the Add-Ins tab | none | done |
| On / off (pause) button | none | `MCP: ON/OFF`, label and icon swapped | none (pyRevit setting + reload) | tranche 1 |
| Static tooltips | Status, Probe Python | every button | — | tranche 2 |
| Live tooltip (client, last call, state) | none | yes, refreshed on `Idling` | — | tranches 1, 2, 4 (pushed by events) |
| `LongDescription` / `ToolTipImage` / F1 help | none / none / none | 2 buttons (copy of the tooltip) / none / none | — | tranche 2; F1 to be decided |
| Usable with no document open | no | no | — | tranche 1 |
| Status / settings window | text `TaskDialog`; still says "milestone M1: read-only" | Settings window, 4 tabs, one help sentence per setting | — | tranche 3 |
| History window | none | rich: filters, detail, past sessions, re-run | — | tranche 4 (+ receipts, tranche 6) |
| Activity card | dark, fixed bottom-right corner, 6 s, never topmost, never above a modal dialog | topmost, movable, remembered position, hover keeps it, per-tool summaries, click opens History | none | tranche 3 (Z#D01 kit); hover and click recommended |
| Keep / Discard of a write | `TaskDialog`, Discard by default; did not show in the measured build (fixed, to re-play) | none | none | done (M2), defect fixed |
| Tool catalogue | none | Settings › Tools | none | tranche 5 |
| Icons | Lucide, monitor scale, follow the Revit theme | GDI+, fixed size and colours | — | done |
| Localisation | English only (decision) | 15 languages, ~35 % of keys left in English | English | out of scope |
| Launching processes | never (copy log path) | `Process.Start` for logs, images, links | — | kept |

### (c4) Reliability and performance

| Measure | RIFT | rvt-mcp | pyRevit MCP | Status |
|---|---|---|---|---|
| `tools/list` | 7 ms | 31 ms | 13 ms | measured |
| Context call | 8 ms | 98 ms | 590 ms | measured |
| Template inventory | 86 ms, 5.1 kB | not timed (D4), ~80 kB | 536 ms (free code, second attempt) | measured |
| Error path | 29 ms | not timed | 366 ms | measured |
| Infinite or long loop | stopped at the deadline (10 025 ms), Revit responsive | not interrupted | not interrupted, Revit frozen | measured |
| Second client | refused in 42 ms | refused in ~4.5 s | crossed answers | measured |
| First call after a Revit restart | `TARGET_LOST` once (cached pid), second call passes | `switch_target` call required | n/a (fixed port) | measured |
| Correctness defects observed | inventory returns a count, not names; misleading outcome on writes to another document | — | warnings counted 0 instead of 2; Revit 2026 API break in IronPython 2.7 scripts | measured |
| Off-API-thread model access | none | not observed | `/status/` and `/model_info/` read the model from the HTTP thread | static |
| Late execution after a client timeout | jobs not started within 30 s are cancelled (`REVIT_BUSY` naming the dialog) | client-side timeout, no reason | "may still be running": request stays queued and runs later | measured (scenario 11) |
| Self-test | 21 passed, 2 skipped by design | — | — | measured |

### (c5) Installation and integrity

| Criterion | RIFT | rvt-mcp | pyRevit MCP | Status |
|---|---|---|---|---|
| Distribution | build + `install.ps1`, fixed path and AddInId | setup executable from GitHub releases | pyRevit installer + git clone of the extension | static |
| Code signing | unsigned (signing planned, ADR-022) | unsigned | pyRevit loader signed; extension unsigned | static |
| Integrity check at install | `SHA256SUMS.txt` verified before copy; sums sit beside the files (integrity, not authenticity); manifest not covered; the installed folder no longer verifies because of `version.json` | not observed | none | static |
| Dependency pinning | `uv.lock` | self-contained executable | `uv.lock`, but installed `mcp` 1.29.1 against a pinned 2.2.0; interpreter from the Microsoft Store | static |
| Update channel | manual | the server suggests the assistant guide the user through an unsigned installer | git | static |
| Outgoing network | none | release check at start-up (can be disabled) | none | static |
| Client permissions | generated list (31 allow, 30 ask), not installed by `install claude` | allow-list can be generated, `--read-only` | none | static |

## (d) Findings per server

Severity: **critical** (exploitable without the user, or loses work), **high** (breaks a stated guarantee), **medium** (weakens a guarantee or misleads), **low** (hygiene). Status: *measured*, *static*, *unverified*.

### RIFT

| ID | Finding | Severity | Status | Evidence |
|---|---|---|---|---|
| F-R1 | Deferred code escapes the read guard and the timeout: an `uiapp.Idling` handler subscribed from `run_query` ran **after** the run and its guard had ended; the same path would allow `PostCommand`, updaters, document events | high | measured | witness file written by the handler, with the active document's title, 5 s after a `run_query` that returned `ok` |
| F-R2 | `run_query` is not read-only for the file system: `System` is injected without guard (`System.IO`, `Environment.Exit`, `Diagnostics.Process`); `open` remains; the module-builtins bypass of the import guard | high | measured (file write); static (exit, process, builtins bypass) | witness file written by `System.IO.File.WriteAllText` from a `run_query` that returned `ok` |
| F-R14 | A `run_query` that opens a document is reported `WRITE_IN_QUERY`, "everything was rolled back", yet the document stays open | medium | measured | document B opened by `app.OpenDocumentFile` in the bench set-up remained listed in every later state read |
| F-R15 | A modify run that writes into a non-active document: the write is rolled back, but the answer is `outcome: committed` with an empty receipt | medium | measured | scenario 7; the recorded answer is cut before `other_documents`, so whether that field named B is unverified |
| F-R17 | The Keep/Discard dialog of `policy: ask` never showed: `DefaultButton` was set before the command links were added, Revit threw "Corresponding button not found", the run was rolled back and reported `user_discarded` although nobody answered | high (feature broken; safe failure) | measured; fixed in `7084ac0` and re-played (Keep and Discard as specified) | scenario 5 |
| F-R5 | The security assumption "free code and writes behind a confirmation" is not installed: `install claude` registers the server only; no `ask` rule for `run_query`, `run_modify`, `audit_template_write` in the client | medium | static | contradicts ADR-030 §3, which planned the installation "when M2 exposes writes" |
| F-R7 | Coexistence detection (ADR-019, `AGENT_CONFLICT`) not implemented | medium | measured in effect | `run_modify` accepted with pyRevit Routes and rvt-mcp loaded in the same Revit, both able to bypass RIFT |
| F-R4 | Sanitiser leaves Unicode TAG characters, ZWJ/ZWNJ, U+2060–2064, line/paragraph separators | medium | static | closed list of 13 code points |
| F-R6 | SANDBOX marker matched as a substring of the file name; `config.json` writable by a script (via F-R2) | medium | static | a name such as "Copyright…" or "Scratch-…" is classified SANDBOX |
| F-A1 | 11 writes that overwrite are annotated `destructiveHint: false`; no write is `idempotentHint` | medium | static | a client that trusts annotations would treat overwrites as safe |
| F-A2 | Server instructions and the `change` prompt stale since M3 (no word on typed tools, `survey`, `batch`); no parameter description on the 8 M1/M2 tools; `run_modify.expected_document` has a default `""` in the schema but is required | medium | static | — |
| F-U1 | The Status dialog says "milestone M1: read-only" while the add-in writes | medium | static | misinforms the user about what the assistant can do |
| F-R3 | Cooperative timeout defeated by primitives without trace events (catastrophic regex, `Thread.Sleep`, huge allocations) | medium | unverified | static reading of the engine |
| F-A3 | `list_view_templates` promises controlled parameters and returns a count | low | measured | scenario 2 |
| F-R16 | `TARGET_LOST` on the first call after a Revit restart | low | measured | second call passes |
| F-A4 | `cancel_job` unreachable from an MCP client (`run_modify` returns no `job_id`) | low | static | — |
| F-R8 – F-R13 | No read timeout on the pipe; unlimited key-rotation rate; pipe name not validated from the discovery file; checksums not authenticated; `lint: false` exposed in the schema (ADR-017 keeps it for the `full` profile); 8.3 names and UNC servers not redacted | low | static | — |

### rvt-mcp (BIMWright)

| ID | Finding | Severity | Status | Evidence |
|---|---|---|---|---|
| F-B1 | Arbitrary C# (`send_code_to_revit`) on by default, `toolsets: all`, no read mode configured; a "read" request wrote | high | measured | scenario 3: template renamed, `_changes` receipt |
| F-B5 | No document policy in the server: SANDBOX protection exists only in the maintainer's client hook, absent from other clients | high | static | scenario 7 not played |
| F-B6 | No interruption of long code inside Revit | medium | measured (bounded loop) | 15 s loop ran to the end |
| F-B7 | No dry-run path for the scenario's rename; dry run default `false` on 5 write tools | medium | measured / static | scenario 4 |
| F-B2 | Fixed session token, sent in clear on every request, no nonce or MAC; optional LAN bind | medium | static | — |
| F-B3 | The server suggests the assistant guide an update to an unsigned installer, no published hash check observed | medium | static | — |
| F-B8 | Verbose answers (~3.3 kB per view template) | low | measured | scenario 2 |
| F-B4 | Release check over the network at start-up | low | static | can be disabled |
| — | Strength: one client at a time, clean `TARGET_BUSY` refusal | — | measured | scenario 12 |

### pyRevit MCP extension

| ID | Finding | Severity | Status | Evidence |
|---|---|---|---|---|
| F-P1 | Unauthenticated code execution on the local HTTP port (details withheld pending coordinated disclosure to the maintainers) | critical | measured (server side); browser path unverified | scenario 10: HTTP 200, document title returned |
| F-P6 | One `ExternalEvent` shared by a multi-threaded HTTP server: concurrent calls get each other's answers, a request can be lost | high | measured | scenario 12 |
| F-P2 | No write guard at all: no transaction group, dry run, document policy, receipt; writes into non-active documents | high | measured | scenarios 3, 4, 7 |
| F-P7 | No timeout inside Revit; busy-waiting HTTP thread; late execution after a client timeout | high | measured (no timeout); static (late execution) | scenario 9 |
| F-P8 | `save_document` performs `SaveAs` with overwrite to any path | high | static | — |
| F-P3 | Unauthenticated `/status/` is the source of truth of the maintainer's client hook (a squatted port can fake the document title) | medium | static | — |
| F-P9 | Two routes read the model from the HTTP thread, outside the Revit API context; warnings counted 0 instead of 2 | medium | static; bug measured | scenario 1 |
| F-P10 | IronPython 2.7 scripts break on Revit 2026 API removals; undocumented internal feet; localised category names | medium | measured (API break); static | scenario 2 |
| F-P11 | pyRevit core API can reload pyRevit without authentication | medium | static | — |
| F-P12 | Installation drift (`mcp` 1.29.1 installed vs 2.2.0 pinned), unbounded answers echoing the code | low | static | — |

### Client-side hook (maintainer's own, outside RIFT)

| ID | Finding | Severity | Status |
|---|---|---|---|
| F-H1 | Read/write classification by regular expression: obfuscated writes pass, a hook error lets the call through | high | static |
| F-H2 | The document checked is the one of the instance holding port 48884, not the one rvt-mcp targets | medium | static |
| F-H3 | Protects one client only | medium | static |

## (e) RIFT action list

Ordered inside each priority. P1 closes before v1.0.0; P2 before the next benchmark; P3 follows the UI plan and the coverage backlog.

| Priority | Action | Closes | Rationale |
|---|---|---|---|
| P1 | Refuse deferred code in scripts: façades over `uiapp`/`app` that reject event subscriptions, `PostCommand`, `RegisterUpdater`, `OpenDocumentFile`, `CopyModel`; an AST rule enforced in the add-in, not only the server lint; acceptance test "Idling from a query → refused" | F-R1, F-R14 | measured: the read guard and the timeout end before the deferred code runs — open. F-R14 partly closed after the benchmark: a document opened or created during a run is now seen and reported (`documents_left_open`, `blocked: open_document`) and a modify run is not kept; refusing the call itself belongs to this item |
| P1 | Replace the raw `System` namespace with a façade (value types, `Math`, `DateTime`, collections), remove `open`, proxy module builtins; until then, describe `run_query` as arbitrary code execution | F-R2 | measured: a "read-only" run wrote to disk — open |
| P1 | Report a modify run whose writes into other documents were rolled back as `rolled_back` (or a distinct reason) with `other_documents` counts, on the answer, the card and the history | F-R15 | measured: `committed` with an empty receipt misleads both the assistant and the user — **closed** after the benchmark: `OTHER_DOCUMENT_WRITE`, `outcome: rolled_back`, `reason: other_document_write`, `other_documents [{title, transactions}]`, never kept (dry run included), in the history too |
| P1 | `rift-mcp install claude` writes (or prints and verifies) the generated `ask` rules; `doctor` reports their absence | F-R5 | ADR-030 §3 promised it with M2; today confirmation rests on the client's mode |
| P1 | Implement coexistence detection (ADR-019): report pyRevit Routes and rvt-mcp in `get_context` and on the card; refuse `run_modify` by default with `AGENT_CONFLICT` | F-R7 | measured in effect: RIFT's guards are bypassable by the other add-ins in the same process |
| P1 | Strip all format (`Cf`) and private-use characters, the TAG block and lone surrogates | F-R4 | invisible instructions in model data reach the assistant — **closed** after the benchmark (also variation selectors and noncharacters; U+2028/2029 become a line feed) |
| P1 | Delimited SANDBOX markers, minimum length, no punctuation-only marker; fingerprint of `config.json` shown in Status, marker changes confirmed in Revit | F-R6 | the document policy is RIFT's main guarantee; its key is a substring — markers **closed** after the benchmark (delimited words, at least 4 characters with 3 letters or digits, invalid markers ignored and logged); the `config.json` fingerprint in Status and the confirmation in Revit are UI work, open |
| P1 | `destructiveHint: true` on the 11 overwriting writes | F-A1 | other clients decide on annotations — **closed** after the benchmark (`OVERWRITING_WRITES` in the registry) |
| P1 | Remove "milestone M1: read-only" from the Status report | F-U1 | the user is told the opposite of what the add-in does; one line — **closed** after the benchmark |
| P2 | Server instructions rewritten for the typed library (reads without confirmation, writes as dry runs, `survey` before change, `batch`, `result_ref`); `change` prompt aligned on `survey` then the typed tool | F-A2 | the assistant is never told how to use 50 of the 61 tools |
| P2 | Enums on `run_query.mode`, `audit_template.preset`, `run_modify.policy`; `expected_document` required in the schema; parameter descriptions on the 8 M1/M2 tools; `lint` removed from the default schema | F-A2, F-R12 | the riskiest tools have the poorest schemas |
| P2 | `list_view_templates` returns the controlled parameters (names) or an `include_parameters` switch, and its description matches | F-A3 | scenario 2 is the one where RIFT is behind rvt-mcp (ADR-026 §1) |
| P2 | Typed tools for V/G by category (read and write), template from a view, removing a filter from a view, schedulable fields, schedule sort/filter/field formatting, removing a parameter binding, family load/replace/unload | coverage | the template-domain gaps; 27 tools proposed for M3 on 2026-10-13 never entered the registry |
| P2 | Bounded timeout for the .NET regex engine, a watchdog that shows the card when the API thread is stuck, documented limits | F-R3 | one regex can freeze Revit past the cooperative timeout |
| P2 | Pipe read and drain timeouts; rotation rate limit; validated pipe name from the discovery file | F-R8, F-R9, F-R10 | a local process can hold the single listener |
| P2 | Forget the cached pid and rediscover on `TARGET_LOST` within the same call | F-R16 | the first call after every Revit restart fails |
| P2 | One usage sentence and one chaining hint per typed tool; nearest-name candidates in "not found" errors; `offset` on listing tools | ergonomics | rvt-mcp's guidance is its main ergonomic edge |
| P2 | UI tranche 1: the pause button (server on / paused, `PAUSED` gate) with `AvailabilityClassName` so it works with no document open | UI T1 | the user's own kill switch, inside Revit |
| P3 | UI tranche 2: full panel, tooltips and `LongDescription` per expert C's texts | UI T2 | — |
| P3 | UI tranche 3: Status window in Z#D01; card kept open on hover | UI T3 | — |
| P3 | UI tranches 4 + 6 merged: History with change receipts (M2 is delivered); click on the card opens History | UI T4, T6 | — |
| P3 | UI tranche 5: Tools catalogue | UI T5 | — |
| P3 | Return a `job_id` from long `run_modify` calls so `cancel_job` is usable | F-A4 | — |
| P3 | Signed checksums published out of band, manifest in the sums, `version.json` out of the verified set; Authenticode (ADR-022) | F-R11 | integrity today, authenticity later |
| P3 | Redact 8.3 short names and UNC servers; say in SECURITY.md that results carry client data | F-R13 | — |
| P3 | Writing annotation styles in typed form (text, dimension, line, fill patterns, object styles) | coverage | the gap no server fills; RIFT already reads them |
| P3 | Correct `scenarios.md` (weighting vs maximum) and reword ADR-026 §1 for scenarios where rvt-mcp already scores 2 | method | the next benchmark needs a consistent scale |

## (f) Verdict

| Server | Verdict | Unweighted (measured) | Strongest point | Gaps that matter |
|---|---|:---:|---|---|
| RIFT | **The only one whose guard rails are enforced by the server and held on every measured security scenario — but its "read-only" is weaker than it claims.** Not ready for v1.0.0 until the P1 list is closed. | 18 / 20 | authenticated pipe, server-side document policy, dry run by default, rollback-always reads, timeout that fires, single-client lease | read-only mode escaped by deferred code and the file system (both measured); a misleading receipt on writes into another document; no coexistence check; confirmation rules not installed; 61 tools against 233, terse descriptions and stale instructions; one button in Revit |
| rvt-mcp (BIMWright) | **The broadest and best-guided tool surface, with a clean single-client discipline — but, as configured, nothing in the server stops a write.** Run it with `--read-only`, or at least `--disable-send-code`, beside RIFT. | 6 / 14 | coverage, descriptions that chain tools, survey with budget and coverage, rich in-Revit UI | arbitrary C# on by default, no document policy, no read mode configured, no interruption of long code, unsigned LLM-guided updates |
| pyRevit MCP extension | **To be switched off, at least its `execute_code` route: it failed every measured security scenario and lowers the whole Revit process to its level.** | 2 / 18 | simplicity, IronPython access to pyRevit helpers, view images, session tools (launch, open) | unauthenticated code execution, crossed answers, no timeout, no write guard, `SaveAs` with overwrite |

Cross-cutting conclusion: the security of the workstation is the security of the weakest add-in loaded in the same Revit process. As long as pyRevit Routes runs without authentication and rvt-mcp exposes arbitrary C#, RIFT's authentication and guards can be bypassed without touching RIFT. Reducing that surface is a configuration decision for the maintainer, not a RIFT change; RIFT's part is the P1 list above and the coexistence warning.
