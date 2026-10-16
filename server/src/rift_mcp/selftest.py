# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""``rift-mcp selftest``: the acceptance checks of M1-M3 against the Revit that is open.

Every check goes through the same functions as the MCP tools. By default only reads run (plus a
query that tries to write, which the read guard must roll back). With ``--write`` and a SANDBOX
copy active, write tools run as **dry runs** only (nothing is kept): the guard, the policy and
the receipt are exercised without changing the model. With ``--keep`` one change is kept and
then reported, so that its single undo entry can be checked in Revit by hand.

On a document that is not a SANDBOX copy (a reference template, a client project), only pure
reads run: the read-guard check (a transaction rolled back) and the timeout check (two seconds of
busy Revit) are skipped, and so is every write.

The report says, per check, ok / failed / skipped with the reason; the exit code is 0 only when
no check failed. Nothing is written outside the run journal.
"""

from __future__ import annotations

import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field
from typing import Any

from .errors import RiftError
from .tools import audit as _audit
from .tools import run_modify as _run_modify
from .tools import run_query as _run_query
from .tools import typed as _typed
from .tools.run_query import Caller

#: Typed read tools exercised with no argument (they must answer on any project).
READS = (
    "list_levels",
    "list_views",
    "list_view_templates",
    "list_view_filters",
    "list_sheets",
    "list_titleblocks",
    "list_schedules",
    "list_materials",
    "list_loaded_families",
    "list_shared_parameters",
    "list_project_parameters",
    "get_warnings_summary",
    "audit_model",
)


@dataclass
class Check:
    name: str
    status: str  # ok | failed | skipped
    detail: str = ""
    ms: int = 0


@dataclass
class Report:
    checks: list[Check] = field(default_factory=list)
    document: dict[str, Any] | None = None

    @property
    def failed(self) -> list[Check]:
        return [c for c in self.checks if c.status == "failed"]

    def to_dict(self) -> dict[str, Any]:
        counts: dict[str, int] = {}
        for c in self.checks:
            counts[c.status] = counts.get(c.status, 0) + 1
        return {
            "document": self.document,
            "summary": counts,
            "checks": [c.__dict__ for c in self.checks],
        }


async def _run(report: Report, name: str, fn: Callable[[], Awaitable[str]]) -> None:
    started = time.monotonic()
    try:
        detail = await fn()
        report.checks.append(Check(name, "ok", detail, int((time.monotonic() - started) * 1000)))
    except _Skip as skip:
        report.checks.append(Check(name, "skipped", str(skip)))
    except RiftError as exc:
        report.checks.append(
            Check(
                name,
                "failed",
                f"{exc.code}: {exc.message}",
                int((time.monotonic() - started) * 1000),
            )
        )
    except (AssertionError, KeyError, TypeError, ValueError) as exc:
        report.checks.append(
            Check(
                name,
                "failed",
                f"{type(exc).__name__}: {exc}",
                int((time.monotonic() - started) * 1000),
            )
        )


class _Skip(Exception):
    pass


async def selftest(client: Caller, *, write: bool = False, keep: bool = False) -> Report:
    report = Report()
    context: dict[str, Any] = {}

    async def get_context() -> str:
        response = await client.call("context", {}, timeout_s=60, tool="selftest")
        context.update(response.get("result") or {})
        doc = context.get("document")
        assert doc, "no active document: open a SANDBOX copy first"
        report.document = {"title": doc.get("title"), "class": doc.get("class")}
        engine = context.get("engine") or {}
        assert engine.get("timeout_available", True), "TIMEOUT_UNAVAILABLE: scripts are refused"
        version = (context.get("revit") or {}).get("version")
        return f"{doc.get('title')} ({doc.get('class')}), Revit {version}"

    await _run(report, "get_context", get_context)
    if report.failed:
        return report
    sandbox = (report.document or {}).get("class") == "sandbox"

    def guarded_check(fn: Callable[[], Awaitable[str]]) -> Callable[[], Awaitable[str]]:
        """Checks that open a transaction or hold Revit busy run on a SANDBOX copy only: on any
        other document (a reference template, a client project) the user may be working."""

        async def wrapped() -> str:
            if not sandbox:
                raise _Skip("needs a SANDBOX copy: this check opens a transaction or holds Revit")
            return await fn()

        return wrapped

    async def query_ok() -> str:
        out = await _run_query.run_query(
            client, _run_query.RunQueryInput(script="result = doc.Title", title="selftest")
        )
        assert out["run"]["outcome"] == "ok", out["run"].get("outcome")
        return f"{out['run']['timing']['run_ms']} ms"

    async def query_write_is_rolled_back() -> str:
        script = (
            "from Autodesk.Revit.DB import Transaction\n"
            "t = Transaction(doc, 'selftest')\nt.Start()\nt.Commit()\nresult = 1\n"
        )
        try:
            await _run_query.run_query(
                client, _run_query.RunQueryInput(script=script, title="selftest")
            )
        except RiftError as exc:
            assert exc.code == "WRITE_IN_QUERY", exc.code
            return "WRITE_IN_QUERY, rolled back"
        raise AssertionError("a committed transaction in a query was not reported")

    async def import_guard() -> str:
        try:
            await _run_query.run_query(client, _run_query.RunQueryInput(script="import ctypes\n"))
        except RiftError as exc:
            assert "import:ctypes" in (exc.details or {}).get("blocked", []), exc.details
            return "import ctypes refused"
        raise AssertionError("import ctypes was not refused")

    async def timeout() -> str:
        try:
            await _run_query.run_query(
                client, _run_query.RunQueryInput(script="while True:\n    pass\n", timeout_s=2)
            )
        except RiftError as exc:
            assert exc.code == "TIMEOUT", exc.code
            return "infinite loop stopped"
        raise AssertionError("an infinite loop was not stopped")

    async def audit_all() -> str:
        out = await _audit.audit(client, _audit.AuditInput(preset="all", timeout_s=300))
        resume = out["documents"]["00_TOUT"].get("resume", {})
        return ", ".join(f"{k} {v}" for k, v in sorted(resume.items())) or "empty project"

    for name, fn in (
        ("run_query", query_ok),
        ("read_guard_write_in_query", guarded_check(query_write_is_rolled_back)),
        ("import_guard", import_guard),
        ("cooperative_timeout", guarded_check(timeout)),
        ("audit_template_all", audit_all),
    ):
        await _run(report, name, fn)

    for tool in READS:

        async def read(tool: str = tool) -> str:
            out = await _typed.call_typed(client, tool, False, {})
            result = out["result"]
            size = len(result) if isinstance(result, list | dict) else 1
            return f"{size} item(s)"

        await _run(report, tool, read)

    doc_class = (report.document or {}).get("class")
    title = (report.document or {}).get("title") or ""

    async def write_refused_outside_sandbox() -> str:
        if doc_class == "sandbox":
            raise _Skip("the active document is a SANDBOX copy")
        try:
            await _run_modify.run_modify(
                client,
                _run_modify.RunModifyInput(script="result = 1\n", expected_document=title),
            )
        except RiftError as exc:
            assert exc.code in ("SANDBOX_REQUIRED",), exc.code
            return "SANDBOX_REQUIRED before anything ran"
        raise AssertionError("a write on a non-SANDBOX document was not refused")

    await _run(report, "policy_refuses_outside_sandbox", write_refused_outside_sandbox)

    async def dry_run(tool: str, args: dict[str, Any], expect: str) -> str:
        if not write:
            raise _Skip("pass --write to exercise the write path (dry runs only)")
        if doc_class != "sandbox":
            raise _Skip("writes are tested on a SANDBOX copy only")
        out = await _typed.call_typed(
            client, tool, True, {**args, "expected_document": title, "dry_run": True}
        )
        assert (out["outcome"], out["reason"]) == ("rolled_back", "dry_run"), (
            out["outcome"],
            out["reason"],
        )
        changes = out.get("changes") or {}
        assert changes.get(expect, 0) >= 1, changes
        return f"rolled back, receipt {expect}={changes.get(expect)}"

    stamp = time.strftime("%H%M%S")
    await _run(
        report,
        "dry_run_create_material",
        lambda: dry_run("create_material", {"name": f"RIFT selftest {stamp}"}, "added"),
    )
    await _run(
        report,
        "dry_run_create_sheet",
        lambda: dry_run("create_sheet", {"number": f"RIFT-{stamp}", "name": "selftest"}, "added"),
    )

    async def kept_change() -> str:
        if not keep:
            raise _Skip("pass --keep to keep one change (one undo entry to check in Revit)")
        if doc_class != "sandbox":
            raise _Skip("writes are tested on a SANDBOX copy only")
        out = await _typed.call_typed(
            client,
            "create_material",
            True,
            {"name": f"RIFT selftest kept {stamp}", "expected_document": title, "dry_run": False},
        )
        assert out["outcome"] == "committed", out["outcome"]
        return f"committed: undo entry 'Agent: create_material', history {out['history_id']}"

    await _run(report, "kept_change", kept_change)
    return report
