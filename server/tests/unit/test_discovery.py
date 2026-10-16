# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Plan test 16: files listed, malformed ones ignored, secrets read, liveness, target choice."""

from __future__ import annotations

import json
import os
from datetime import UTC, datetime
from pathlib import Path

import pytest

from rift_mcp.discovery import (
    choose_target,
    list_instances,
    live_instances,
    read_secret,
    reload,
    verify_alive,
)
from rift_mcp.errors import RiftError


def _write(directory: Path, pid: int, **extra: object) -> Path:
    data = {
        "pid": pid,
        "process_start_utc": "2026-10-09T08:00:00+00:00",
        "transport": "pipe",
        "pipe": f"rift-revit-{pid}",
        "revit_version": "2026",
        "build": "26.3.0.37",
        "engine": "ironpython-3.4",
        "addin_version": "0.1.0",
        "generation": 1,
        "protocol": "2.0",
        "capabilities": ["context"],
        "written_utc": "2026-10-09T08:00:01+00:00",
    }
    data.update(extra)
    path = directory / f"revit-{pid}.json"
    path.write_text(json.dumps(data), encoding="utf-8")
    return path


def test_list_ignores_malformed_files_and_reads_secret(tmp_path: Path) -> None:
    _write(tmp_path, 100)
    (tmp_path / "revit-101.json").write_text("{not json", encoding="utf-8")
    (tmp_path / "revit-102.json").write_text(json.dumps({"pid": 102}), encoding="utf-8")
    (tmp_path / "revit-100.key").write_bytes(b"\x01" * 32)

    found = list_instances(tmp_path)
    assert [i.pid for i in found] == [100]
    assert found[0].pipe == "rift-revit-100"
    assert found[0].capabilities == ("context",)
    assert read_secret(found[0]) == b"\x01" * 32


def test_missing_or_short_secret_is_target_lost(tmp_path: Path) -> None:
    _write(tmp_path, 200)
    inst = list_instances(tmp_path)[0]
    with pytest.raises(RiftError) as info:
        read_secret(inst)
    assert info.value.code == "TARGET_LOST"
    (tmp_path / "revit-200.key").write_bytes(b"short")
    with pytest.raises(RiftError):
        read_secret(inst)


def test_choose_target_rules(tmp_path: Path) -> None:
    with pytest.raises(RiftError) as none:
        choose_target([], None)
    assert none.value.code == "NO_TARGET"

    _write(tmp_path, 300)
    _write(tmp_path, 301)
    both = list_instances(tmp_path)
    with pytest.raises(RiftError) as amb:
        choose_target(both, None)
    assert amb.value.code == "AMBIGUOUS_TARGET"
    assert sorted(amb.value.details["pids"]) == [300, 301]
    assert choose_target(both, 301).pid == 301
    with pytest.raises(RiftError):
        choose_target(both, 999)


def test_empty_dir_and_missing_dir(tmp_path: Path) -> None:
    assert list_instances(tmp_path / "nope") == []
    assert list_instances(tmp_path) == []


def test_verify_alive_checks_pid_and_creation_time(tmp_path: Path) -> None:
    _write(tmp_path, 400)
    inst = list_instances(tmp_path)[0]
    started = inst.process_start_utc.timestamp()

    assert verify_alive(inst, create_time=lambda pid: started + 0.8)
    assert verify_alive(inst, create_time=lambda pid: started - 1.9)
    # Same pid, other creation time: the pid was reused by another program after a crash.
    assert not verify_alive(inst, create_time=lambda pid: started + 3600)
    assert not verify_alive(inst, create_time=lambda pid: None)


def test_verify_alive_against_the_real_process_table(tmp_path: Path) -> None:
    import psutil

    me = psutil.Process(os.getpid())
    start = datetime.fromtimestamp(me.create_time(), UTC).isoformat()
    _write(tmp_path, os.getpid(), process_start_utc=start)
    _write(tmp_path, os.getpid() + 1_000_000, process_start_utc=start)  # no such pid
    found = {i.pid: i for i in list_instances(tmp_path)}
    assert verify_alive(found[os.getpid()])
    assert not verify_alive(found[os.getpid() + 1_000_000])
    assert [i.pid for i in live_instances(tmp_path)] == [os.getpid()]


def test_live_instances_feed_choose_target(tmp_path: Path) -> None:
    _write(tmp_path, 500)
    _write(tmp_path, 501)
    alive = live_instances(tmp_path, alive=lambda i: i.pid == 501)
    assert choose_target(alive, None).pid == 501
    with pytest.raises(RiftError) as info:
        choose_target(live_instances(tmp_path, alive=lambda i: False), None)
    assert info.value.code == "NO_TARGET"


def test_reload_follows_the_generation_and_rejects_another_process(tmp_path: Path) -> None:
    _write(tmp_path, 600)
    inst = list_instances(tmp_path)[0]
    _write(tmp_path, 600, generation=2)
    fresh = reload(inst)
    assert fresh is not None and fresh.generation == 2
    _write(tmp_path, 600, process_start_utc="2026-10-10T08:00:00+00:00")
    assert reload(inst) is None
    (tmp_path / "revit-600.json").unlink()
    assert reload(inst) is None
