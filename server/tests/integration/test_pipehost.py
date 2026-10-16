# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Plan test 21: the real CPython client against the real PipeListener (PipeHost).

PipeHost runs the add-in's listener, authenticator (window, nonces, rotation), secret store,
discovery file and lease with a fake Revit API thread. The client finds it through the discovery
file and checks its pid and creation time with psutil, exactly as against Revit.

Skipped unless the PipeHost executable exists (``dotnet build addin/rift.sln -c "Release R26"``)
or ``RIFT_PIPEHOST`` points to it.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from collections.abc import Iterator
from pathlib import Path
from typing import Any

import pytest

from rift_mcp.client import RevitClient
from rift_mcp.discovery import Instance, list_instances, live_instances, read_secret
from rift_mcp.errors import RiftError
from rift_mcp.transport import frames, pipe, signing

ROOT = Path(__file__).resolve().parents[3]
PIPEHOST_BIN = ROOT / "addin" / "tests" / "Rift.PipeHost" / "bin"
CANDIDATES = [
    Path(os.environ["RIFT_PIPEHOST"]) if os.environ.get("RIFT_PIPEHOST") else None,
    PIPEHOST_BIN / "Release R26" / "net8.0-windows" / "Rift.PipeHost.exe",
    PIPEHOST_BIN / "Release" / "net8.0-windows" / "Rift.PipeHost.exe",
]
EXE = next((p for p in CANDIDATES if p is not None and p.is_file()), None)

pytestmark = [
    pytest.mark.skipif(sys.platform != "win32", reason="named pipes"),
    pytest.mark.skipif(EXE is None, reason="PipeHost not built"),
]


@pytest.fixture
def host(tmp_path: Path) -> Iterator[dict[str, Any]]:
    proc = subprocess.Popen(  # noqa: S603 - our own build output
        [str(EXE), "30", str(tmp_path)], stdout=subprocess.PIPE, text=True, encoding="utf-8"
    )
    info: dict[str, Any] = {"dir": tmp_path}
    assert proc.stdout is not None
    while True:
        line = proc.stdout.readline().strip()
        assert line, "PipeHost exited before READY"
        if line == "READY":
            break
        key, _, value = line.partition(" ")
        info[key] = value
    try:
        yield info
    finally:
        proc.kill()
        proc.wait(5)


def _target(host: dict[str, Any]) -> Instance:
    found = live_instances(host["dir"])
    assert [i.pid for i in found] == [int(host["PID"])]
    return found[0]


def _client(host: dict[str, Any], name: str) -> RevitClient:
    inst = _target(host)
    return RevitClient(inst, read_secret(inst), client_name=name)


def _exchange(name: str, header: bytes, body: bytes) -> tuple[frames.ResponseHeader, bytes]:
    """One raw frame on the pipe, whatever its signature."""
    with pipe._open(pipe.pipe_path(name)) as f:
        f.write(header + body)
        resp = frames.parse_response_header(pipe._read_header(f))
        return resp, pipe._read_exactly(f, resp.length)


def _frame(
    secret: bytes, body: dict[str, Any], *, ts: int | None = None, nonce: str | None = None
) -> tuple[bytes, bytes, str]:
    raw = json.dumps(body).encode()
    ts = int(time.time()) if ts is None else ts
    nonce = nonce or signing.new_nonce()
    mac = signing.compute_mac(secret, ts, nonce, raw)
    return frames.RequestHeader(ts, nonce, mac, len(raw)).encode(), raw, mac


def _stats(client: RevitClient) -> int:
    return client.call_sync("stats")["result"]["context_runs"]


def test_health_lease_context_second_client_and_oversize(host: dict[str, Any]) -> None:
    health = pipe.health(host["PIPE"])
    assert health.body == {"v": "2.0", "pid": int(host["PID"])}
    assert health.server_pid == int(host["PID"])

    first = _client(host, "pytest-a")
    ctx = first.call_sync("context", {"text": "élévation 😀"})
    assert ctx["status"] == "ok"
    assert ctx["result"]["document"]["title"] == "FAKE_SANDBOX"
    assert ctx["result"]["echo"] == {"text": "élévation 😀"}
    assert ctx["target"]["pid"] == int(host["PID"])
    assert ctx["target"]["generation"] == 1
    assert first.holder["name"] == "pytest-a"

    second = _client(host, "pytest-b")
    with pytest.raises(RiftError) as held:
        second.call_sync("context")
    assert held.value.code == "LEASE_HELD"
    assert held.value.details["holder"]["name"] == "pytest-a"
    assert held.value.details["holder"]["since_utc"]
    assert second.holder["name"] == "pytest-a"

    # The holder keeps working, and only its own context ran.
    assert _stats(first) == 1

    with pytest.raises(pipe.PipeError):
        pipe.call(host["PIPE"], first._secret, {"blob": "x" * (1024 * 1024 + 10)})


