# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""M4 operations: exports and the document life cycle forwarded as op tool export.* and
document.*, their schemas and risks, and launch_revit."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest
from test_tools import LEASE_OK, FakeAddin, _client

from rift_mcp.errors import RiftError
from rift_mcp.registry import get
from rift_mcp.server import ClientPool, build_server
from rift_mcp.tools import operations


def _payload(out: Any) -> dict[str, Any]:
    if isinstance(out, tuple):
        return out[1] if isinstance(out[1], dict) else json.loads(out[0][0].text)
    return json.loads(out[0].text)


async def test_every_operation_is_registered_with_its_schema_and_risk() -> None:
    tools = {
        t.name: t
        for t in await build_server(ClientPool(lambda _p: _client(FakeAddin()))).list_tools()
    }
    for name, (op, _, _) in operations.OPS.items():
        spec = get(name)
        assert spec.remote_op == f"tool:{op}", name
        assert spec.status == "implemented"
        props = tools[name].inputSchema["properties"]
        assert "pid" in props and "timeout_s" in props
    for name in ("save_document", "close_document", "sync_with_central"):
        assert (
            get(name).risk == "never_auto"
            and "confirmed by the user in Revit" in get(name).description
        )
    for name in ("export_pdf", "export_ifc", "open_document"):
        assert get(name).risk == "ask"
    assert tools["export_pdf"].inputSchema["required"] == ["folder"]
    assert tools["close_document"].inputSchema["required"] == ["document"]


async def test_an_export_forwards_its_inputs_without_the_nones(tmp_path: Path) -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("tool", {"status": "ok", "result": {"format": "pdf", "files": ["A.pdf"]}})
    mcp = build_server(ClientPool(lambda _p: _client(addin)))
    out = _payload(
        await mcp.call_tool(
            "export_pdf", {"folder": str(tmp_path), "views": [12, "A101"], "name": "A"}
        )
    )
    assert out["ok"] is True and out["result"]["files"] == ["A.pdf"]
    call = addin.calls[1]
    assert call["args"]["name"] == "export.pdf"
    assert call["args"]["inputs"] == {
        "folder": str(tmp_path),
        "views": [12, "A101"],
        "name": "A",
        "combine": True,
        "overwrite": False,
    }
    assert call["client"]["tool"] == "export_pdf"


async def test_a_relative_folder_never_reaches_revit() -> None:
    addin = FakeAddin()
    with pytest.raises(RiftError):
        await operations.call_op(_client(addin), "export_dwg", {"folder": "out"})
    assert addin.calls == []


async def test_a_declined_save_is_returned_as_user_declined() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer(
        "tool",
        {
            "status": "refused",
            "error": {"code": "USER_DECLINED", "message": "the user answered no in Revit"},
        },
    )
    mcp = build_server(ClientPool(lambda _p: _client(addin)))
    out = _payload(await mcp.call_tool("save_document", {}))
    assert out["error"]["code"] == "USER_DECLINED"
    assert addin.calls[1]["args"]["name"] == "document.save"


def test_launch_revit_starts_the_newest_or_the_named_version(tmp_path: Path) -> None:
    for year in ("2025", "2026"):
        exe = tmp_path / "Autodesk" / f"Revit {year}" / "Revit.exe"
        exe.parent.mkdir(parents=True)
        exe.write_bytes(b"")
    found = operations.revit_installations([tmp_path])
    assert sorted(found) == ["2025", "2026"]
    started: list[list[str]] = []
    out = operations.launch_revit(
        None, installations=found, start=lambda a: started.append(a) or 77
    )
    assert (out["version"], out["pid"]) == ("2026", 77)
    assert started[0][0].endswith("Revit 2026\\Revit.exe") or started[0][0].endswith(
        "Revit 2026/Revit.exe"
    )
    with pytest.raises(RiftError) as err:
        operations.launch_revit("2019", installations=found, start=lambda a: 1)
    assert err.value.code == "NO_REVIT"
    with pytest.raises(RiftError):
        operations.launch_revit(None, installations={}, start=lambda a: 1)


async def test_baked_commands_are_proposed_listed_and_run_with_the_write_arguments() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("tool", {"status": "ok", "result": {"proposed": "count_walls", "id": "abc"}})
    addin.answer("tool", {"status": "ok", "result": {"command": "tag_rooms", "result": 3}})
    mcp = build_server(ClientPool(lambda _p: _client(addin)))
    out = _payload(
        await mcp.call_tool(
            "propose_baked_tool",
            {"name": "count_walls", "description": "d", "mode": "query", "script": "result = 1"},
        )
    )
    assert out["result"]["proposed"] == "count_walls"
    assert addin.calls[1]["args"]["name"] == "baked.propose"
    refused = _payload(await mcp.call_tool("run_baked_tool", {"name": "tag_rooms"}))
    assert refused["error"]["code"] == "EXPECTED_DOCUMENT_REQUIRED"
    ran = _payload(
        await mcp.call_tool(
            "run_baked_tool",
            {"name": "tag_rooms", "inputs": {"level": "L1"}, "expected_document": "X_SANDBOX"},
        )
    )
    assert ran["result"]["result"] == 3
    args = addin.calls[2]["args"]
    assert args["name"] == "baked.run" and args["dry_run"] is True and args["policy"] == "auto"
    assert args["inputs"] == {"name": "tag_rooms", "inputs": {"level": "L1"}}
    assert get("run_baked_tool").risk == "never_auto"
    assert get("list_baked_tools").risk == "auto"


async def test_csharp_scripts_are_sent_with_their_language_and_skip_the_python_lint() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("compile", {"status": "ok", "result": {"ok": True, "language": "csharp"}})
    addin.answer("run", {"status": "ok", "result": {"outcome": "rolled_back", "result": 1}})
    mcp = build_server(ClientPool(lambda _p: _client(addin)))
    out = _payload(
        await mcp.call_tool(
            "run_modify",
            {"script": "return 1;", "expected_document": "X_SANDBOX", "language": "csharp"},
        )
    )
    assert out.get("ok") is not False, out
    assert addin.calls[1]["args"]["language"] == "csharp"
    assert addin.calls[2]["args"]["language"] == "csharp"
