# RIFT wire protocol — version 2.0 (draft)

The protocol links the CPython MCP server to the .NET add-in running inside Revit. It is versioned independently of the software (`"v": "2.0"` in every message). A different major version is refused with `PROTOCOL_MISMATCH`; minor versions are additive.

Version 2.0 (2026-10-13, ADR-028) changes nothing but names: the frame magic and the HMAC prefix become `RIFT/1` (version 1.0 used the former product name), the pipe becomes `rift-revit-<pid>` and the data folder `%LOCALAPPDATA%\rift`. A 1.x client looks in another folder for another pipe; a frame header with the old magic is malformed and the connection is closed without a response; a body with `"v": "1.x"` is refused with `PROTOCOL_MISMATCH`. The framing itself is unchanged (hence `/1`).

## Transport (ADR-001)

A **named pipe** `\\.\pipe\rift-revit-<pid>` (or a random-suffixed name published in the discovery file when the plain name is taken), one server instance, byte mode, with an ACL that allows the current user and denies network logons. One connection carries one request and one response; the server disconnects afterwards and waits again on the same instance. A second client while a request is in flight — or any client in the instant between two connections — gets `ERROR_PIPE_BUSY` (surfaced by the C runtime as `EINVAL` when the pipe is opened like a file); the client retries with `WaitNamedPipe` for up to 2 s and then reports `TARGET_BUSY`.

Before its first request a client verifies that the `pid` of the discovery file is alive and that its creation time matches `process_start_utc` (±2 s); a pid reused by another program is not a target (`TARGET_CHANGED`), and records failing the check are ignored when choosing a target (`NO_TARGET`, or `AMBIGUOUS_TARGET` when several live instances remain and no `pid` was given). On every connection it also checks that the process serving the pipe (`GetNamedPipeServerProcessId`) is that pid; otherwise `TARGET_CHANGED`.

## Framing

One ASCII header line terminated by `\n` (≤ 256 bytes), then exactly `len` bytes of UTF-8 JSON (≤ 1 048 576; checked before any allocation).

```
request  : RIFT/1 ts=<unix seconds> nonce=<32 hex> mac=<64 hex> len=<bytes>\n<body>
response : RIFT/1 proof=<64 hex> len=<bytes>\n<body>
health   : RIFT/1 health len=0\n          →  RIFT/1 proof=- len=N\n{"v":"2.0","pid":1234}
```

A malformed or oversized header closes the connection without a response. `health` is the only unsigned message and carries nothing but the protocol version and the pid.

## Authentication (ADR-002)

`mac = HMAC-SHA256(secret, "RIFT/1|" + ts + "|" + nonce + "|" + sha256_hex(body))`. Window ±120 s; a nonce is valid once within 5 minutes and is consumed only by a frame whose `mac` is valid (a forger cannot burn the nonces of the legitimate client). Failure → status `error`, body `{"error":{"code":"UNAUTHORIZED","message":"BadMac|BadTimestamp|ReplayedNonce"}}`, signed like any response, then disconnection; the operation is never executed. Ten consecutive failures (of any kind; a success resets the count) rotate the secret: the key file is rewritten, `generation` is incremented in the discovery file and in `target.generation`. A client whose request is refused — or whose response proof does not verify, since the refusal is signed with the new secret — re-reads the discovery and key files once and retries with a fresh nonce if the secret changed; otherwise it reports `UNAUTHORIZED` (or `TARGET_CHANGED` for a failed proof).

`proof = HMAC-SHA256(secret, mac + "|" + sha256_hex(response_body))`. The server refuses a response without a valid proof. Both sides compare digests in constant time. The secret (32 random bytes) lives in `revit-<pid>.key` next to the discovery file and never travels.

Test vectors for `mac` and `proof` are shared in `shared/schemas/test-vectors/hmac.json` and checked by both the xUnit and the pytest suites.

## Lease (ADR-004)

`op: "lease"` with `{"client_id": "<name>-<pid>-<start>", "client": {"name": "...", "version": "..."}}` → `{"lease_until_utc": "...", "holder": {"client_id", "name", "version", "since_utc"}}` (60 s) or status `refused`, error `LEASE_HELD`, `result: {"holder": {...}}`. Every other signed request is admitted by the lease through `client.session_id`, which carries the same `client_id`: the holder's lease is renewed; any other client gets `LEASE_HELD` with the holder while the lease is live and the operation is not executed; when nobody holds a live lease the request takes it (labelled with `client.name`/`client.version`), so a client idle past its lease simply takes it back. The client re-leases on its own after 50 s without an answered request. The activity card and the Status button show the holder and since when. Pause revokes the lease.

