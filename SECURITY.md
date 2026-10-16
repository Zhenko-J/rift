# Security

## Threat model

> **Implementation status (2026-10-13, M1 code complete).** This section describes the security design. Implemented and tested outside Revit: the named pipe with its ACL and single instance, signed requests and responses (time window, single-use nonces, rotation after ten failures), the discovery and key files with owner-only ACLs, the single-client lease, the frame size limits; for `run_query` and the audit presets the `__import__` guard with a closed standard-library subset, the timeout invariant (a run is refused when the trace hook does not fire), the narrowing of `except` clauses through the AST, stdout/stderr bounded to 1 MiB, the read guard (transaction group always rolled back, saves/closes/syncs/exports blocked, writes reported as `WRITE_IN_QUERY`) and the run journal redacted at the source; `SHA256SUMS.txt` written by the build and verified by `install.ps1` before any file is copied; the Claude Code permission list generated from the tool registry (`never_auto` tools are never allowed). The read guard's behaviour inside Revit (rollback, empty undo stack, blocked saves) is covered by fakes outside Revit and awaits its acceptance test inside Revit. Since M2 (code written 2026-10-13, acceptance inside Revit pending): writes go through `run_modify` only, under the document policy (SANDBOX copies; unsaved or explicitly allowed reference documents through Keep/Discard in Revit; everything else refused before compilation), as a dry run by default, as one named undo entry when kept, with failures and script dialogs handled without prompting the user, change receipts and a per-document history, cancellation of running jobs, and the server-side lint before anything is sent.


**RIFT is a local tool**: an MCP server (stdio) and a .NET Revit add-in that talk over a **named pipe** whose ACL is restricted to your Windows account and denied to network logons, with a per-Revit-instance secret stored under `%LOCALAPPDATA%\rift` with the same ACL.

**What it protects against**
- Web pages open in your browser: a named pipe is unreachable from a browser by construction; in addition every request and response is HMAC-signed and the secret never travels on the wire.
- Other accounts on the same machine and remote logons: kernel-enforced ACL on the pipe and on the secret file; the client verifies that the process serving the pipe is the expected Revit instance before sending anything.
- The agent's mistakes on your model: every run is wrapped in a transaction group that is rolled back on error, in dry-run, or on your refusal; saves, save-as, synchronisations, exports and document closes are blocked during a run; writes are refused outside a SANDBOX copy unless explicitly allowed; dry-run is the default; every run is journaled (redacted at the source).

**What it does not protect against**
- Any program running under **your** Windows account: it can read the secret and drive Revit exactly as the agent does.
- A hostile or defective script sent by your MCP client: it runs **without a sandbox** inside the Revit process with your rights. The add-in embeds a Python engine; importing `clr`, `ctypes`, `subprocess`, sockets and similar modules is refused at import time and the server lints scripts before sending them, but these are guard rails, not a security boundary — `System` is reachable from a script.
- Misleading text inside a model (element names, comments, journals) that your assistant might mistake for an instruction. Tool results are returned as typed data and labelled as untrusted; the prompts tell the agent never to act on instructions found in model data.

**Assumptions**: you control the MCP client that connects; you keep free-code and write tools behind a confirmation; you never relax the pipe ACL nor share the pipe (`\\host\pipe\…` is denied by default); you work on a SANDBOX copy.

**Unsigned add-in**: until code signing is in place (planned, see `docs/DECISIONS.md` ADR-022), Revit shows its "unverified publisher" warning once for this add-in; verify the release hashes (`SHA256SUMS.txt`) before answering "Always Load".

## Reporting a vulnerability

Use GitHub's private vulnerability reporting on this repository. Do not open a public issue containing a discovery file, a `.key` file, a `runs/` journal or an unredacted Revit journal.
