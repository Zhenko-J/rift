# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""M2 plan tests 8 and 9: run_modify refuses without sending when expected_document is missing
or the lint fails; dry run by default; committed and refused answers relayed; history and
cancel_job send the right envelopes."""

from __future__ import annotations

from typing import Any

import pytest
from test_tools import COMPILE_OK, LEASE_OK, FakeAddin, _client

from rift_mcp.errors import RiftError
from rift_mcp.server import ClientPool, build_server
from rift_mcp.tools import history
from rift_mcp.tools import run_modify as rm

SCRIPT = "t = Transaction(doc, 'x')\nt.Start()\nt.Commit()\nresult = 1\n"
RECEIPT = {"added": 1, "modified": 2, "deleted": 0, "transactions": 1, "by_category": {}}


def _run_ok(outcome: str = "rolled_back", reason: str | None = "dry_run") -> dict[str, Any]:
    run = {
        "outcome": outcome,
        "reason": reason,
        "dry_run": reason == "dry_run",
        "history_id": "abc123",
        "changes": RECEIPT,
    }
    return {"status": "ok", "result": run, "target": {"pid": 4242}}


async def test_a_dry_run_is_the_default_and_sends_mode_modify_with_the_document() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("compile", COMPILE_OK)
    addin.answer("run", _run_ok())
    args = rm.RunModifyInput(script=SCRIPT, expected_document=r"C:\w\A_SANDBOX.rvt", title="t")
    out = await rm.run_modify(_client(addin), args)
    assert out["ok"] is True
    assert (out["outcome"], out["reason"], out["history_id"]) == (
        "rolled_back",
        "dry_run",
        "abc123",
    )
    assert out["changes"] == RECEIPT
    run = addin.calls[2]
    assert run["args"]["mode"] == "modify"
    assert run["args"]["dry_run"] is True
    assert run["args"]["policy"] == "auto"
    assert run["args"]["allow_reference"] is False
    assert run["expected_document"] == {"path": r"C:\w\A_SANDBOX.rvt"}
    assert run["client"]["tool"] == "run_modify"


async def test_a_kept_change_is_relayed_as_committed() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("compile", COMPILE_OK)
    addin.answer("run", _run_ok("committed", None))
    args = rm.RunModifyInput(script=SCRIPT, expected_document="A_SANDBOX", dry_run=False)
    out = await rm.run_modify(_client(addin), args)
    assert out["outcome"] == "committed"
    assert addin.calls[2]["args"]["dry_run"] is False
    assert addin.calls[2]["expected_document"] == {"title": "A_SANDBOX"}


async def test_lint_errors_stop_before_anything_is_sent_and_warnings_come_back() -> None:
    addin = FakeAddin()
    bad = await rm.run_modify(
        _client(addin), rm.RunModifyInput(script="doc.Save()\n", expected_document="A_SANDBOX")
    )
    assert bad["error"]["code"] == "LINT_ERROR"
    assert bad["error"]["details"]["errors"][0]["code"] == "E011"
    assert addin.calls == []

    addin.answer("lease", LEASE_OK)
    addin.answer("compile", COMPILE_OK)
    addin.answer("run", _run_ok())
    out = await rm.run_modify(
        _client(addin),
        rm.RunModifyInput(script="v = doc.ActiveView\nresult = 1\n", expected_document="A_SANDBOX"),
    )
    assert out["lint"]["warnings"][0]["code"] == "W003"


async def test_lint_can_be_switched_off_and_the_add_in_is_told() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("compile", COMPILE_OK)
    addin.answer("run", _run_ok())
    args = rm.RunModifyInput(script="doc.Save()\n", expected_document="A_SANDBOX", lint=False)
    await rm.run_modify(_client(addin), args)
    assert addin.calls[2]["args"]["lint"] is False


async def test_sandbox_required_is_relayed_as_a_refusal() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("compile", COMPILE_OK)
    addin.answer(
        "run",
        {
            "status": "refused",
            "error": {"code": "SANDBOX_REQUIRED", "message": "not a SANDBOX copy"},
            "result": {"document_class": "unmarked"},
        },
    )
    with pytest.raises(RiftError) as info:
        await rm.run_modify(
            _client(addin), rm.RunModifyInput(script=SCRIPT, expected_document="PLAN")
        )
    assert info.value.code == "SANDBOX_REQUIRED"
    assert info.value.details == {"document_class": "unmarked"}


async def test_the_mcp_tool_refuses_without_expected_document_and_without_sending() -> None:
    addin = FakeAddin()

    def factory(_pid: int | None) -> Any:
        return _client(addin)

    server = build_server(ClientPool(factory))
    tools = {t.name: t for t in await server.list_tools()}
    assert tools["run_modify"].annotations.destructiveHint is True
    assert tools["run_modify"].annotations.readOnlyHint is False
    out = await server.call_tool("run_modify", {"script": SCRIPT})
    payload = out[1] if isinstance(out, tuple) else out
    assert "EXPECTED_DOCUMENT_REQUIRED" in str(payload)
    assert addin.calls == []
    prompts = {p.name for p in await server.list_prompts()}
    assert "change" in prompts
    text = await server.get_prompt("change", {"goal": "rename templates"})
    assert "dry_run" in str(text) and "SANDBOX" in str(text)


async def test_history_and_cancel_send_their_envelopes() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer(
        "tool",
        {"status": "ok", "result": {"document": {"title": "A"}, "record": {"history_id": "h1"}}},
        {"status": "ok", "result": {"document": {"title": "A"}, "records": [{"history_id": "h1"}]}},
    )
    addin.answer(
        "job",
        {"status": "ok", "result": {"job_id": "j1", "cancel": "requested", "state": "running"}},
    )
    client = _client(addin)
    rec = await history.record_change(client, "user asked", "h1")
    assert rec["record"] == {"history_id": "h1"}
    assert addin.calls[1]["args"] == {
        "name": "history.record",
        "reason": "user asked",
        "history_id": "h1",
    }
    lst = await history.get_change_records(client, 5)
    assert lst["records"] == [{"history_id": "h1"}]
    assert addin.calls[2]["args"] == {"name": "history.list", "limit": 5}
    can = await history.cancel_job(client, "j1")
    assert (can["cancel"], can["state"]) == ("requested", "running")
    assert addin.calls[3]["args"] == {"job_id": "j1", "cancel": True}
    assert addin.calls[3]["client"]["tool"] == "cancel_job"
