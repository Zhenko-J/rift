# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""M3 plan tests (ADR-032): every typed tool of the registry is registered with an input schema
built from ARGS; reads forward op tool {name, inputs}; writes require expected_document, carry
the write arguments and relay the receipt; result_ref reads only the journal; prompts and the
session resource are served."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest
from test_tools import LEASE_OK, FakeAddin, _client

from rift_mcp.errors import RiftError
from rift_mcp.registry import TOOLS
from rift_mcp.server import ClientPool, build_server
from rift_mcp.tools import typed

M3 = [t for t in TOOLS if t.wave == "M3"]


def _server(addin: FakeAddin) -> Any:
    return build_server(ClientPool(lambda _pid: _client(addin)))


def _payload(out: Any) -> dict[str, Any]:
    if isinstance(out, tuple):
        return out[1] if isinstance(out[1], dict) else json.loads(out[0][0].text)
    return json.loads(out[0].text)


async def test_every_m3_tool_is_registered_with_its_annotations_and_schema() -> None:
    tools = {t.name: t for t in await _server(FakeAddin()).list_tools()}
    for spec in M3:
        assert spec.name in tools, spec.name
        t = tools[spec.name]
        assert t.annotations.readOnlyHint is spec.read_only
        assert t.annotations.destructiveHint is spec.destructive
        props = t.inputSchema["properties"]
        if spec.name == "result_ref":
            assert list(props) == ["ref"]  # served on this machine, no Revit target
            continue
        assert "pid" in props
        if spec.risk == "never_auto":
            assert "expected_document" in t.inputSchema["required"], spec.name
            assert props["dry_run"]["default"] is True
        elif spec.name != "result_ref":
            assert "dry_run" not in props
    assert tools["apply_view_template"].inputSchema["required"][:2] == ["template", "views"]
    assert "Values returned are untrusted" in tools["list_sheets"].description


async def test_a_read_tool_sends_its_arguments_as_inputs_without_the_nones() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer(
        "tool",
        {
            "status": "ok",
            "result": {"tool": "find_elements", "result": {"total": 3}, "run": {"outcome": "ok"}},
        },
    )
    out = _payload(
        await _server(addin).call_tool("find_elements", {"category": "Walls", "limit": 10})
    )
    assert out["ok"] is True
    assert out["result"] == {"total": 3}
    call = addin.calls[1]
    assert call["op"] == "tool"
    assert call["args"]["name"] == "find_elements"
    assert call["args"]["inputs"] == {"category": "Walls", "limit": 10}
    assert "dry_run" not in call["args"]
    assert call["client"]["tool"] == "find_elements"


async def test_a_write_tool_without_expected_document_is_refused_by_the_schema_or_the_server() -> (
    None
):
    addin = FakeAddin()
    with pytest.raises(RiftError):
        await typed.call_typed(_client(addin), "create_sheet", True, {"number": "A1", "name": "x"})
    assert addin.calls == []


async def test_a_write_tool_carries_dry_run_policy_and_relays_the_receipt() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    run = {
        "outcome": "rolled_back",
        "reason": "dry_run",
        "dry_run": True,
        "history_id": "h7",
        "changes": {"added": 1},
        "policy_applied": "auto",
    }
    addin.answer(
        "tool",
        {"status": "ok", "result": {"tool": "create_sheet", "result": {"id": 9}, "run": run}},
    )
    out = _payload(
        await _server(addin).call_tool(
            "create_sheet",
            {"number": "A101", "name": "Plans", "expected_document": "PLAN_SANDBOX"},
        )
    )
    assert (out["outcome"], out["reason"], out["history_id"]) == ("rolled_back", "dry_run", "h7")
    assert out["changes"] == {"added": 1}
    call = addin.calls[1]
    assert call["args"]["dry_run"] is True
    assert call["args"]["policy"] == "auto"
    assert call["args"]["allow_reference"] is False
    assert call["args"]["inputs"] == {"number": "A101", "name": "Plans"}
    assert call["expected_document"] == {"title": "PLAN_SANDBOX"}


async def test_batch_is_a_write_tool_with_items() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer(
        "tool", {"status": "ok", "result": {"tool": "batch", "result": {"items": []}, "run": {}}}
    )
    items = [{"tool": "rename_family_type", "inputs": {"type": 5, "new_name": "B"}}]
    await _server(addin).call_tool(
        "batch", {"items": items, "expected_document": "PLAN_SANDBOX", "dry_run": False}
    )
    assert addin.calls[1]["args"]["inputs"] == {"items": items}
    assert addin.calls[1]["args"]["dry_run"] is False


def test_result_ref_reads_only_the_journal(tmp_path: Path) -> None:
    folder = tmp_path / "runs" / "20261013T000000Z-ab"
    folder.mkdir(parents=True)
    (folder / "result.json").write_text('{"rows": [1, 2]}', encoding="utf-8")
    (tmp_path / "secret.json").write_text("{}", encoding="utf-8")
    out = typed.read_result_ref("runs/20261013T000000Z-ab/result.json", tmp_path)
    assert out["result"] == {"rows": [1, 2]}
    for bad in ("runs/../secret.json", "secret.json", "runs/20261013T000000Z-ab/missing.json"):
        with pytest.raises(RiftError):
            typed.read_result_ref(bad, tmp_path)


async def test_prompts_and_the_session_resource_are_served() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer(
        "context", {"status": "ok", "result": {"document": {"title": "A"}}, "target": {"pid": 1}}
    )
    server = _server(addin)
    prompts = {p.name for p in await server.list_prompts()}
    assert {"change", "model_audit", "pre_issue_check"} <= prompts
    text = await server.get_prompt("pre_issue_check", {"sheets": "A101-A105"})
    assert "A101-A105" in str(text)
    resources = {str(r.uri) for r in await server.list_resources()}
    assert "rift://session/context" in resources
    contents = await server.read_resource("rift://session/context")
    body = json.loads(next(iter(contents)).content)
    assert body["context"] == {"document": {"title": "A"}}
