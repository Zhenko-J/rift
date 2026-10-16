# RIFT

**An MCP server for Autodesk Revit that lets an AI assistant read and change the open model, behind guard rails that live in Revit itself.**

RIFT connects an MCP client (Claude Code, Claude Desktop, Cursor and other stdio clients) to a running Revit session through a local, signed channel. Reads run inside a transaction group that is always rolled back. Writes are refused outside a SANDBOX copy, run as a dry run by default, and when kept they become a single named undo entry with a change receipt. The guard rails are enforced by the Revit add-in and the server, not by a hook in your MCP client, so they hold whichever client connects.

Inside Revit, RIFT shows up as the `RIFT` panel of a ribbon tab named `NORN`. RIFT creates the tab when it is missing and needs no other add-in: the tab name only lets other plugins by the same author share it when they happen to be installed.

> **Status: pre-alpha.** Milestones M1 (read-only), M2 (guarded writes) and M3 (typed library) are implemented and were accepted inside Revit 2026.3 on a SANDBOX copy on 2026-10-16. The 2026-10-16 benchmark found open security issues in the read-only path. Read [Security status and known limitations](#security-status-and-known-limitations) before you connect RIFT to a model you care about. M4 and M5 are designed but not implemented. Tested only on Revit 2026, Windows 11 and Claude Code.

## Contents

- [What it is, and what it is not](#what-it-is-and-what-it-is-not)
- [Highlights](#highlights)
- [Status](#status)
- [Requirements](#requirements)
- [Quick start](#quick-start)
- [How it works](#how-it-works)
- [Tools](#tools)
- [Prompts and resources](#prompts-and-resources)
- [Safety model](#safety-model)
- [Security status and known limitations](#security-status-and-known-limitations)
- [Configuration](#configuration)
- [CLI reference](#cli-reference)
- [Troubleshooting](#troubleshooting)
- [Comparison with other Revit MCP servers](#comparison-with-other-revit-mcp-servers)
- [Roadmap](#roadmap)
- [Development](#development)
- [Reporting a vulnerability](#reporting-a-vulnerability)
- [Origins and attribution](#origins-and-attribution)
- [Licence](#licence)

## What it is, and what it is not

RIFT has two halves:

- **`server/`** contains `rift-mcp`, a CPython 3.13 MCP server over stdio. Your MCP client launches it. It never listens on a network port.
- **`addin/`** contains a standalone .NET Revit add-in (`IExternalApplication`). It runs inside Revit, serves a named pipe restricted to your Windows account, and runs scripts and typed tools in an embedded IronPython 3.4 engine. It depends on nothing but Revit: no pyRevit, no other add-in.

RIFT is **not**:

- **A sandbox.** Scripts run inside the Revit process with your Windows rights. The guard rails protect your *model* from an assistant's mistakes. They do not protect your *machine* from a hostile script. See [SECURITY.md](SECURITY.md).
- **A hosted service.** Nothing is deployed anywhere, there is no account to create, and RIFT makes no outgoing network calls and no LLM calls.
- **A modelling tool.** RIFT is template-centric: view templates, filters, parameters, schedules, sheets, materials, families and model checks. Typed tools for creating walls, rooms, annotation, MEP or structure are out of scope (ADR-027 §4); a guarded script can still do that through `run_modify`.
- **Multi-version yet.** Only Revit 2026 is built and tested.

## Highlights

- **Reads that cannot keep a change.** `run_query` and every read tool run inside a transaction group, on every open document, that is always rolled back. A script that commits a transaction is reported as `WRITE_IN_QUERY`, and the transaction is undone. Saves, closes, syncs and exports attempted during a run are blocked and named. (This guarantee has two known escapes, listed under [known limitations](#security-status-and-known-limitations).)
- **Guarded writes.** `run_modify` and the 25 typed write tools require `expected_document`. They are refused with `SANDBOX_REQUIRED` before anything compiles unless the active file is a SANDBOX copy, an unsaved document, or a reference you explicitly allow.
- **Dry run by default.** Every write runs, reports what it would change, and is rolled back unless you pass `dry_run: false`.
- **One undo entry.** A kept change is one entry named `Agent: <title>` in Revit's Undo list, so one Ctrl+Z reverts it. `batch` chains up to 50 typed writes into that single entry.
- **Change receipts and history.** Every write, dry runs included, returns a receipt (added, modified and deleted elements by category, with samples) and a `history_id`. `record_change` attaches the reason, and `get_change_records` reads the history back per document.
- **Keep or Discard in Revit.** With `policy: "ask"` (forced on unsaved and allowed reference documents), RIFT selects the touched elements and asks in a Revit dialog, where Discard is the default.
- **A pause in one click.** The large **server on** button of the ribbon pauses RIFT: every call is refused with `PAUSED` before anything runs, the client loses its lease and queued calls are cancelled. A second click resumes.
- **A signed, local pipe.** The pipe is `\\.\pipe\rift-revit-<pid>`, with an ACL for your account only and network logons denied. Every request is signed with HMAC-SHA256 (timestamp, single-use nonce), every answer carries a proof, the secret never travels, and one client holds a lease at a time.
- **A timeout that fires.** The cooperative timeout stops an infinite loop and leaves Revit responsive. If the timeout mechanism is not working, nothing runs (`TIMEOUT_UNAVAILABLE`). Bare `except:` clauses are narrowed, so a script cannot swallow the timeout.
- **Typed tools in millimetres and names.** 50 typed tools take element ids *or* names, refuse ambiguous names, and use millimetres for lengths. 31 read tools run without confirmation in Claude Code with the generated permission list.
- **Template audit presets.** `audit_template` returns six JSON documents in one job: view templates, legends and title blocks; line styles and patterns; text and dimension types; filled regions and fill patterns; schedules; parameters; and a merged summary. `audit_template_write` saves them to a folder you choose.
- **Untrusted output.** Model data comes back in typed fields, with control and bidirectional characters stripped. Every tool description tells the assistant that returned values are data, not instructions.
- **A generated catalogue.** The tool table, MCP schemas, Claude Code permission list and one-page pamphlet are all generated from one registry and checked in CI.

## Status

| Milestone | Scope | State |
|---|---|---|
| M1: read-only end to end | Signed pipe, lease, `get_context`, `run_query` under the read guard, six audit presets, `install claude`, `doctor`, generated listings, `SHA256SUMS.txt` checked at install | Implemented. Exercised in Revit 2026.3 on 2026-10-16 by `rift-mcp selftest --write` and the benchmark. Tranche 1 was accepted on 2026-10-13. |
| M2: guarded writes | `run_modify` (document policy, dry run, write guard, one undo entry, receipts), per-document history, `record_change`, `get_change_records`, `cancel_job`, `change` prompt, server-side lint | Implemented. Accepted 2026-10-16 on a SANDBOX copy: dry run, kept change, rollback on error and timeout, `DOCUMENT_MISMATCH`, history. Keep/Discard (`policy: "ask"`) accepted the same day after a fix (the dialog did not show in the first build). `cancel_job` cannot be reached from an MCP client yet. |
| M3: typed library, wave 1 | 50 typed tools, `survey`, `batch`, `result_ref`, `model_audit` and `pre_issue_check` prompts, `rift://session/context` resource | Implemented. Accepted 2026-10-16 with every domain exercised on a SANDBOX copy. Four defects found during acceptance were fixed in the same commit. |
| M4: free code wave 2 and operations | C# (Roslyn) under the same guard, baked commands, several Revit instances (`list_targets`/`switch_target`), exports, document life cycle, UI tools, `getting_started` prompt | Designed, not implemented (18 tools listed as `designed` below) |
| M5: ribbon and in-Revit UI | Pause button, tooltips, Status window, History window with receipts, tool catalogue | Tranche 1 implemented and accepted in Revit on 2026-10-16: the `server on` / `paused` toggle (ten calls refused with `PAUSED`, none executed), tooltips and long descriptions on every button, the activity card in the project's visual style. The Status and History windows, the card switch and the tool catalogue are planned in [`docs/plans/ui-ribbon/`](docs/plans/ui-ribbon/). |
| v1.0.0 | M1–M3, installer, CI, self-test, benchmark | **Not released.** Blocked on the two P1 items of the 2026-10-16 benchmark that are still open: deferred code and file-system access from `run_query`. |

Versions at HEAD: `rift-mcp` 0.0.1, add-in 0.1.0, wire protocol 2.0. Acceptance records (in French, the maintainer's working language) are in `docs/plans/m1-read-only/00-status.md`, `docs/plans/m2-guarded-writes/00-status.md` and `docs/plans/m3-typed-library/00-status.md`. Progress is in [CHANGELOG.md](CHANGELOG.md).

## Requirements

| Component | Requirement |
|---|---|
| Operating system | Windows 11 (Windows only: named pipes and Windows ACLs) |
| Revit | Autodesk Revit 2026. Builds for other years are not configured yet. |
| Building the add-in | .NET SDK 8.0.400 or later (`addin/global.json`). Revit does not need to be installed for the build. |
| Installing the add-in | PowerShell 7 (`pwsh`). Revit must be closed. |
| Server | Python 3.13 and [uv](https://docs.astral.sh/uv/) |
| MCP client | Any stdio MCP client. `rift-mcp install claude` automates the Claude Code setup; other clients get a config block to paste. |

## Quick start

On Windows 11 with Revit 2026, PowerShell 7, the .NET SDK 8 and [uv](https://docs.astral.sh/uv/) installed, close Revit and run from a clone of this repository:

```powershell
pwsh -File setup.ps1
```

`setup.ps1` checks the requirements, builds and installs the add-in (checksums verified), sets up the server and registers it in Claude Code as `rift`. It stops at the first problem with what to do. `pwsh -File setup.ps1 -CheckOnly` checks without changing anything; `-NoRegister` prints a configuration block for any other MCP client instead.

Then start Revit, answer **Always Load** once (the add-in is not code-signed yet), open the `NORN` tab and the `RIFT` panel, and ask your assistant: *"Use rift: get_context"*.

| Guide | For |
|---|---|
| [INSTALL.md](INSTALL.md) | a person installing RIFT: requirements, one-command and manual installation, checks, permission rules, first session, update, uninstall, troubleshooting |
| [agents/install.md](agents/install.md) | an AI agent asked to install RIFT: the same steps with ground rules (never close Revit itself, ask before changing the user's environment) and a report to give |
| [agents/first-session.md](agents/first-session.md) | an AI agent about to use RIFT's tools in Revit |

Two habits matter from the first minute: **work on a copy whose file name contains `SANDBOX`** (RIFT refuses writes anywhere else), and install the generated **permission rules** (`uv run --directory server rift-mcp permissions --format claude`) so that reads run freely and every write asks first. The **server on** button in the RIFT panel pauses RIFT at once.

## How it works

```text
 MCP client (Claude Code, Claude Desktop, Cursor, ...)
      |
      |  stdio, MCP (JSON-RPC)
      v
 rift-mcp  -- CPython 3.13, launched by the client, no network listener
      |       tool registry -> MCP tools, schemas, annotations, permission list
      |       lint of free code, discovery of Revit instances, lease, retries
      |
      |  named pipe \\.\pipe\rift-revit-<pid>
      |  ACL: current user only, network logons denied
      |  request: HMAC-SHA256 over timestamp, single-use nonce and body hash
      |  response: HMAC proof bound to the request
      v
 RIFT add-in, inside Revit.exe  -- .NET 8, isolated AssemblyLoadContext "rift"
      |       listener -> authenticator -> lease -> router -> job table
      |       ExternalEvent onto the Revit API thread
      |       document policy -> read guard | write guard
      |       IronPython 3.4 engine: import guard, cooperative timeout, bounded output
      |       run journal (redacted), change receipts, per-document history
      v
 Revit API  -- active document; transaction group rolled back, or kept as one undo entry
```

1. **Discovery.** At start-up, the add-in writes `%LOCALAPPDATA%\rift\instances\revit-<pid>.json` (pipe name, versions, generation). Next to it, `revit-<pid>.key` holds a 32-byte secret, with an owner-only ACL. The server trusts a record only if the pid is alive, its creation time matches, and the process serving the pipe is that pid.
2. **Authentication and lease.** Signatures are accepted within a ±120 s window, and each nonce can be used once. After ten failed attempts, the add-in rotates the secret. Only one client holds the 60-second lease; any other client gets `LEASE_HELD` and its request does not run.
3. **Execution.** Each request becomes a job on the Revit API thread. If Revit does not start the job within `start_timeout_s` (30 s by default), for example because a modal dialog is open, the answer is `REVIT_BUSY`. The job is cancelled and never runs later.
4. **Guards.** Reads go through the read guard. Writes go through the document policy, then the write guard (see [Safety model](#safety-model)). Typed tools are embedded IronPython scripts that take the same two paths (ADR-032).
5. **Answers.** Results are JSON, sanitised and capped at 1 MiB per frame. A result larger than 700 KiB stays in the run journal and comes back as a `result_ref` with a preview.

In Revit, a non-modal **activity card** shows when a client connects and when each call ends. It shows the MCP tool name and the error code the client received, never script text or model data. You can turn it off from **Status**.

The wire protocol is documented in [docs/PROTOCOL.md](docs/PROTOCOL.md), with a normative pipe description in [shared/schemas/transport-pipe.md](shared/schemas/transport-pipe.md). The architecture decisions (ADR-001 to ADR-032) are in [docs/DECISIONS.md](docs/DECISIONS.md).

## Tools

All 61 shipped tools appear in Claude Code as `mcp__rift__<tool>`. Each tool takes an optional `pid` to pick a Revit instance when several are running. Write tools take `expected_document` (required), `dry_run` (default `true`), `policy` (`auto` or `ask`) and `allow_reference`.

| Domain | Read (no confirmation with the generated permissions) | Write (guarded, dry run by default) |
|---|---|---|
| Context and code | `get_context`, `run_query`* | `run_modify` |
| Template audit | `audit_template`, `audit_template_write`* | |
| View templates | `list_view_templates`, `get_view_template` | `apply_view_template`, `duplicate_view_template`, `delete_view_template` |
| Filters and graphics | `list_view_filters` | `create_view_filter`, `apply_filter_to_view`, `set_filter_overrides`, `override_element_graphics`, `clear_element_overrides` |
| Parameters | `list_shared_parameters`, `list_project_parameters`, `list_category_parameters` | `create_shared_parameter`, `create_project_parameter`, `bind_shared_parameter` |
| Schedules | `list_schedules`, `get_schedule_data`, `get_schedule_definition` | `create_schedule`, `add_schedule_field` |
| Sheets | `list_sheets`, `list_titleblocks`, `get_titleblock_parameters` | `create_sheet`, `place_view_on_sheet`, `renumber_sheets` |
| Materials | `list_materials`, `get_material_properties` | `create_material`, `assign_material_to_element` |
| Families | `list_loaded_families`, `list_family_types`, `get_family_instances` | `duplicate_family_type`, `rename_family_type` |
| Elements | `list_levels`, `list_views`, `get_element`, `get_element_parameters`, `find_elements`, `get_view_elements` | `set_element_parameters`, `set_type_parameters`, `delete_elements` |
| Model checks | `get_warnings_summary`, `find_untagged_elements`, `audit_model` | `purge_unused` |
| Safety and history | `survey`, `result_ref`, `get_change_records` | `batch`, `record_change`*, `cancel_job`* |

\* asks for confirmation in the generated permission list: `run_query` runs free code, `audit_template_write` writes files, and `record_change` and `cancel_job` have side effects outside the model.

Notes on specific tools:

- `run_query` and `run_modify` take an IronPython 3.4 script that must assign its answer to `result`. The script sees `doc`, `uidoc`, `app`, `uiapp`, `DB`, `UI`, `System`, `eid`, `inputs` and `report_progress`. `timeout_s` ranges from 1 to 3600 s (default 60 for queries, 120 for writes).
- `survey` reports what a change to the given ids would touch, grouped by relationship, before you make it.
- `batch` runs up to 50 typed write tools in order as one undo entry. The first failure rolls everything back.
- `result_ref` reads a large journaled result on this machine; Revit is not called.
- `create_shared_parameter` writes the shared parameter file, which no transaction can undo. In a dry run it therefore only describes what it would create.
- `purge_unused` relies on `Document.GetUnusedElements` (Revit 2024 and later).

The listing below is generated from the tool registry (`server/src/rift_mcp/registry.py`) by `rift-mcp tools --format md --write`, and CI fails when it is stale. The same data feeds the one-page catalogue [docs/pamphlet/index.html](docs/pamphlet/index.html), the input schemas ([docs/pamphlet/schemas.json](docs/pamphlet/schemas.json)) and the permission list ([docs/pamphlet/permissions.claude.json](docs/pamphlet/permissions.claude.json)).

<!-- tools:begin (generated by `rift-mcp tools --format md`, do not edit) -->

79 tools in the catalogue, 61 shipped. `designed` tools are planned and do not run yet. Risk follows ADR-012: `auto` fixed reads, `ask` bounded side effects, `never_auto` writes and the document life cycle. Origin names the counterpart in the pyRevit MCP extension or in rvt-mcp.

**M1 — read-only end to end**

| Tool | Toolset | Risk | Status | Origin |
|---|---|---|---|---|
| `get_context` | context | `auto` | implemented | `pyrevit:get_revit_status`, `pyrevit:get_revit_model_info`, `pyrevit:get_current_view_info`, `rvt-mcp:revit_get_current_target`, `rvt-mcp:revit_get_current_view_info` |
| `run_query` | code | `ask` | implemented | `pyrevit:execute_revit_code`, `rvt-mcp:revit_send_code_to_revit` |
| `audit_template` | audit | `auto` | implemented | `rvt-mcp:revit_list_view_templates`, `rvt-mcp:revit_list_view_filters`, `rvt-mcp:revit_list_schedules`, `rvt-mcp:revit_list_titleblocks`, `rvt-mcp:revit_list_project_parameters`, `rvt-mcp:revit_list_shared_parameters` |
| `audit_template_write` | audit | `ask` | implemented | — |

**M2 — guarded writes**

| Tool | Toolset | Risk | Status | Origin |
|---|---|---|---|---|
| `run_modify` | code | `never_auto` | implemented | `pyrevit:execute_revit_code`, `rvt-mcp:revit_send_code_to_revit` |
| `record_change` | history | `ask` | implemented | `rvt-mcp:revit_record_change` |
| `get_change_records` | history | `auto` | implemented | `rvt-mcp:revit_get_change_records` |
| `cancel_job` | jobs | `ask` | implemented | — |

**M3 — typed library, wave 1**

| Tool | Toolset | Risk | Status | Origin |
|---|---|---|---|---|
| `list_view_templates` | view templates | `auto` | implemented | `rvt-mcp:revit_list_view_templates` |
| `get_view_template` | view templates | `auto` | implemented | `rvt-mcp:revit_list_view_templates` |
| `apply_view_template` | view templates | `never_auto` | implemented | `rvt-mcp:revit_apply_view_template` |
| `duplicate_view_template` | view templates | `never_auto` | implemented | `rvt-mcp:revit_duplicate_view_template` |
| `delete_view_template` | view templates | `never_auto` | implemented | `rvt-mcp:revit_delete_view_template` |
| `list_view_filters` | filters and graphics | `auto` | implemented | `rvt-mcp:revit_list_view_filters` |
| `create_view_filter` | filters and graphics | `never_auto` | implemented | `rvt-mcp:revit_create_view_filter` |
| `apply_filter_to_view` | filters and graphics | `never_auto` | implemented | `rvt-mcp:revit_apply_filter_to_view` |
| `set_filter_overrides` | filters and graphics | `never_auto` | implemented | `rvt-mcp:revit_set_filter_overrides` |
| `override_element_graphics` | filters and graphics | `never_auto` | implemented | `rvt-mcp:revit_override_element_graphics`, `pyrevit:color_splash` |
| `clear_element_overrides` | filters and graphics | `never_auto` | implemented | `rvt-mcp:revit_clear_element_overrides`, `pyrevit:clear_colors` |
| `list_shared_parameters` | parameters | `auto` | implemented | `rvt-mcp:revit_list_shared_parameters` |
| `list_project_parameters` | parameters | `auto` | implemented | `rvt-mcp:revit_list_project_parameters` |
| `list_category_parameters` | parameters | `auto` | implemented | `pyrevit:list_category_parameters` |
| `create_shared_parameter` | parameters | `never_auto` | implemented | `rvt-mcp:revit_create_shared_parameter` |
| `create_project_parameter` | parameters | `never_auto` | implemented | `rvt-mcp:revit_create_project_parameter` |
| `bind_shared_parameter` | parameters | `never_auto` | implemented | `rvt-mcp:revit_bind_shared_parameter` |
| `list_schedules` | schedules | `auto` | implemented | `rvt-mcp:revit_list_schedules` |
| `get_schedule_data` | schedules | `auto` | implemented | `rvt-mcp:revit_get_schedule_data` |
| `get_schedule_definition` | schedules | `auto` | implemented | `rvt-mcp:revit_get_schedule_definition` |
| `create_schedule` | schedules | `never_auto` | implemented | `rvt-mcp:revit_create_schedule` |
| `add_schedule_field` | schedules | `never_auto` | implemented | `rvt-mcp:revit_add_schedule_field` |
| `list_sheets` | sheets | `auto` | implemented | `rvt-mcp:revit_list_sheets` |
| `list_titleblocks` | sheets | `auto` | implemented | `rvt-mcp:revit_list_titleblocks` |
| `get_titleblock_parameters` | sheets | `auto` | implemented | `rvt-mcp:revit_get_titleblock_parameters` |
| `create_sheet` | sheets | `never_auto` | implemented | `rvt-mcp:revit_create_sheet` |
| `place_view_on_sheet` | sheets | `never_auto` | implemented | `rvt-mcp:revit_place_view_on_sheet` |
| `renumber_sheets` | sheets | `never_auto` | implemented | `rvt-mcp:revit_renumber_sheets` |
| `list_materials` | materials | `auto` | implemented | `rvt-mcp:revit_list_materials` |
| `get_material_properties` | materials | `auto` | implemented | `rvt-mcp:revit_get_material_properties` |
| `create_material` | materials | `never_auto` | implemented | `rvt-mcp:revit_create_material` |
| `assign_material_to_element` | materials | `never_auto` | implemented | `rvt-mcp:revit_assign_material_to_element` |
| `list_loaded_families` | families | `auto` | implemented | `rvt-mcp:revit_list_loaded_families`, `pyrevit:list_families`, `pyrevit:list_family_categories` |
| `list_family_types` | families | `auto` | implemented | `rvt-mcp:revit_list_family_types_in_family` |
| `get_family_instances` | families | `auto` | implemented | `rvt-mcp:revit_get_family_instances` |
| `duplicate_family_type` | families | `never_auto` | implemented | `rvt-mcp:revit_duplicate_family_type` |
| `rename_family_type` | families | `never_auto` | implemented | `rvt-mcp:revit_rename_family_type` |
| `list_levels` | elements | `auto` | implemented | `pyrevit:list_levels` |
| `list_views` | elements | `auto` | implemented | `pyrevit:list_revit_views` |
| `get_element` | elements | `auto` | implemented | `rvt-mcp:revit_get_element_details` |
| `get_element_parameters` | elements | `auto` | implemented | `rvt-mcp:revit_get_element_parameters` |
| `find_elements` | elements | `auto` | implemented | `rvt-mcp:revit_ai_element_filter` |
| `get_view_elements` | elements | `auto` | implemented | `pyrevit:get_current_view_elements` |
| `set_element_parameters` | elements | `never_auto` | implemented | `rvt-mcp:revit_set_element_parameter_values` |
| `set_type_parameters` | elements | `never_auto` | implemented | `rvt-mcp:revit_set_type_parameter_values` |
| `delete_elements` | elements | `never_auto` | implemented | `rvt-mcp:revit_delete_element` |
| `get_warnings_summary` | lint | `auto` | implemented | `rvt-mcp:revit_get_model_warnings_summary` |
| `find_untagged_elements` | lint | `auto` | implemented | `rvt-mcp:revit_find_untagged_elements` |
| `audit_model` | lint | `auto` | implemented | `rvt-mcp:revit_workflow_model_audit` |
| `purge_unused` | lint | `never_auto` | implemented | `rvt-mcp:revit_purge_unused` |
| `survey` | safety | `auto` | implemented | `rvt-mcp:revit_survey_change_impact` |
| `batch` | safety | `never_auto` | implemented | `rvt-mcp:revit_batch_execute` |
| `result_ref` | safety | `auto` | implemented | — |

**M4 — free code wave 2 and operations**

| Tool | Toolset | Risk | Status | Origin |
|---|---|---|---|---|
| `list_baked_tools` | baked commands | `auto` | designed | `rvt-mcp:revit_list_baked_tools` |
| `run_baked_tool` | baked commands | `never_auto` | designed | `rvt-mcp:revit_run_baked_tool` |
| `list_targets` | targets | `auto` | designed | `rvt-mcp:revit_list_available_targets`, `pyrevit:list_revit_installations` |
| `switch_target` | targets | `ask` | designed | `rvt-mcp:revit_switch_target` |
| `export_pdf` | exports | `ask` | designed | `rvt-mcp:revit_export_pdf` |
| `export_dwg` | exports | `ask` | designed | `rvt-mcp:revit_export_dwg` |
| `export_ifc` | exports | `ask` | designed | `rvt-mcp:revit_export_ifc` |
| `export_nwc` | exports | `ask` | designed | `rvt-mcp:revit_export_nwc` |
| `export_image` | exports | `ask` | designed | `rvt-mcp:revit_export_image`, `rvt-mcp:revit_capture_view_image`, `pyrevit:get_revit_view` |
| `export_schedule_csv` | exports | `ask` | designed | `rvt-mcp:revit_export_schedule_csv` |
| `open_document` | documents | `ask` | designed | `pyrevit:open_document`, `rvt-mcp:revit_open_model` |
| `save_document` | documents | `never_auto` | designed | `pyrevit:save_document` |
| `close_document` | documents | `never_auto` | designed | `pyrevit:close_document` |
| `sync_with_central` | documents | `never_auto` | designed | `pyrevit:sync_with_central` |
| `launch_revit` | documents | `ask` | designed | `pyrevit:launch_revit` |
| `show_element` | ui | `ask` | designed | `rvt-mcp:revit_show_element_in_view` |
| `select_elements` | ui | `ask` | designed | `rvt-mcp:revit_select_elements` |
| `activate_view` | ui | `ask` | designed | `rvt-mcp:revit_activate_view` |

**MCP prompts and resources**

| Name | Kind | Wave | Status | Origin |
|---|---|---|---|---|
| `change` | prompt | M2 | implemented | `rvt-mcp:revit_change` |
| `model_audit` | prompt | M3 | implemented | `rvt-mcp:revit_model_audit` |
| `pre_issue_check` | prompt | M3 | implemented | `rvt-mcp:revit_pre_issue_check` |
| `getting_started` | prompt | M4 | designed | `rvt-mcp:revit_getting_started` |
| `rift://session/context` | resource | M3 | implemented | `rvt-mcp:Session Context` |

Not taken over on purpose: `pyrevit:place_family` (element creation, excluded by ADR-027 §4; possible with run_modify); `rvt-mcp:revit_show_message` (modal dialog, excluded by ADR-027 §4); `rvt-mcp:revit_stairs` (prompt for creating stairs: element creation, excluded by ADR-027 §4). Whole families: ADR-027 §4 and `docs/plans/tool-catalogue/`.

<!-- tools:end -->

## Prompts and resources

| Name | Kind | What it does |
|---|---|---|
| `change` | prompt (`goal`) | The safe way to change the model: check that the document is a SANDBOX copy, read the scope first and agree it with the user, dry-run and read the receipt, keep the change, then `record_change` the reason |
| `model_audit` | prompt | A read-only audit: context, `audit_model`, warnings, the optional template inventory, and a report table per domain. It proposes changes but makes none. |
| `pre_issue_check` | prompt (`sheets`) | Read-only checks before issuing sheets: title block values, placed views without a template, untagged doors, windows and rooms, warnings |
| `rift://session/context` | resource (JSON) | The context of the served Revit instance, as `get_context` returns it |

## Safety model

RIFT's guarantees are enforced by the add-in, inside Revit, whatever the client. The server adds lint and refuses some calls before sending them. Your MCP client's permission prompts are a third layer, not the main one.

**Document policy (ADR-009).** RIFT classifies the active document by its *file name*, never its title:

| Class | Rule | Writes |
|---|---|---|
| `sandbox` | the file name contains a SANDBOX marker (case-insensitive) | allowed with the requested policy (`auto` keeps, `ask` asks in Revit) |
| `reference` | the file name is listed in `reference_names` | refused unless `allow_reference: true`, then always Keep/Discard |
| `unsaved` | no path yet | always Keep/Discard |
| `unmarked` | anything else | refused with `SANDBOX_REQUIRED` before compilation |

The default markers are `SANDBOX`, `JETABLE`, `_INJ_`, `TESTRUN`, `COPIE`, `COPY` and `SCRATCH`. Writes also need `expected_document` (a path or title). If it does not match the active document, the call is refused with `DOCUMENT_MISMATCH`.

**Read guard (`run_query`, read tools, audit presets).** RIFT opens a transaction group on every open, non-linked document and always rolls it back. A committed transaction, save, close, sync or export makes the run `WRITE_IN_QUERY`, with everything rolled back and the blocked calls named in `blocked`. Revit failures are rolled back without a dialog.

**Write guard (`run_modify`, typed writes, `batch`).** RIFT opens a transaction group named `Agent: <title>` on the target document, and read groups (always rolled back) on the other open documents. Saves, closes, syncs and exports are blocked, and dialogs raised by the script are cancelled. An error, timeout, cancellation, Revit failure or a transaction left open rolls everything back. Otherwise:

- a dry run is rolled back (`reason: dry_run`);
- with `policy: "auto"`, the change is kept as **one** undo entry;
- with `policy: "ask"`, RIFT selects the touched elements and shows Keep/Discard, with Discard as the default.

Every run, dry runs included, returns a receipt and is appended to the document's history.

**Script engine (ADR-016).** The add-in embeds IronPython 3.4 in an isolated load context:

- **Imports** are limited to a closed set of data and text modules plus `System` and `Autodesk`. `clr`, `ctypes`, `subprocess`, sockets, `shutil`, `winreg` and others are refused, and so are imports from namespaces such as `System.Diagnostics`, `System.Net`, `System.Reflection` and `System.Threading`.
- **The timeout** relies on a trace hook. The engine checks that the hook fires when Revit starts and again before each run; otherwise the run is refused with `TIMEOUT_UNAVAILABLE`.
- **Bare and broad `except` clauses** are narrowed through the syntax tree, so a script cannot swallow the timeout.
- **Output** is bounded: stdout and stderr are capped at 1 MiB each.

**Lint (ADR-017).** `run_modify` scripts are linted by the server before anything is sent: IronPython 3.4 syntax (parso), saves, closes and syncs, refused modules, modal dialogs, custom failure preprocessors, and obsolete or fragile API patterns. An error returns `LINT_ERROR` and nothing reaches Revit; warnings come back with the result.

**Journal and redaction (ADR-005).** Each run writes a journal under `%LOCALAPPDATA%\rift\runs\`, redacted at the source: your user name becomes `<USER>`, the machine name becomes `<HOST>`, and the document folder becomes `.`. The add-in keeps journals for 14 days; `rift-mcp purge` deletes them earlier.

**Prompt-injection hygiene (ADR-020).** Results come back as typed fields, never as prose. Control and bidirectional characters are stripped, and each string is capped at 64 Ki characters. Every tool description, prompt and the server instructions tell the assistant that values found in the model are untrusted data, not instructions.

**Integrity.** `install.ps1` verifies every staged file against `SHA256SUMS.txt` before copying. The add-in's build bans process spawning, networking and console APIs.

**Pause (ADR-029 §2).** The ribbon toggle pauses the server from inside Revit, with no protocol operation to undo it remotely. While paused, every authenticated request is refused with `PAUSED` before the handler lookup and the lease; the lease is revoked and queued jobs are cancelled, and a job already running is allowed to finish. The pause is not remembered between Revit sessions.

**What it does not protect against.** Another program running under your Windows account can read the secret and drive Revit the way the assistant does. A hostile script runs with your rights. Another add-in loaded in the same Revit process can bypass RIFT entirely. See [SECURITY.md](SECURITY.md) for the full threat model.

## Security status and known limitations

The [2026-10-16 benchmark](docs/benchmark/2026-10-16-comparison.md) tested the code at commit `02276da`. RIFT held on every security scenario it measured, but the benchmark also found issues that weaken the guarantees above. The status column says which were fixed after the benchmark; the fixes are covered by tests outside Revit and await the maintainer's acceptance inside Revit. Listed by severity:

| Severity | Issue | Status | What to do until it is fixed |
|---|---|---|---|
| High | **Deferred code escapes the read guard and the timeout.** An event handler (for example `Idling`) subscribed from `run_query` runs *after* the run and its guard have ended. The same applies to `PostCommand`, updaters and document events. | measured | Treat `run_query` as code that can change the model. Keep it on "ask" in your client and read scripts before approving them. |
| High | **`run_query` can reach the file system.** The `System` namespace given to scripts is not filtered: a `System.IO` write from a "read-only" query succeeded. `open()` is available, and `Environment.Exit` and process APIs are reachable (static finding). | measured (file write) | Same as above: `run_query` is arbitrary code execution with your rights. |
| Medium | **Misleading outcome on writes into another document.** A `run_modify` script that wrote into a non-active document had that write rolled back, but the answer said `outcome: committed` with an empty receipt. | fixed after the benchmark: the run is never kept and answers `OTHER_DOCUMENT_WRITE`, `outcome: rolled_back`, with `other_documents` | — |
| Medium | **A `run_query` that opens a document** leaves it open: a rollback cannot close a document. | partly fixed: the run now says so (`documents_left_open`, `blocked: open_document`) and a modify run is not kept; the call itself is not refused yet | Do not open documents from scripts; this belongs to M4's `open_document`. |
| Medium | **Confirmation rules are not installed unless you ask.** `install claude` registers the server only; whether `run_query`, `run_modify` and the typed writes ask first depends on your client's permission mode. | fixed after the benchmark: `rift-mcp permissions --install` (or `install claude --with-permissions`, `setup.ps1 -WithPermissions`) merges them, and `rift-mcp doctor` says when scripts and writes do not ask | Install them ([INSTALL.md, section 4](INSTALL.md#4-recommended-settings)). |
| Medium | **Other assistant add-ins in the same Revit** can act on the model outside RIFT's guards. | fixed after the benchmark (ADR-019): `get_context` reports them in `coexistence`, and a change is not kept while one is loaded (`AGENT_CONFLICT`; dry runs still work) unless `coexist_with_other_agents` is set | Unload the other add-in, or accept the risk with `coexist_with_other_agents: true`. |
| Medium | The output sanitiser left Unicode TAG characters, zero-width joiners and a few other invisible format characters. | fixed after the benchmark: every format and private-use character, the TAG block, variation selectors and lone surrogates are removed | Treat model text as untrusted, as the prompts say. |
| Medium | SANDBOX markers matched as substrings, so a file named `Copyright...` counted as a SANDBOX copy. A script can also edit `config.json` (through the file-system issue above). | markers fixed after the benchmark (delimited words, at least 4 characters); `config.json` stays writable by a script until the file-system issue is fixed | Choose distinctive markers in `config.json`. |
| Medium | 11 typed writes that overwrite values were annotated `destructiveHint: false`. | fixed after the benchmark | — |
| Medium | Stale texts: the server instructions only describe the M1/M2 tools and some refusal messages mention earlier milestones (the Status dialog line "milestone M1: read-only" was removed after the benchmark). | static | No functional impact. |
| Medium | The cooperative timeout could be defeated by calls that emit no trace events, such as a catastrophic regular expression or a long native call. | unverified | Keep `timeout_s` tight and avoid unbounded regular expressions. |
| Low | `cancel_job` cannot be reached from an MCP client, because `run_modify` returns no `job_id` while it waits. | static | Use the timeout instead. |
| Low | `list_view_templates` returns the *number* of controlled parameters, not their names. | measured | Use `get_view_template` or `audit_template` (p1). |
| Low | `TARGET_LOST` on the first call after Revit restarts. | measured | Call again; the server rediscovers the instance. |
| Low | The add-in is unsigned. `SHA256SUMS.txt` proves integrity but not authenticity: the sums sit beside the files. | static | Build from source or verify the CI artifact. Code signing is planned (ADR-022). |

The benchmark's action list (section (e)) schedules all P1 items before v1.0.0. [SECURITY.md](SECURITY.md) describes the threat model and how to report a vulnerability.

## Configuration

**Add-in:** `%LOCALAPPDATA%\rift\config.json`. The file is optional; RIFT uses the defaults when it is missing or invalid. Restart Revit after editing it. The activity-card switch in **Status** saves itself.

| Key | Default | Meaning |
|---|---|---|
| `activity_card` | `true` | Show the activity card in Revit |
| `sandbox_markers` | `["SANDBOX", "JETABLE", "_INJ_", "TESTRUN", "COPIE", "COPY", "SCRATCH"]` | File-name markers of a SANDBOX copy. A list here **replaces** the defaults. |
| `reference_names` | `[]` | File names (with or without extension) of reference documents: writable only with `allow_reference: true` and Keep/Discard |
| `start_timeout_s` | `30` | How long Revit has to start a job before `REVIT_BUSY` (clamped to 5–300) |
| `developer` | `false` | Shows the developer-only **Probe Python** ribbon button |
| `agent_patterns` | `RvtMcp*`, `revit_mcp*`, `RevitMCP*`, `*mcp-server-for-revit*` | Assembly name patterns of other assistant add-ins (ADR-019); matched case-insensitively, `*` and `?` as wildcards |
| `coexist_with_other_agents` | `false` | Keep changes even while another assistant add-in is loaded (otherwise `AGENT_CONFLICT`; dry runs always work) |

Example:

```json
{
  "activity_card": true,
  "sandbox_markers": ["_SANDBOX", "_SCRATCH"],
  "reference_names": ["MyTemplate.rte"],
  "start_timeout_s": 30
}
```

**Data folder:** `%LOCALAPPDATA%\rift\`

| Path | Content |
|---|---|
| `instances\revit-<pid>.json`, `revit-<pid>.key` | Discovery record and pipe secret of each running Revit (owner-only ACL). Never share them. |
| `logs\addin-<pid>.log` | Add-in log (**Status** can copy its path) |
| `runs\` | Run journals, redacted, kept 14 days (`rift-mcp purge`) |
| `history\<hash>.jsonl` | Per-document change history (receipts and reasons) |
| `config.json` | Add-in configuration |

**Server:** the server has no configuration file. Each tool's `pid` argument picks the Revit instance. The `RIFT_HOME` environment variable changes where the *server* looks for discovery files; it exists for tests, and the add-in always writes under `%LOCALAPPDATA%\rift`.

## CLI reference

`rift-mcp --version` prints the server and protocol versions. Run the commands with `uv run --directory server rift-mcp <command>`, or `rift-mcp <command>` from the activated environment.

| Command | What it does |
|---|---|
| `serve` | Runs the MCP server over stdio (what your MCP client launches) |
| `install claude [--scope user\|local\|project] [--print]` | Registers the server through `claude mcp add` (scope `user` by default) and prints the `mcpServers` block for other clients. It never writes a file itself. |
| `doctor` | Lists the Revit instances found (pid, pipe, versions, generation, health), why any are ignored, which one would be used, and whether the `claude` CLI is on the PATH. Exits 1 when there is no usable target. |
| `selftest [--write] [--keep] [--pid N]` | Acceptance checks against the open Revit, as a JSON report. Pure reads on any document. The read-guard and timeout checks, and with `--write` the dry runs of the write tools, run on a SANDBOX copy only. `--keep` keeps one change, to check its single undo entry. Exits 1 on any failure. |
| `tools [--format md\|json] [--check\|--write]` | The tool catalogue (this README's table and `docs/pamphlet/tools.json`) |
| `permissions [--format claude] [--check\|--write]` | The Claude Code permission rules (`docs/pamphlet/permissions.claude.json`) |
| `schemas [--check\|--write]` | The input schemas as the server advertises them (`docs/pamphlet/schemas.json`) |
| `purge [--days N]` | Deletes run journals older than N days (all of them by default) |

`pause` and `resume` exist only to say that pausing is a ribbon command (ADR-029); they exit with code 2.

## Troubleshooting

Start with `rift-mcp doctor`. Every failed call returns `{"ok": false, "error": {"code", "message", "details"}}`. The codes below are the ones you are likely to meet; [docs/PROTOCOL.md](docs/PROTOCOL.md) lists them all.

| Code or symptom | Meaning | What to do |
|---|---|---|
| `NO_TARGET` | No live Revit with the RIFT add-in was found | Start Revit and check that the `NORN` tab shows the `RIFT` panel. Run `doctor`. |
| `AMBIGUOUS_TARGET` | Several Revit instances run the add-in | Pass `pid` (from `doctor` or `get_context`) to each tool |
| `TARGET_LOST` | Typically the first call after Revit restarted (the server had cached the old pid) | Call again; the server rediscovers the new instance |
| `TARGET_CHANGED` | The pid was reused, or the pipe is served by another process, or the response proof failed | Call again. If it persists, run `doctor` and restart Revit. |
| `TARGET_BUSY` | The pipe stayed busy for more than 2 s, or a job is still running | Retry shortly |
| `LEASE_HELD` | Another client holds the 60-second lease (another assistant session, or `selftest` running alongside your client) | Close the other client or wait 60 s. **Status** shows the holder. |
| `AGENT_CONFLICT` | Another assistant add-in is loaded in this Revit (listed in `other_agents`); RIFT does not keep changes while it is (ADR-019) | Unload it, or set `coexist_with_other_agents: true` in `config.json`. Dry runs still work. |
| `UNAUTHORIZED` | The signature was refused (wrong secret, clock outside ±120 s, replayed nonce) | Restart the MCP server. After ten failures the add-in rotates its secret, and the client re-reads it once. |
| `PROTOCOL_MISMATCH` | Server and add-in speak different major protocol versions | Rebuild both from the same commit |
| `REVIT_BUSY` | Revit did not start the job within `start_timeout_s`; the message names the modal dialog when there is one | Close the dialog and call again. The cancelled job never runs later. |
| `NO_DOCUMENT` | No active document | Open one |
| `DOCUMENT_MISMATCH` | `expected_document` is not the active document | Activate the intended document, or fix the path or title |
| `EXPECTED_DOCUMENT_REQUIRED` | A write was sent without `expected_document` | Pass the path or title from `get_context` |
| `SANDBOX_REQUIRED` | The active file is not a SANDBOX copy and is not an allowed reference | Save a copy whose name contains a marker (for example `MyTemplate_SANDBOX.rte`) and activate it |
| `LINT_ERROR` | The server lint found an error; nothing was sent | Fix the line given in `details` |
| `WRITE_IN_QUERY` | A read tried to write; everything was rolled back | Use `run_modify` (dry run first) for changes |
| `READ_ONLY_MODE` | `run_query` was called with a mode other than `query` | Use `run_modify` |
| `SCRIPT_ERROR` | The script raised an exception, or a typed tool could not resolve an argument | Read `error.line` and `error.message`. For typed tools, line numbers include the shared prelude. |
| `TIMEOUT` | The script passed `timeout_s`; everything was rolled back | Narrow the script, or raise `timeout_s` (up to 3600) |
| `TIMEOUT_UNAVAILABLE` | The engine's trace hook did not fire, so RIFT refused to run anything without a working timeout | Restart Revit. The add-in log should show `timeout_available=True` at start-up, and **Status** shows the engine state. |
| `PAYLOAD_TOO_LARGE` | The answer exceeded the 1 MiB frame | Return less. Results above 700 KiB come back through `result_ref`. |
| `LISTENER_DEAD` | Revit refused or lost the add-in's external event | Restart Revit |
| `JOB_NOT_FOUND` | Unknown job, or it expired after 10 minutes | Run the call again |
| `WRITE_FAILED` | `audit_template_write` could not write to `write_to` | Check the folder and your rights |
| `install.ps1`: "Revit is running" | The installer never touches a running Revit | Close Revit and run it again |
| `install.ps1`: SHA256SUMS mismatch | The staged folder differs from its sums | Rebuild. `-SkipHashCheck` exists but defeats the check. |
| `install claude` fails | The `claude` CLI is missing, or a server named `rift` is already registered | Run `claude mcp remove rift` and retry, or paste the printed config block |
| No `NORN` tab in Revit | The add-in did not load | Check `%APPDATA%\Autodesk\Revit\Addins\2026\rift.addin` and the log under `%LOCALAPPDATA%\rift\logs\` |

When you open an issue, attach the (redacted) output of `rift-mcp doctor` and, if relevant, the `run_id`. Never post a `.key` file, a discovery file or an unredacted journal.

## Comparison with other Revit MCP servers

The [2026-10-16 benchmark](docs/benchmark/2026-10-16-comparison.md) compared the three MCP servers for Revit installed on the maintainer's workstation, in one live Revit 2026.3 session, on a SANDBOX copy. It combined a measured scenario bench with static review. It follows the [2026-10-09 baseline](docs/benchmark/2026-10-09-comparison.md), which scored RIFT on its design only. The scenarios are in [docs/benchmark/scenarios.md](docs/benchmark/scenarios.md).

| | RIFT | rvt-mcp (BIMWright) 1.1.0 | pyRevit MCP extension 0.1.0 |
|---|---|---|---|
| Tools / prompts / resources | 61 / 3 / 1 | 233 / 6 / 1 | 20 / 0 / 0 |
| Transport | Named pipe with explicit ACL, HMAC per frame, lease | Named pipe, per-session token | HTTP on localhost, no authentication |
| Free code | IronPython 3.4, import guard, timeout | C# (Roslyn), on by default | IronPython 2.7, no timeout |
| Document policy in the server | Yes (SANDBOX / reference) | No | No |
| Scenario score (unweighted, 12 scenarios; rvt-mcp: 10 played) | 22 / 24 | 7 / 20 | 2 / 24 |
| Save during a run | refused inside Revit | saved (only a client-side hook asked) | saved (only a client-side hook asked) |
| Revit blocked by a modal dialog | `REVIT_BUSY` naming the dialog, job cancelled | client timeout, no reason | timeout; the request stays queued and runs later |

In short:

- **RIFT** was the only server whose guard rails held on every security scenario measured. Its read-only mode is weaker than it claims (see [known limitations](#security-status-and-known-limitations)).
- **rvt-mcp** has by far the broadest and best-guided tool surface and a richer in-Revit interface. As configured for the test, nothing in the server stopped a write, so the benchmark recommends running it with its read-only or no-free-code options alongside RIFT.
- **The pyRevit MCP extension** executed unauthenticated code sent to its local HTTP port and failed every measured security scenario.

Caveats: the results come from a single workstation and are not reproducible by a third party. Several cells are still pending a human at the keyboard. Each server was scored as configured. And a Revit process is only as safe as the weakest agent add-in it loads.

## Roadmap

The direction is set by ADR-026: one server that combines a typed, annotated tool surface and an in-Revit experience (inspired by rvt-mcp) with Python scripting (inspired by the pyRevit MCP extension), on RIFT's own authenticated transport, document policy, guards and journal.

1. **Before v1.0.0 (P1 in the benchmark, still open):**
   - refuse deferred code (event subscriptions, `PostCommand`, updaters, opening documents) inside the add-in;
   - replace the raw `System` namespace with a narrow façade and remove `open`;

   Done after the benchmark: permission rules installed on request and checked by `doctor`, coexistence detection with `AGENT_CONFLICT`, honest reports of writes into other documents, stripping of invisible format characters, delimited SANDBOX markers, accurate `destructiveHint` values, the Status text.
2. **Next (P2):**
   - rewrite the server instructions and the `change` prompt for the typed library;
   - add enums and parameter descriptions on the M1/M2 tools;
   - make `list_view_templates` return the controlled parameter names;
   - fill the template-domain gaps: visibility/graphics by category, template from a view, removing a filter from a view, schedulable fields and schedule formatting, removing a parameter binding, loading, replacing and unloading families;
   - add pipe read timeouts and a rotation rate limit;
   - rediscover within the same call on `TARGET_LOST`.
3. **M4:**
   - C# (Roslyn) under the same guard;
   - baked commands;
   - several Revit instances;
   - exports (PDF, DWG, IFC, NWC, images, schedules);
   - document life cycle (open, save, close, sync) and UI tools;
   - the `getting_started` prompt.
4. **M5:** the rest of the ribbon and in-Revit UI: the card switch, Status and History windows with receipts, and a tool catalogue ([`docs/plans/ui-ribbon/`](docs/plans/ui-ribbon/)).
5. **Later:** code signing (ADR-022), signed checksums published out of band, and builds for other Revit years when an issue asks for them.

## Development

| Path | Content |
|---|---|
| `server/` | `rift-mcp`: MCP server, pipe client, registry, lint, CLI; pytest suite with a fake Revit |
| `addin/src/Rift.Addin` | Bootstrap named in the manifest; creates the isolated load context |
| `addin/src/Rift.Core` | Protocol, pipe, HMAC, discovery, lease, jobs, policy, journal, sanitiser (no Revit reference) |
| `addin/src/Rift.Scripting` | IronPython engine, guards for imports and `except`, run paths, audit presets, typed tool scripts (`Tools/*.py`) |
| `addin/src/Rift.Revit` | Dispatcher, read and write guards, Keep/Discard dialog, ribbon |
| `addin/src/Rift.Ui` | Theme and activity card in WPF built in code, without the Revit API (testable without Revit) |
| `addin/tests/` | xUnit tests (`Rift.Tests`, no Revit) and `Rift.PipeHost`, the real listener with a fake dispatcher, used by the integration tests |
| `shared/schemas/` | Pipe transport specification and shared HMAC test vectors |
| `docs/` | ADRs, protocol, milestone plans, benchmarks, generated pamphlet |
| `tools/` | Pamphlet generator, HMAC vector generator, checksum and private-term scripts |
| `setup.ps1`, `install.ps1` | One-command installation; add-in install and uninstall with checksum verification |
| `INSTALL.md`, `agents/`, `AGENTS.md` | Installation guide for people; procedures for AI agents that install or use RIFT |

**Build and test**

```powershell
# Add-in (no Revit needed: reference packages only)
dotnet build addin/rift.sln -c "Release R26"
dotnet test addin/tests/Rift.Tests -c Release

# Server
uv sync --directory server --all-extras
uv run --directory server pytest -q
uv run --directory server ruff check .
uv run --directory server rift-mcp tools --check
```

At HEAD there are 366 xUnit tests and 138 pytest tests. The xUnit tests compile every embedded script under IronPython 3.4 with the allowed imports only, check that reads open no transaction and writes do, and check routing. The pytest suite includes end-to-end runs of the real CPython client against the real pipe listener in `Rift.PipeHost`. Behaviour inside Revit is checked with `rift-mcp selftest` on a SANDBOX copy and recorded in the milestone status files.

**CI** (`.github/workflows/ci.yml`) runs four jobs:

- **Privacy:** gitleaks, a clean-room guard (no pyRevit code imported or referenced) and a private-term check.
- **Server:** ruff, pytest, and freshness checks for the schemas, permissions, tool listings and pamphlet.
- **Add-in:** builds without Revit, runs xUnit, verifies `SHA256SUMS.txt`, and uploads the staged add-in.
- **Changelog:** requires a `CHANGELOG.md` line on every pull request, unless the PR has the `no-changelog` label.

**Contributing.** Read [CONTRIBUTING.md](CONTRIBUTING.md). In short:

- Open an issue first for anything bigger than a typo.
- Branch from `main`, add a line under `Unreleased` in `CHANGELOG.md`, and install the `pre-commit` hooks.
- Never commit model files, journals, discovery or key files, or real project data.
- Keep the add-in dependent on nothing but Revit, and make no LLM calls inside RIFT.

Architecture changes start as an issue that references the ADR to revisit.

## Reporting a vulnerability

Use GitHub's private vulnerability reporting on this repository. Do not open a public issue that contains a discovery file, a `.key` file, a `runs/` journal or an unredacted Revit journal. See [SECURITY.md](SECURITY.md).

## Origins and attribution

- The tool surface draws on two existing MCP servers for Revit (ADR-026): [bimwright/rvt-mcp](https://github.com/bimwright/rvt-mcp) (Apache-2.0), for the typed, annotated tools, change receipts and in-Revit experience; and the pyRevit MCP extension [mcp-servers-for-revit/mcp-server-for-revit-python](https://github.com/mcp-servers-for-revit/mcp-server-for-revit-python) (MIT), for Python scripting and the status, view and family tools. The `Origin` column above names the counterpart tool. Any file adapted from rvt-mcp carries an attribution header; nothing is copied from the pyRevit extension beyond tool names.
- pyRevit itself (GPL-3.0) served as a behavioural specification only (clean-room, ADR-011). No code was consulted or copied, and RIFT neither loads nor requires it.

## Licence

Apache-2.0. See [LICENSE](LICENSE), [NOTICE](NOTICE) and [THIRD_PARTY.md](THIRD_PARTY.md). The add-in redistributes IronPython 3.4 and the Dynamic Language Runtime (Apache-2.0). Its ribbon icons are drawn from Lucide outlines (ISC).

RIFT is an independent open-source project. It is not affiliated with, sponsored by, or endorsed by Autodesk, Inc. or Anthropic. Autodesk and Revit are registered trademarks or trademarks of Autodesk, Inc., in the USA and other countries. All other trademarks are the property of their respective owners.
