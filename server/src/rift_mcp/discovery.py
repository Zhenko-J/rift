# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Find the Revit instances served by the add-in (ADR-003).

Each running add-in writes ``%LOCALAPPDATA%\\rift\\instances\\revit-<pid>.json``
(shareable metadata) next to ``revit-<pid>.key`` (the secret, never logged).
A record is trusted only when its pid is alive **and** that process was created at
``process_start_utc`` (``psutil``, ±2 s): a pid reused by another program after a Revit
crash is not a target (ADR-002).
"""

from __future__ import annotations

import json
import os
from collections.abc import Callable
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

import psutil

from .errors import RiftError

#: Tolerance between the add-in's ``Process.StartTime`` and psutil's ``create_time``.
START_TOLERANCE_S = 2.0


def data_root() -> Path:
    """The add-in's data folder: ``%LOCALAPPDATA%\\rift`` (or ``RIFT_HOME``)."""
    env = os.environ.get("RIFT_HOME")
    return Path(env) if env else Path(os.environ.get("LOCALAPPDATA", Path.home())) / "rift"


def instances_dir(root: Path | None = None) -> Path:
    if root is not None:
        return root
    return data_root() / "instances"


@dataclass(frozen=True, slots=True)
class Instance:
    pid: int
    process_start_utc: datetime
    pipe: str
    revit_version: str
    build: str
    engine: str
    addin_version: str
    generation: int
    protocol: str
    capabilities: tuple[str, ...]
    path: Path

    @property
    def key_path(self) -> Path:
        return self.path.with_suffix(".key")


def _parse(path: Path) -> Instance | None:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        return Instance(
            pid=int(data["pid"]),
            process_start_utc=datetime.fromisoformat(data["process_start_utc"]),
            pipe=str(data["pipe"]),
            revit_version=str(data.get("revit_version", "?")),
            build=str(data.get("build", "?")),
            engine=str(data.get("engine", "?")),
            addin_version=str(data.get("addin_version", "?")),
            generation=int(data.get("generation", 0)),
            protocol=str(data.get("protocol", "?")),
            capabilities=tuple(data.get("capabilities", ())),
            path=path,
        )
    except (OSError, ValueError, KeyError, TypeError):
        return None


def list_instances(root: Path | None = None) -> list[Instance]:
    """All parseable discovery files, newest pid file first. Malformed files are ignored."""
    directory = instances_dir(root)
    if not directory.is_dir():
        return []
    found = [
        inst for p in sorted(directory.glob("revit-*.json")) if (inst := _parse(p)) is not None
    ]
    found.sort(key=lambda i: i.path.stat().st_mtime, reverse=True)
    return found


def process_create_time(pid: int) -> float | None:
    """Creation time (Unix seconds) of ``pid``; None when no such process may be inspected."""
    try:
        return psutil.Process(pid).create_time()
    except (psutil.NoSuchProcess, psutil.AccessDenied, psutil.ZombieProcess, ValueError):
        return None


def verify_alive(
    inst: Instance, *, create_time: Callable[[int], float | None] = process_create_time
) -> bool:
    """True when ``inst.pid`` runs and was created at ``inst.process_start_utc`` (±2 s)."""
    created = create_time(inst.pid)
    if created is None:
        return False
    return abs(created - inst.process_start_utc.timestamp()) <= START_TOLERANCE_S


def live_instances(
    root: Path | None = None, *, alive: Callable[[Instance], bool] = verify_alive
) -> list[Instance]:
    """The discovery records whose process is the one that wrote them."""
    return [inst for inst in list_instances(root) if alive(inst)]


def reload(inst: Instance) -> Instance | None:
    """Re-reads the discovery file of ``inst`` (after a secret rotation the generation changed)."""
    fresh = _parse(inst.path)
    if fresh is None or fresh.pid != inst.pid or fresh.process_start_utc != inst.process_start_utc:
        return None
    return fresh


def read_secret(inst: Instance) -> bytes:
    try:
        secret = inst.key_path.read_bytes()
    except OSError as exc:
        raise RiftError("TARGET_LOST", f"secret file unreadable for pid {inst.pid}") from exc
    if len(secret) != 32:
        raise RiftError("TARGET_LOST", f"secret file has {len(secret)} bytes, expected 32")
    return secret


def choose_target(instances: list[Instance], pid: int | None = None) -> Instance:
    if pid is not None:
        for inst in instances:
            if inst.pid == pid:
                return inst
        raise RiftError("NO_TARGET", f"no RIFT instance with pid {pid}")
    if not instances:
        raise RiftError("NO_TARGET", "no running Revit with the RIFT add-in was found")
    if len(instances) > 1:
        # Two live records cannot be told apart without a pid: refuse rather than guess.
        raise RiftError(
            "AMBIGUOUS_TARGET",
            "several Revit instances run the add-in; pass pid",
            {"pids": [i.pid for i in instances]},
        )
    return instances[0]