def test_unsigned_and_forged_requests_are_refused_without_execution(
    host: dict[str, Any],
) -> None:
    secret = bytes.fromhex(host["SECRET"])
    body = json.dumps({"v": "2.0", "id": "u", "op": "context"}).encode()

    # Unsigned: a request header whose mac is not the HMAC of anything we know.
    unsigned = frames.RequestHeader(int(time.time()), "0" * 32, "0" * 64, len(body)).encode()
    resp, raw = _exchange(host["PIPE"], unsigned, body)
    answer = json.loads(raw)
    assert answer["status"] == "error"
    assert (answer["error"]["code"], answer["error"]["message"]) == ("UNAUTHORIZED", "BadMac")
    assert resp.proof is not None and signing.verify_proof(secret, "0" * 64, raw, resp.proof)

    # Signed with a wrong secret through the real client: refused, never executed.
    forged = RevitClient(_target(host), b"\x01" * 32, client_name="intruder", reload_fn=None)
    with pytest.raises(RiftError) as info:
        forged.call_sync("context")
    # The refusal is signed with the real secret, which the intruder cannot verify.
    assert info.value.code == "TARGET_CHANGED"

    # A malformed header closes the connection without an answer.
    with pipe._open(pipe.pipe_path(host["PIPE"])) as f:
        f.write(b"HELLO\n")
        try:
            answer_byte = f.read(1)
        except OSError:  # the server already closed its end: broken pipe
            answer_byte = b""
        assert answer_byte == b""

    assert _stats(_client(host, "pytest-a")) == 0


def test_replayed_nonce_and_out_of_window_timestamp(host: dict[str, Any]) -> None:
    secret = bytes.fromhex(host["SECRET"])
    header, raw, mac = _frame(secret, {"v": "2.0", "id": "r", "op": "context"})

    _, first = _exchange(host["PIPE"], header, raw)
    assert json.loads(first)["status"] == "ok"
    resp, replay = _exchange(host["PIPE"], header, raw)
    assert json.loads(replay)["error"]["code"] == "UNAUTHORIZED"
    assert json.loads(replay)["error"]["message"] == "ReplayedNonce"
    assert signing.verify_proof(secret, mac, replay, resp.proof)

    for skew in (-121, 121, -3600):
        header, raw, _ = _frame(
            secret, {"v": "2.0", "id": "t", "op": "context"}, ts=int(time.time()) + skew
        )
        _, old = _exchange(host["PIPE"], header, raw)
        error = json.loads(old)["error"]
        assert (error["code"], error["message"]) == ("UNAUTHORIZED", "BadTimestamp")

    # Only the first, genuine frame ran.
    assert _stats(_client(host, "pytest-a")) == 1


def test_ten_failures_rotate_the_secret_and_the_client_follows(host: dict[str, Any]) -> None:
    client = _client(host, "pytest-a")
    assert client.call_sync("context")["target"]["generation"] == 1
    old_secret = read_secret(_target(host))

    body = json.dumps({"v": "2.0", "id": "x", "op": "context"}).encode()
    for _ in range(10):
        bad = frames.RequestHeader(int(time.time()), signing.new_nonce(), "f" * 64, len(body))
        _exchange(host["PIPE"], bad.encode(), body)

    rotated = list_instances(host["dir"])[0]
    assert rotated.generation == 2
    assert read_secret(rotated) != old_secret

    # The client still signs with the old secret: refused, re-reads discovery + key, retries once.
    response = client.call_sync("context")
    assert response["status"] == "ok"
    assert response["target"]["generation"] == 2
    assert client.generation == 2
    assert client.rotations_seen == 1
    assert client.holder["name"] == "pytest-a"  # the lease survived the rotation