## Operations (`op` in the body; all signed)

| op | Purpose | API thread |
|---|---|---|
| `health` | protocol version and pid (unsigned, see framing) | no |
| `lease` | acquire or renew the single-client lease | no |
| `context` | versions, document, open documents, active view, selection, levels, policy, coexistence, server state | yes |
| `compile` | compile a script with the engine, return `ok` or `{line, column, message}` | no |
| `run` | execute a script (`mode: query|modify`, `language`, `dry_run`, `policy`, `expected_document`, `document_path`, `title`, `inputs`, `timeout_s`, `async`) | yes |
| `job` | state, progress and result of a running or finished job | no |
| `tool` | a fixed tool or a preset (`inspect`, `show`, `capture`, `purge`, `document.*`, `audit.*`, `batch`) | depends |
| `lookup` | API reflection and offline index | no |
| `diagnose` | extracted facts (journal, add-ins, listener) | no |

`op: "tool"` with `args: {"name": "audit.p1", "timeout_s": 300}` (M1: `audit.p1` … `audit.p6`, `audit.all`) runs an embedded audit preset (ADR-024) through exactly the path of `run` in `mode: query` — timeout invariant, compile, journal (`op` recorded as `tool:audit.pN`), read guard, rollback — and answers `{"tool": "audit.p1", "run": <run result without result>, "documents": {"01_vues_lignes_cartouches": {...}}}`. `audit.all` runs every preset of the catalogue in order (p1-p5, then the merge p6, which reads the earlier documents from the shared collector) inside **one job**: `timeout_s` bounds each script, progress spans the whole job (`<preset>: <message>`), the answer carries `runs: {"audit.p1": <run>, …}` and `timing: {total_ms, presets: {name: ms}}` instead of `run`, and `documents` holds the six documents, `00_TOUT` included; the first failing preset stops the job with its own error, whose details carry `preset` and the `runs` already done. `audit.p6` on its own sees an empty collector (its document then only has the legends on sheets). Documents keep the number text the preset wrote (`594.0` stays `594.0`); every string, property names included, goes through the sanitiser (ADR-020). When their total exceeds 700 KiB they are journaled and the answer carries `documents_ref: {name: "runs/<folder>/<name>.json"}`, `documents_bytes` and `documents_redacted: true` instead of `documents` (the files are redacted like every journal file, ADR-005). Unknown tool name → `refused / BAD_REQUEST` listing the available ones; no active document → `refused / NO_DOCUMENT` before the guard is armed; a preset that writes → `WRITE_IN_QUERY` like `run`.

## Envelopes

Request body:
```json
{"v":"2.0","id":"<request id>","op":"run","timeout_s":60,
 "client":{"session_id":"<lease client_id>","name":"rift-mcp","version":"...","tool":"<MCP tool name, optional>"},
 "expected_document":{"path":"<exact path>","title":"<title>"},
 "args":{...}}
```

Response body:
```json
{"v":"2.0","id":"<request id>","status":"ok|error|refused|accepted|running",
 "target":{"pid":1234,"generation":3,"document":{"title":"...","path":"..."}},
 "result":{...},
 "error":{"code":"...","type":"...","message":"...","traceback":"..."},
 "elapsed_ms":12}
```

Run result (inside `result` for `op: run`): `run_id`, `mode`, `language`, `title`, `outcome` (`ok | committed | rolled_back | refused | error | timeout`), `reason`, `policy_applied`, `dry_run`, `result` (or `result_ref` + `preview` above 700 KiB), `stdout`, `stderr`, `changes {added, modified, deleted, transactions, by_category, samples}`, `other_documents`, `blocked`, `dialogs`, `failures`, `sentinel`, `error`, `engine`, `timing {queued_ms, run_ms}`, `job_id`, `run_log`.

### Typed tools (M3, ADR-032)

`op: "tool"` with `args: {"name": "<typed tool>", "inputs": {...}, "timeout_s": 120}` runs the embedded script of that tool. A read tool answers `{"tool", "writes": false, "result", "run"}` through the query path. A write tool also takes `dry_run` (default true), `policy` and `allow_reference` in `args`, requires `expected_document`, and answers `{"tool", "writes": true, "result", "run"}` where `run` is a modify run result (outcome, reason, `changes`, `history_id`, policy). `name: "batch"` with `inputs: {"items": [{"tool", "inputs"}]}` (at most 50, typed write tools only) runs them in order in one modify run and answers `result: {"items": [{"tool", "result"}]}`. An argument that does not resolve is a `SCRIPT_ERROR` whose details carry the tool's sentence (`ValueError`); an unknown tool name is refused with the list of every tool served.

