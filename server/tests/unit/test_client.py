# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Plan test 18: target check, lease and renewal, rotation, transport errors, accepted → job."""

from __future__ import annotations

from dataclasses import replace
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import pytest

from rift_mcp.client import RevitClient
from rift_mcp.discovery import Instance
from rift_mcp.errors import RiftError
from rift_mcp.transport import pipe


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
        capabilities=("context", "lease", "job"),
        path=Path("revit-4242.json"),
    )


def _client(call_fn: Any, **kw: Any) -> RevitClient:
    kw.setdefault("alive_fn", lambda _inst: True)
    kw.setdefault("reload_fn", None)
    return RevitClient(_instance(), b"\0" * 32, call_fn=call_fn, **kw)


class FakeAddin:
    """Scripted add-in: records every op and answers from a queue keyed by op."""

    def __init__(self) -> None:
        self.calls: list[dict[str, Any]] = []
        self.answers: dict[str, list[Any]] = {}
        self.secrets: list[bytes] = []

    def answer(self, op: str, *responses: Any) -> None:
        self.answers.setdefault(op, []).extend(responses)

    def __call__(
        self, name: str, secret: bytes, body: dict[str, Any], *, expected_pid: int | None = None
    ):
        self.calls.append(body)
        self.secrets.append(secret)
        queue = self.answers.get(body["op"])
        if not queue:
            raise AssertionError(f"unexpected op {body['op']}")
        resp = queue.pop(0)
        if isinstance(resp, Exception):
            raise resp
        resp.setdefault("v", "2.0")
        resp.setdefault("id", body["id"])
        return pipe.PipeResponse(resp, b"{}", True, 4242)


def test_lease_is_taken_once_before_the_first_call() -> None:
    addin = FakeAddin()
    addin.answer(
        "lease",
        {
            "status": "ok",
            "result": {"lease_until_utc": "2026-10-09T10:00:00Z", "holder": {"name": "rift-mcp"}},
        },
    )
    addin.answer(
        "context",
        {"status": "ok", "result": {"document": {"title": "Sandbox"}}},
        {"status": "ok", "result": {"document": None}},
    )
    client = _client(addin)

    first = client.call_sync("context")
    second = client.call_sync("context")

    assert [c["op"] for c in addin.calls] == ["lease", "context", "context"]
    assert addin.calls[0]["args"]["client_id"] == client.client_id
    assert first["result"]["document"]["title"] == "Sandbox"
    assert second["result"]["document"] is None
    assert client.holder == {"name": "rift-mcp"}


def test_refusals_and_errors_become_rift_errors_with_details() -> None:
    addin = FakeAddin()
    addin.answer(
        "lease",
        {
            "status": "refused",
            "error": {"code": "LEASE_HELD", "message": "held by X"},
            "result": {"holder": {"name": "X"}},
        },
    )
    client = _client(addin)
    with pytest.raises(RiftError) as info:
        client.call_sync("context")
    assert info.value.code == "LEASE_HELD"
    assert info.value.details == {"holder": {"name": "X"}}
    assert not client.leased


@pytest.mark.parametrize(
    ("raised", "code"),
    [
        (pipe.PipeError("TARGET_BUSY"), "TARGET_BUSY"),
        (pipe.PipeError("TARGET_CHANGED"), "TARGET_CHANGED"),
        (pipe.ProofError("proof"), "TARGET_CHANGED"),
        (FileNotFoundError("no pipe"), "TARGET_LOST"),
    ],
)
def test_transport_failures_are_mapped(raised: Exception, code: str) -> None:
    def boom(*_args: Any, **_kw: Any):
        raise raised

    client = _client(boom)
    with pytest.raises(RiftError) as info:
        client.call_sync("context")
    assert info.value.code == code


def test_accepted_response_is_followed_by_job_polling() -> None:
    addin = FakeAddin()
    addin.answer("lease", {"status": "ok", "result": {"lease_until_utc": "x", "holder": {}}})
    addin.answer("run", {"status": "accepted", "result": {"job_id": "j1", "progress": 10}})
    addin.answer(
        "job",
        {"status": "ok", "result": {"job_id": "j1", "state": "running", "progress": 50}},
        {"status": "ok", "result": {"job_id": "j1", "state": "done", "result": {"value": 7}}},
    )
    client = _client(addin)
    response = client.call_sync("run", {"script": "x"}, timeout_s=30)
    assert response["result"] == {"value": 7}
    assert [c["op"] for c in addin.calls] == ["lease", "run", "job", "job"]
    assert addin.calls[2]["args"]["job_id"] == "j1"


def test_tool_name_travels_in_client_object_for_the_activity_card() -> None:
    addin = FakeAddin()
    addin.answer("lease", {"status": "ok", "result": {"lease_until_utc": "x", "holder": {}}})
    addin.answer("context", {"status": "ok", "result": {}})
    addin.answer("run", {"status": "accepted", "result": {"job_id": "j1"}})
    addin.answer("job", {"status": "ok", "result": {"job_id": "j1", "state": "done", "result": {}}})
    client = _client(addin)
    client.call_sync("context", tool="get_context")
    client.call_sync("run", {"script": "x"}, timeout_s=30, tool="run_query")
    assert "tool" not in addin.calls[0]["client"]
    assert addin.calls[1]["client"]["tool"] == "get_context"
    assert [c["client"].get("tool") for c in addin.calls[2:]] == ["run_query", "run_query"]