async def test_run_query_end_to_end_with_the_real_engine(host: dict[str, Any]) -> None:
    """Plan test 19 against the real listener, router, IronPython engine, journal (fake guard)."""
    from rift_mcp.tools import run_query as rq

    client = _client(host, "pytest-a")

    bad = await rq.run_query(client, rq.RunQueryInput(script="x = 1\nif (y := 2):\n    pass\n"))
    assert bad["error"]["code"] == "SCRIPT_ERROR"
    assert bad["error"]["details"]["line"] == 2

    ok = await rq.run_query(
        client,
        rq.RunQueryInput(
            script="print('hi')\nresult = {'t': doc.Title, 'n': inputs['n']}", inputs={"n": 3}
        ),
    )
    assert ok["ok"] is True
    assert ok["run"]["outcome"] == "ok"
    assert ok["run"]["result"] == {"t": "FAKE_SANDBOX", "n": 3}
    assert ok["run"]["stdout"] == "hi\n"

    with pytest.raises(RiftError) as write:
        await rq.run_query(client, rq.RunQueryInput(script="doc.Commit()\ndoc.Save()\n"))
    assert write.value.code == "WRITE_IN_QUERY"
    assert write.value.details["outcome"] == "rolled_back"
    assert write.value.details["blocked"] == ["save"]

    with pytest.raises(RiftError) as clr:
        await rq.run_query(client, rq.RunQueryInput(script="import clr\n"))
    assert clr.value.code == "SCRIPT_ERROR"
    assert "import:clr" in clr.value.details["blocked"]

    with pytest.raises(RiftError) as loop:
        await rq.run_query(client, rq.RunQueryInput(script="while True:\n    pass\n", timeout_s=1))
    assert loop.value.code == "TIMEOUT"
    assert loop.value.details["outcome"] == "timeout"


async def test_audit_template_write_end_to_end_keeps_the_number_text(
    host: dict[str, Any], tmp_path: Path
) -> None:
    """Plan test 19 (audit part) through the real pipe: op tool, preset under the read guard,
    collector, sanitiser, files written by the server (PipeHost stand-in for p1)."""
    from rift_mcp.tools import audit

    client = _client(host, "pytest-a")
    out = await audit.audit_write(client, audit.AuditInput(preset="p1"), str(tmp_path))
    assert out["ok"] is True, out
    path = tmp_path / "FAKE_SANDBOX" / "01_vues_lignes_cartouches.json"
    assert out["written"] == [str(path)]
    text = path.read_text(encoding="utf-8")
    assert '"width_mm": 594.0' in text
    assert '"type": "A1"' in text
    assert out["run"]["outcome"] == "ok"

    with pytest.raises(RiftError) as unknown:
        await client.call("tool", {"name": "audit.p9"}, timeout_s=30)
    assert unknown.value.code == "BAD_REQUEST"


async def test_audit_all_end_to_end_runs_one_job_and_merges_through_the_collector(
    host: dict[str, Any], tmp_path: Path
) -> None:
    """Plan test 19 (all part) through the real pipe: audit.all chains the PipeHost stand-ins
    (p1, then the merge p6 reading p1 from the collector) in one job and writes 00_TOUT.json."""
    from rift_mcp.tools import audit

    client = _client(host, "pytest-a")
    out = await audit.audit_write(client, audit.AuditInput(preset="all"), str(tmp_path))
    assert out["ok"] is True, out
    assert list(out["runs"]) == ["audit.p1", "audit.p6"]
    assert set(out["timing"]["presets"]) == {"audit.p1", "audit.p6"}
    folder = tmp_path / "FAKE_SANDBOX"
    assert [Path(p).name for p in out["written"]] == [
        "00_TOUT.json",
        "01_vues_lignes_cartouches.json",
    ]
    merged = json.loads((folder / "00_TOUT.json").read_text(encoding="utf-8"))
    assert merged["resume"] == {"cartouches": 1}
    assert merged["data"]["cartouches"][0]["width_mm"] == 594.0
    assert '"width_mm": 594.0' in (folder / "00_TOUT.json").read_text(encoding="utf-8")
    assert out["summary"]["00_TOUT"] == {"cartouches": 1}