### `compile`, `run` and `job` in M1 (implemented in tranche 3)

- `compile` — `args {script, language: "python"}` → status `ok`, `result {ok, language, sha256, timeout_available}`; a syntax error is not an error status: `result {ok: false, line, column, message}`, so the server returns the line and never sends `run`. Runs on the pipe thread.
- `run` — `mode: "query"` (M1) or `"modify"` (M2, below); any other mode is refused with `BAD_REQUEST`; `run_query` answers `READ_ONLY_MODE` itself for any mode but query and `language: "python"`. `args {script, mode, language, title, inputs {…}, timeout_s}`: `args.timeout_s` is the script's cooperative deadline (1–3600 s, default 60), the envelope `timeout_s` is how long the pipe waits before answering `accepted {job_id}`. `expected_document {path | title}` is optional in query mode; a mismatch is refused with `DOCUMENT_MISMATCH` (no active document: `NO_DOCUMENT`). The script sees `uiapp, app, uidoc, doc, DB, UI, System, eid, inputs, report_progress`, guarded builtins (closed standard-library subset, `clr`, `ctypes`, `subprocess`, sockets… refused at import time) and must assign its answer to `result`.
  - The run happens inside a transaction group on every open, non-linked document, **always rolled back**; nothing reaches the undo stack. Saves, save-as, closes, synchronisations and exports attempted by the script are blocked and named in `blocked` (`save`, `save_as`, `close`, `sync`, `export`); refused imports are listed there as `import:<module>`.
  - A clean query answers status `ok`, `outcome: "ok"`. Otherwise the envelope is status `error` with one code and the **whole run result in `result`**: `WRITE_IN_QUERY` (`outcome: rolled_back`, the guard saw a committed transaction, a save, a close, a sync or an export), `TIMEOUT` (`outcome: timeout`, cooperative deadline), `SCRIPT_ERROR` (`outcome: error`, `error {type, message, line, traceback}`; a syntax error has `reason: syntax_error` and nothing ran), `TIMEOUT_UNAVAILABLE` (the engine's trace hook delivered no event at start-up or at the start of this run: nothing ran). Error messages carry codes, types and line numbers only (they reach the activity card); script text and model data stay in `result`.
  - `stdout` and `stderr` are captured up to 1 MiB each by the engine. A `result` whose JSON exceeds 700 KiB is written to the run journal and replaced by `result_ref` (`runs/<folder>/result.json`, relative to `%LOCALAPPDATA%\rift`), `result_bytes` and a 2 KiB `preview`. The response must fit one 1 MiB frame, so `stdout`/`stderr` carry what fits; `stdout_truncated`/`stderr_truncated` say whether anything was cut (by the 1 MiB bound or by the frame), and `stdout_ref`/`stderr_ref` point to the full bounded text in the journal. Every string goes through the sanitiser of ADR-020 (control and bidirectional characters removed, 64 Ki characters per field).
- `run` in `mode: "modify"` (M2, ADR-009/031) — extra `args {dry_run: true (default), policy: "auto"|"ask" (default auto), allow_reference: false, lint}`. `expected_document` is **required** (`EXPECTED_DOCUMENT_REQUIRED` otherwise, nothing compiled). The document policy is applied before compilation from the active document's class: `sandbox` → the requested policy; `unsaved` → `ask`; `reference` → `ask` with `allow_reference`, refused otherwise; `unmarked` → refused. Refusal: status `refused`, `SANDBOX_REQUIRED`, `result {document_class, reason, policy_requested}`. The run happens under a transaction group named `Agent: <title>` on the target and read groups on the other open documents (always rolled back); saves, closes, syncs and exports are blocked as in query mode; dialogs raised by the script are cancelled (`dialogs`); a commit with errors is rolled back without a dialog and condemns the run (`failures: true`, reason `revit_failure`). Decision: error, timeout, cancellation, Revit failure or a transaction left open → rolled back; else `dry_run` → rolled back (`reason: dry_run`); else `auto` → kept (**one undo entry**); else `ask` → the touched elements are selected and a `TaskDialog` `rift.ask` asks Keep/Discard, Discard by default (`reason: user_discarded`; a dialog that cannot be shown rolls back with `reason: ask_failed`). A successful run answers status `ok` with `outcome: committed | rolled_back`; the run result adds `policy_applied`, `policy_requested`, `policy_reason`, `dry_run`, `history_id` (= `run_id`), `changes {added, modified, deleted, transactions, by_category {<category>: {added, modified, deleted}}, samples [{id, category, name, kind}] ≤ 20}`, `other_documents [{title, transactions, rolled_back}]`, `documents_left_open`, `dialogs`, `failures`. A run that wrote into another open document, or opened or created one, is never kept, dry run included: status `error`, `OTHER_DOCUMENT_WRITE`, `outcome: rolled_back`, `reason: other_document_write | document_opened` (F-R15, F-R14). In `mode: query` a document opened by the script is reported as `WRITE_IN_QUERY` with `blocked: ["open_document"]` and `documents_left_open`: the guard never closes a document. Every modify run, dry run included, is appended to the per-document history.
- `tool` with `history.list {limit ≤ 200}` → `{document {title}, records [...]}` newest first; `history.record {reason, history_id?}` → `{document, record}` (sets `reason_given` on the newest receipt or the one named; `BAD_REQUEST` when there is none). History files: `%LOCALAPPDATA%\rift\history\<16 hex of SHA-256(path or title)>.jsonl`, redacted like the journal.
- `job` with `args {job_id, cancel: true}` (M2) → `{job_id, state, cancel: cancelled|requested|finished, cancel_requested}`, never an error: a queued job ends with `CANCELLED` and never runs; a running job's token is cancelled and the script stops at its next trace event (a modify run is then rolled back, error `CANCELLED`, reason `cancelled`); a finished job is left alone.
- `job` — `args {job_id, wait_s ≤ 300}` → `result {job_id, op, state: queued|running|done, progress, message, queued_utc, started_utc, finished_utc, result}`; a finished job that failed answers with its error code (and its details in `result`); unknown or expired (10 min) → `JOB_NOT_FOUND`.
- `REVIT_BUSY` — when Revit does not start a job within `start_timeout_s` (`config.json`, default 30 s) the answer names the reason (a modal dialog by its title) in the message and in `result {busy_reason, job_id, cancelled: true}`; the job is cancelled and never runs later. A job that already started is never abandoned (`accepted`, then `job`).
- A response body above 1 MiB is replaced by `PAYLOAD_TOO_LARGE`.
- `PAUSED` (ADR-029 §2) — the user paused the server with the ribbon toggle in Revit. Every authenticated request, `lease` included, is refused with status `refused`, message `paused by the user in Revit` and `result {paused_since_utc}`, **before** the handler lookup and the lease: no handler runs, no lease is taken, no job is created. Pausing revokes the lease and cancels the queued jobs (they end `PAUSED`, refused, and never run); a running job is allowed to finish. The client forgets its lease and does not retry; its next call leases again and is refused again while the pause holds. The pause is not remembered between Revit sessions and there is no protocol operation to pause or resume.

- `AGENT_CONFLICT` (ADR-019) — `get_context` reports `coexistence {pyrevit_loaded, other_agents, coexist_with_other_agents, changes_kept}` from the names of the assemblies loaded in Revit (`agent_patterns` in `config.json`). A `run` in `mode: modify` that is not a dry run is refused with status `refused`, `AGENT_CONFLICT` and `result {other_agents}` while another assistant add-in is loaded, unless `coexist_with_other_agents` is true; dry runs are not affected.

## Error codes

Add-in: `BAD_REQUEST, INTERNAL, UNAUTHORIZED, PROTOCOL_MISMATCH, PAYLOAD_TOO_LARGE, RATE_LIMITED, LEASE_HELD, PAUSED, REVIT_BUSY, LISTENER_DEAD, AGENT_CONFLICT, NO_DOCUMENT, DOCUMENT_MISMATCH, SANDBOX_REQUIRED, EXPECTED_DOCUMENT_REQUIRED, CONFIRM_TITLE_MISMATCH, SCRIPT_ERROR, TIMEOUT, CANCELLED, TIMEOUT_UNAVAILABLE, JOB_NOT_FOUND`. Rollback reasons returned as codes with the whole run result: `WRITE_IN_QUERY` (`mode: query`, ADR-025), `OTHER_DOCUMENT_WRITE` (`mode: modify`: the script wrote into another open document, `reason: other_document_write`, or opened or created one, `reason: document_opened`; nothing is kept, F-R15/F-R14).
Server: `NO_TARGET, AMBIGUOUS_TARGET, TARGET_CHANGED, TARGET_LOST, TARGET_BUSY, TARGET_INTERRUPTED, READ_ONLY_MODE, LINT_ERROR, WRITE_FAILED`.

The JSON Schemas in `shared/schemas/` are the normative definition; this document is the readable summary and must be regenerated with them.