LEASE_OK = {"status": "ok", "result": {"lease_until_utc": "x", "holder": {"name": "rift-mcp"}}}


def test_target_is_verified_before_the_first_request() -> None:
    addin = FakeAddin()
    client = _client(addin, alive_fn=lambda _inst: False)
    with pytest.raises(RiftError) as info:
        client.call_sync("context")
    assert info.value.code == "TARGET_CHANGED"
    assert addin.calls == []

    checks: list[int] = []
    addin.answer("lease", dict(LEASE_OK))
    addin.answer("context", {"status": "ok", "result": {}}, {"status": "ok", "result": {}})
    client = _client(addin, alive_fn=lambda inst: checks.append(inst.pid) or True)
    client.call_sync("context")
    client.call_sync("context")
    assert checks == [4242]


def test_envelope_carries_the_lease_identity() -> None:
    addin = FakeAddin()
    addin.answer("lease", dict(LEASE_OK))
    addin.answer("context", {"status": "ok", "result": {}})
    client = _client(addin, client_name="tester", client_version="9.9")
    client.call_sync("context")
    assert addin.calls[1]["client"]["session_id"] == client.client_id
    assert addin.calls[1]["client"]["name"] == "tester"
    assert addin.calls[1]["client"]["version"] == "9.9"


def test_lease_is_taken_again_after_an_idle_period() -> None:
    now = [1000.0]
    addin = FakeAddin()
    addin.answer("lease", dict(LEASE_OK), dict(LEASE_OK))
    addin.answer("context", *({"status": "ok", "result": {}} for _ in range(4)))
    client = _client(addin, clock=lambda: now[0])
    client.call_sync("context")
    now[0] += 45  # each answered request renewed the lease on the add-in side
    client.call_sync("context")
    now[0] += 45
    client.call_sync("context")
    now[0] += 55  # idle past the lease (60 s, re-leased 10 s early)
    client.call_sync("context")
    assert [c["op"] for c in addin.calls] == [
        "lease",
        "context",
        "context",
        "context",
        "lease",
        "context",
    ]


def test_lease_held_on_a_call_drops_the_lease_and_reports_the_holder() -> None:
    addin = FakeAddin()
    addin.answer("lease", dict(LEASE_OK), dict(LEASE_OK))
    addin.answer(
        "context",
        {
            "status": "refused",
            "error": {"code": "LEASE_HELD", "message": "held by other"},
            "result": {"holder": {"name": "other"}},
        },
        {"status": "ok", "result": {}},
    )
    client = _client(addin)
    with pytest.raises(RiftError) as info:
        client.call_sync("context")
    assert info.value.code == "LEASE_HELD"
    assert client.holder == {"name": "other"}
    assert not client.leased
    client.call_sync("context")
    assert [c["op"] for c in addin.calls] == ["lease", "context", "lease", "context"]


def test_paused_forgets_lease_and_does_not_retry() -> None:
    """UI tranche 1, test 21: PAUSED drops the lease, is raised as is, and nothing is sent again."""
    addin = FakeAddin()
    addin.answer("lease", dict(LEASE_OK), dict(LEASE_OK))
    addin.answer(
        "context",
        {"status": "ok", "result": {}},
        {
            "status": "refused",
            "error": {"code": "PAUSED", "message": "paused by the user in Revit"},
            "result": {"paused_since_utc": "2026-10-16T12:00:00.0000000+00:00"},
        },
    )
    client = _client(addin)
    client.call_sync("context")
    assert client.leased
    with pytest.raises(RiftError) as info:
        client.call_sync("context")
    assert info.value.code == "PAUSED"
    assert not client.leased
    assert [c["op"] for c in addin.calls] == ["lease", "context", "context"]


def test_rotation_is_followed_by_a_reload_and_one_retry() -> None:
    new_secret = b"\x07" * 32
    addin = FakeAddin()
    addin.answer("lease", pipe.ProofError("proof"), dict(LEASE_OK))
    addin.answer("context", {"status": "ok", "result": {"ok": 1}})
    reloads: list[int] = []

    def reload(inst: Instance) -> tuple[Instance, bytes]:
        reloads.append(inst.pid)
        return replace(inst, generation=2), new_secret

    client = _client(addin, reload_fn=reload)
    response = client.call_sync("context")
    assert response["result"] == {"ok": 1}
    assert reloads == [4242]
    assert client.generation == 2
    assert client.rotations_seen == 1
    assert addin.secrets == [b"\0" * 32, new_secret, new_secret]
    assert addin.calls[0]["id"] != addin.calls[1]["id"]


@pytest.mark.parametrize(
    ("first", "code"),
    [
        (pipe.ProofError("proof"), "TARGET_CHANGED"),
        ({"status": "error", "error": {"code": "UNAUTHORIZED", "message": "ReplayedNonce"}}, None),
    ],
)
def test_stale_secret_without_rotation_is_not_retried(first: Any, code: str | None) -> None:
    addin = FakeAddin()
    addin.answer("lease", first)
    client = _client(addin, reload_fn=lambda inst: (inst, b"\0" * 32))
    with pytest.raises(RiftError) as info:
        client.call_sync("context")
    assert info.value.code == (code or "UNAUTHORIZED")
    assert len(addin.calls) == 1
    assert client.rotations_seen == 0
