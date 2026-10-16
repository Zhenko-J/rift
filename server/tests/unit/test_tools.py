# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Plan test 19 (run_query part): compile first, never run on a syntax error, READ_ONLY_MODE,
WRITE_IN_QUERY with the run result, accepted → job, and the MCP registration."""

from __future__ import annotations

from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import pytest

from rift_mcp.client import RevitClient
from rift_mcp.discovery import Instance
from rift_mcp.errors import RiftError
from rift_mcp.server import ClientPool, build_server
from rift_mcp.tools import run_query as rq
from rift_mcp.transport import pipe

LEASE_OK = {"status": "ok", "result": {"lease_until_utc": "x", "holder": {"name": "rift-mcp"}}}
COMPILE_OK = {"status": "ok", "result": {"ok": True, "language": "python", "sha256": "ab"}}


def _instance() -> Instance:
    return Instance(
        pid=4242,
        process_start_utc=datetime(2026, 10, 9, tzinfo=UTC),
        pipe="rift-revit-4242",
        revit_version="2026",
        build="26.3.0.37",
        engine="ironpython-3.4",
        addin_version="0.1.0",
        generation=1,
        protocol="2.0",
        capabilities=("context", "lease", "job", "compile", "run"),
        path=Path("revit-4242.json"),
    )


class FakeAddin:
    def __init__(self) -> None:
        self.calls: list[dict[str, Any]] = []
        self.answers: dict[str, list[dict[str, Any]]] = {}

    def answer(self, op: str, *responses: dict[str, Any]) -> None:
        self.answers.setdefault(op, []).extend(responses)

    def __call__(
        self, name: str, secret: bytes, body: dict[str, Any], *, expected_pid: int | None = None
    ) -> pipe.PipeResponse:
        self.calls.append(body)
        queue = self.answers.get(body["op"])
        if not queue:
            raise AssertionError(f"unexpected op {body['op']}")
        resp = dict(queue.pop(0))
        resp.setdefault("v", "2.0")
        resp.setdefault("id", body["id"])
        return pipe.PipeResponse(resp, b"{}", True, 4242)


def _client(addin: FakeAddin) -> RevitClient:
    return RevitClient(
        _instance(), b"\0" * 32, call_fn=addin, alive_fn=lambda _i: True, reload_fn=None
    )


async def test_a_syntax_error_returns_the_line_and_run_is_never_sent() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer(
        "compile",
        {"status": "ok", "result": {"ok": False, "line": 3, "column": 5, "message": "bad"}},
    )
    out = await rq.run_query(_client(addin), rq.RunQueryInput(script="x = (\n"))
    assert out["ok"] is False
    assert out["error"]["code"] == "SCRIPT_ERROR"
    assert out["error"]["details"]["line"] == 3
    assert [c["op"] for c in addin.calls] == ["lease", "compile"]


async def test_any_mode_but_query_is_refused_before_reaching_revit() -> None:
    addin = FakeAddin()
    out = await rq.run_query(_client(addin), rq.RunQueryInput(script="x = 1", mode="modify"))
    assert out["error"]["code"] == "READ_ONLY_MODE"
    assert addin.calls == []


async def test_a_clean_query_sends_query_mode_a_short_pipe_wait_and_the_expected_document() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("compile", COMPILE_OK)
    run_result = {"outcome": "ok", "result": {"walls": 12}, "stdout": ""}
    addin.answer("run", {"status": "ok", "result": run_result, "target": {"pid": 4242}})
    args = rq.RunQueryInput(
        script="result = 12", timeout_s=120, expected_document=r"C:\w\A_SANDBOX.rvt"
    )
    out = await rq.run_query(_client(addin), args)
    assert out["ok"] is True
    assert out["run"]["result"] == {"walls": 12}
    run = addin.calls[2]
    assert run["args"]["mode"] == "query"
    assert run["args"]["timeout_s"] == 120
    assert run["timeout_s"] == rq.PIPE_WAIT_S
    assert run["expected_document"] == {"path": r"C:\w\A_SANDBOX.rvt"}
    assert all(c["client"]["tool"] == "run_query" for c in addin.calls[1:])


async def test_write_in_query_is_an_error_carrying_the_run_result() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("compile", COMPILE_OK)
    details = {"outcome": "rolled_back", "reason": "WRITE_IN_QUERY", "blocked": ["save"]}
    addin.answer(
        "run",
        {
            "status": "error",
            "error": {"code": "WRITE_IN_QUERY", "message": "rolled back"},
            "result": details,
        },
    )
    with pytest.raises(RiftError) as info:
        await rq.run_query(_client(addin), rq.RunQueryInput(script="t.Commit()"))
    assert info.value.code == "WRITE_IN_QUERY"
    assert info.value.details == details


async def test_a_long_run_is_accepted_then_followed_by_job() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("compile", COMPILE_OK)
    addin.answer("run", {"status": "accepted", "result": {"job_id": "j9"}})
    addin.answer(
        "job",
        {"status": "ok", "result": {"job_id": "j9", "state": "running"}},
        {"status": "ok", "result": {"job_id": "j9", "state": "done", "result": {"outcome": "ok"}}},
    )
    out = await rq.run_query(_client(addin), rq.RunQueryInput(script="x", timeout_s=300))
    assert out["run"] == {"outcome": "ok"}
    assert out["job"] == {"job_id": "j9"}
    assert [c["op"] for c in addin.calls] == ["lease", "compile", "run", "job", "job"]


@pytest.mark.parametrize(
    ("value", "expected"),
    [
        (None, None),
        ("Project_SANDBOX", {"title": "Project_SANDBOX"}),
        ("model.rvt", {"path": "model.rvt"}),
        (r"\\server\share\x", {"path": r"\\server\share\x"}),
    ],
)
def test_expected_document_is_a_path_or_a_title(value: str | None, expected: Any) -> None:
    assert rq.expected_document(value) == expected


async def test_run_query_is_registered_with_the_registry_annotations() -> None:
    addin = FakeAddin()

    def factory(_pid: int | None) -> RevitClient:
        return _client(addin)

    server = build_server(ClientPool(factory))
    tools = {t.name: t for t in await server.list_tools()}
    assert "run_query" in tools
    assert tools["run_query"].annotations.readOnlyHint is False
    refused = await server.call_tool("run_query", {"script": "x = 1", "mode": "modify"})
    payload = refused[1] if isinstance(refused, tuple) else refused
    assert "READ_ONLY_MODE" in str(payload)
    assert addin.calls == []
