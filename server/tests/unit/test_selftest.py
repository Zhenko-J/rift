# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""rift-mcp selftest: reads always, write dry runs only with --write on a SANDBOX copy, one kept
change only with --keep; a failing check fails the report, a skipped one does not."""

from __future__ import annotations

from typing import Any

import pytest

from rift_mcp.errors import RiftError
from rift_mcp.selftest import READS, selftest


class Scripted:
    """Answers like a healthy add-in on a document of class ``doc_class``."""

    def __init__(self, doc_class: str = "sandbox", broken: str | None = None) -> None:
        self.doc_class = doc_class
        self.broken = broken
        self.calls: list[tuple[str, dict[str, Any]]] = []

    async def call(self, op: str, args: dict[str, Any] | None = None, **_kw: Any) -> dict[str, Any]:
        args = args or {}
        self.calls.append((op, args))
        if op == "context":
            return {
                "result": {
                    "document": {"title": "PLAN_SANDBOX", "class": self.doc_class},
                    "revit": {"version": "2026"},
                    "engine": {"timeout_available": True},
                }
            }
        if op == "compile":
            return {"result": {"ok": True}}
        if op == "run":
            script = args["script"]
            if args.get("mode") == "modify":
                raise RiftError("SANDBOX_REQUIRED", "not a SANDBOX copy")
            if "Commit()" in script:
                raise RiftError("WRITE_IN_QUERY", "rolled back")
            if "ctypes" in script:
                raise RiftError("SCRIPT_ERROR", "refused", {"blocked": ["import:ctypes"]})
            if "while True" in script:
                raise RiftError("TIMEOUT", "stopped")
            return {"result": {"outcome": "ok", "timing": {"run_ms": 3}}}
        if op == "tool":
            name = args["name"]
            if name == self.broken:
                raise RiftError("SCRIPT_ERROR", "AttributeError at line 4")
            if name == "audit.all":
                return {
                    "result": {"documents": {"00_TOUT": {"resume": {"legendes": 2}}}, "runs": {}}
                }
            if "dry_run" in args:
                kept = args["dry_run"] is False
                run = {
                    "outcome": "committed" if kept else "rolled_back",
                    "reason": None if kept else "dry_run",
                    "changes": {"added": 1},
                    "history_id": "h1",
                }
                return {"result": {"tool": name, "result": {}, "run": run}}
            return {"result": {"tool": name, "result": [1, 2], "run": {"outcome": "ok"}}}
        raise AssertionError(op)


def _status(report: Any) -> dict[str, str]:
    return {c.name: c.status for c in report.checks}


async def test_reads_only_by_default_and_writes_are_skipped() -> None:
    addin = Scripted()
    report = await selftest(addin)
    status = _status(report)
    assert report.failed == []
    assert all(status[t] == "ok" for t in READS)
    assert status["read_guard_write_in_query"] == "ok"
    assert status["import_guard"] == "ok"
    assert status["cooperative_timeout"] == "ok"
    assert status["audit_template_all"] == "ok"
    assert status["dry_run_create_material"] == "skipped"
    assert status["kept_change"] == "skipped"
    assert status["policy_refuses_outside_sandbox"] == "skipped"
    assert not any(args.get("dry_run") is False for _op, args in addin.calls)


async def test_write_runs_dry_runs_only_and_keep_keeps_one() -> None:
    addin = Scripted()
    report = await selftest(addin, write=True)
    assert _status(report)["dry_run_create_sheet"] == "ok"
    assert not any(args.get("dry_run") is False for _op, args in addin.calls)
    kept = await selftest(Scripted(), write=True, keep=True)
    assert _status(kept)["kept_change"] == "ok"


async def test_outside_a_sandbox_the_policy_must_refuse_and_writes_are_skipped() -> None:
    report = await selftest(Scripted(doc_class="unmarked"), write=True, keep=True)
    status = _status(report)
    assert status["policy_refuses_outside_sandbox"] == "ok"
    assert status["dry_run_create_material"] == "skipped"
    assert status["kept_change"] == "skipped"
    # on a reference or client document nothing opens a transaction or holds Revit busy
    assert status["read_guard_write_in_query"] == "skipped"
    assert status["cooperative_timeout"] == "skipped"
    assert status["run_query"] == "ok"
    assert report.failed == []


@pytest.mark.parametrize("broken", ["list_sheets", "audit.all"])
async def test_a_failing_check_fails_the_report_with_its_reason(broken: str) -> None:
    report = await selftest(Scripted(broken=broken))
    assert len(report.failed) == 1
    assert "SCRIPT_ERROR" in report.failed[0].detail
    assert report.to_dict()["summary"]["failed"] == 1


async def test_without_a_document_the_selftest_stops_at_once() -> None:
    class NoDoc(Scripted):
        async def call(
            self, op: str, args: dict[str, Any] | None = None, **kw: Any
        ) -> dict[str, Any]:
            if op == "context":
                return {"result": {"document": None}}
            return await super().call(op, args, **kw)

    report = await selftest(NoDoc())
    assert [c.name for c in report.checks] == ["get_context"]
    assert "SANDBOX" in report.failed[0].detail