def test_a_run_longer_than_the_pipe_wait_is_accepted_then_finished_by_job(
    host: dict[str, Any],
) -> None:
    client = _client(host, "pytest-a")
    response = client.call_sync(
        "run",
        {"script": "import time\ntime.sleep(2)\nresult = 'late'", "timeout_s": 30},
        timeout_s=60,
        wait_s=1,
    )
    assert response["result"]["result"] == "late"
    assert response["job"]["job_id"]


async def test_run_modify_end_to_end_dry_run_then_committed_then_history(
    host: dict[str, Any],
) -> None:
    """M2 plan test 10 through the real pipe: mode modify, policy, write guard stand-in,
    receipt, history; then record_change and get_change_records."""
    from rift_mcp.tools import history
    from rift_mcp.tools import run_modify as rm

    client = _client(host, "pytest-a")
    script = "doc.Commit()\ndoc.Commit()\nresult = 'two'\n"

    with pytest.raises(RiftError) as wrong:
        await rm.run_modify(client, rm.RunModifyInput(script=script, expected_document="OTHER"))
    assert wrong.value.code == "DOCUMENT_MISMATCH"

    dry = await rm.run_modify(
        client, rm.RunModifyInput(script=script, expected_document="FAKE_SANDBOX", title="t1")
    )
    assert (dry["outcome"], dry["reason"]) == ("rolled_back", "dry_run")
    assert dry["changes"]["modified"] == 2
    assert dry["changes"]["by_category"]["Views"]["modified"] == 2

    kept = await rm.run_modify(
        client,
        rm.RunModifyInput(
            script=script, expected_document="FAKE_SANDBOX", title="t2", dry_run=False
        ),
    )
    assert kept["outcome"] == "committed"
    assert kept["run"]["policy_applied"] == "auto"

    rec = await history.record_change(client, "user asked for it")
    assert rec["record"]["history_id"] == kept["history_id"]
    listed = await history.get_change_records(client, 10)
    assert [r["history_id"] for r in listed["records"]] == [kept["history_id"], dry["history_id"]]
    assert listed["records"][0]["reason_given"] == "user asked for it"


def test_cancel_job_end_to_end_rolls_back_a_running_modify(host: dict[str, Any]) -> None:
    client = _client(host, "pytest-a")
    accepted = client._check(  # noqa: SLF001 - drive the raw protocol to get the job id
        client._raw_call(  # noqa: SLF001
            client._envelope(  # noqa: SLF001
                "run",
                {
                    "script": "doc.Commit()\nwhile True:\n    pass\n",
                    "mode": "modify",
                    "dry_run": False,
                    "timeout_s": 60,
                },
                1,
                "run_modify",
                {"title": "FAKE_SANDBOX"},
            )
        )
    )
    assert accepted["status"] == "accepted"
    job_id = accepted["result"]["job_id"]
    cancel = client.call_sync("job", {"job_id": job_id, "cancel": True}, timeout_s=15)
    assert cancel["result"]["cancel"] == "requested"
    with pytest.raises(RiftError) as done:
        client.call_sync("job", {"job_id": job_id, "wait_s": 20}, timeout_s=30)
    assert done.value.code == "CANCELLED"
    assert done.value.details["outcome"] == "rolled_back"


@pytest.fixture
def paused_host(tmp_path: Path) -> Iterator[dict[str, Any]]:
    env = dict(os.environ, RIFT_PIPEHOST_PAUSED="1")
    proc = subprocess.Popen(  # noqa: S603 - our own build output
        [str(EXE), "30", str(tmp_path)],
        stdout=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        env=env,
    )
    info: dict[str, Any] = {"dir": tmp_path}
    assert proc.stdout is not None
    while True:
        line = proc.stdout.readline().strip()
        assert line, "PipeHost exited before READY"
        if line == "READY":
            break
        key, _, value = line.partition(" ")
        info[key] = value
    try:
        yield info
    finally:
        proc.kill()
        proc.wait(5)


def test_paused_host_refuses(paused_host: dict[str, Any]) -> None:
    """UI tranche 1, test 22: a host paused in Revit refuses every call, the lease included."""
    client = _client(paused_host, "pytest-paused")
    for op in ("context", "stats"):
        with pytest.raises(RiftError) as info:
            client.call_sync(op)
        assert info.value.code == "PAUSED"
        assert info.value.details is not None
        assert info.value.details["paused_since_utc"]
        assert not client.leased
