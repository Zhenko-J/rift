# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""M4 operations served by the server: targets (list, pin), schedule export to CSV, the Revit
window tools forwarded as typed reads, and the getting_started prompt."""

from __future__ import annotations

import dataclasses
import json
from pathlib import Path
from typing import Any

import pytest
from test_tools import LEASE_OK, FakeAddin, _client, _instance

from rift_mcp import server as srv
from rift_mcp.errors import RiftError
from rift_mcp.registry import get
from rift_mcp.server import ClientPool, build_server
from rift_mcp.tools import ops

SCHEDULE = {
    "status": "ok",
    "result": {
        "tool": "get_schedule_data",
        "result": {
            "schedule": "Door Schedule: ground",
            "columns": ["Mark", "Width"],
            "rows": [["D1", "900"], ["D2", 'a "quoted", value']],
            "total_rows": 3,
            "truncated": False,
        },
        "run": {"outcome": "ok"},
    },
}


def _payload(out: Any) -> dict[str, Any]:
    if isinstance(out, tuple):
        return out[1] if isinstance(out[1], dict) else json.loads(out[0][0].text)
    return json.loads(out[0].text)


def test_targets_are_listed_with_the_pinned_one_and_a_hint_when_several() -> None:
    a = _instance()
    b = dataclasses.replace(a, pid=5151, path=Path("revit-5151.json"))
    out = ops.list_targets([a, b], None)
    assert [t["pid"] for t in out["targets"]] == [4242, 5151]
    assert "hint" in out
    pinned = ops.list_targets([a, b], 5151)
    assert [t["pinned"] for t in pinned["targets"]] == [False, True]
    assert "warning" in ops.list_targets([a], 5151)
    with pytest.raises(RiftError) as err:
        ops.check_target([a], 7)
    assert err.value.code == "NO_TARGET"


def test_a_pinned_pid_is_used_by_calls_without_a_pid() -> None:
    asked: list[int | None] = []
    addin = FakeAddin()

    def factory(pid: int | None) -> Any:
        asked.append(pid)
        return _client(addin)

    pool = ClientPool(factory)
    pool.pinned = 4242
    pool.get()
    assert asked == [4242]
    pool.get()
    assert asked == [4242]  # cached by pid


async def test_switch_target_pins_the_live_pid_and_returns_its_context(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(srv, "live_instances", lambda: [_instance()])
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("context", {"status": "ok", "result": {"document": {"title": "X_SANDBOX"}}})
    pool = ClientPool(lambda _pid: _client(addin))
    mcp = build_server(pool)
    out = _payload(await mcp.call_tool("switch_target", {"pid": 4242}))
    assert out["ok"] is True and out["pinned"] == 4242
    assert out["context"]["document"]["title"] == "X_SANDBOX"
    assert pool.pinned == 4242
    refused = _payload(await mcp.call_tool("switch_target", {"pid": 9}))
    assert refused["error"]["code"] == "NO_TARGET"
    assert pool.pinned == 4242
    listed = _payload(await mcp.call_tool("list_targets", {}))
    assert listed["targets"][0]["pinned"] is True
    assert _payload(await mcp.call_tool("switch_target", {}))["pinned"] is None
    assert pool.pinned is None


def test_csv_is_quoted_and_uses_crlf() -> None:
    text = ops.to_csv(["a", "b"], [["1", 'x "y", z'], ["2", None]], ";")
    assert text == 'a;b\r\n1;"x ""y"", z"\r\n2;\r\n'
    assert ops.safe_file_name('Door: "ground" ?') == "Door_ _ground_ _"


async def test_export_writes_the_schedule_and_never_overwrites_by_default(tmp_path: Path) -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("tool", SCHEDULE, SCHEDULE)
    client = _client(addin)
    out = await ops.export_schedule_csv(client, "Door Schedule: ground", str(tmp_path))
    assert out["ok"] is True and out["rows"] == 2 and out["columns"] == 2
    path = Path(out["path"])
    assert path.name == "Door Schedule_ ground.csv"
    raw = path.read_bytes()
    assert raw.startswith(b"\xef\xbb\xbf")
    assert raw.decode("utf-8-sig").splitlines()[0] == "Mark,Width"
    assert addin.calls[1]["args"]["inputs"]["limit"] == ops.EXPORT_ROWS
    with pytest.raises(RiftError) as err:
        await ops.export_schedule_csv(client, "Door Schedule: ground", str(tmp_path))
    assert err.value.code == "FILE_EXISTS"


async def test_export_refuses_a_relative_folder_and_an_odd_delimiter() -> None:
    addin = FakeAddin()
    with pytest.raises(RiftError):
        await ops.export_schedule_csv(_client(addin), "S", "relative/folder")
    with pytest.raises(RiftError):
        await ops.export_schedule_csv(_client(addin), "S", str(Path.cwd()), delimiter="|")
    assert addin.calls == []


async def test_window_tools_are_typed_reads_that_ask_first() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer(
        "tool",
        {"status": "ok", "result": {"tool": "select_elements", "result": {"selected": 2}}},
    )
    mcp = build_server(ClientPool(lambda _pid: _client(addin)))
    out = _payload(await mcp.call_tool("select_elements", {"element_ids": [1, 2]}))
    assert out["result"] == {"selected": 2}
    assert addin.calls[1]["args"] == {
        "name": "select_elements",
        "timeout_s": 120,
        "inputs": {"element_ids": [1, 2]},
    }
    for name in ("select_elements", "show_element", "activate_view", "switch_target"):
        spec = get(name)
        assert spec.risk == "ask" and not spec.read_only and not spec.destructive


async def test_getting_started_names_the_safety_rules() -> None:
    mcp = build_server(ClientPool(lambda _pid: _client(FakeAddin())))
    result = await mcp.get_prompt("getting_started", {"focus": "door schedules"})
    text = result.messages[0].content.text  # type: ignore[union-attr]
    assert "SANDBOX" in text and "PAUSED" in text and "untrusted" in text
    assert text.endswith("help with: door schedules.")
