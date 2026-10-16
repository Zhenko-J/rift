# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Synchronous named-pipe client (ADR-001). One connection per request, as the add-in expects.

Proven by prototype P-D3: ``open(pipe_path(name), 'r+b', buffering=0)``,
GetNamedPipeServerProcessId through ctypes, ERROR_PIPE_BUSY handled with WaitNamedPipe.
The asyncio variant (Proactor create_pipe_connection) comes with the MCP server loop at M1.
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import errno
import json
import msvcrt
import sys
import time
from dataclasses import dataclass
from typing import Any

from . import frames, signing

ERROR_PIPE_BUSY = 231

if sys.platform == "win32":  # pragma: no branch
    _k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    _k32.GetNamedPipeServerProcessId.argtypes = [wt.HANDLE, ctypes.POINTER(wt.DWORD)]
    _k32.GetNamedPipeServerProcessId.restype = wt.BOOL
    _k32.WaitNamedPipeW.argtypes = [wt.LPCWSTR, wt.DWORD]
    _k32.WaitNamedPipeW.restype = wt.BOOL


class PipeError(OSError):
    """Transport failure (busy, closed, malformed frame)."""


class ProofError(PipeError):
    """The response proof did not verify: the peer does not hold the secret."""


@dataclass(frozen=True, slots=True)
class PipeResponse:
    body: dict[str, Any]
    raw: bytes
    proof_verified: bool
    server_pid: int


def pipe_path(name: str) -> str:
    return r"\\.\pipe" + "\\" + name


def _is_busy(exc: OSError) -> bool:
    """ERROR_PIPE_BUSY: the single instance serves another client or is between two connections.

    ``open`` goes through the C runtime, which maps ERROR_PIPE_BUSY to EINVAL without a
    ``winerror``: both forms mean "busy" for a pipe path.
    """
    return getattr(exc, "winerror", None) == ERROR_PIPE_BUSY or (
        getattr(exc, "winerror", None) is None and exc.errno == errno.EINVAL
    )


def _open(path: str, wait_ms: int = 2000):
    deadline = time.monotonic() + wait_ms / 1000
    while True:
        try:
            return open(path, "r+b", buffering=0)  # noqa: SIM115 - caller closes
        except OSError as exc:
            if not _is_busy(exc):
                raise
            if time.monotonic() >= deadline:
                raise PipeError("TARGET_BUSY") from exc
            # WaitNamedPipe returns at once while the server is between DisconnectNamedPipe and
            # ConnectNamedPipe; the short sleep keeps the retry loop from spinning.
            _k32.WaitNamedPipeW(path, 500)
            time.sleep(0.005)


def server_pid(fileobj) -> int:
    pid = wt.DWORD()
    handle = wt.HANDLE(msvcrt.get_osfhandle(fileobj.fileno()))
    if not _k32.GetNamedPipeServerProcessId(handle, ctypes.byref(pid)):
        raise ctypes.WinError(ctypes.get_last_error())
    return pid.value


def _read_exactly(fileobj, n: int) -> bytes:
    buf = bytearray()
    while len(buf) < n:
        chunk = fileobj.read(n - len(buf))
        if not chunk:
            raise PipeError("short read")
        buf += chunk
    return bytes(buf)


def _read_header(fileobj) -> bytes:
    line = bytearray()
    while len(line) <= signing.MAX_HEADER_BYTES:
        b = fileobj.read(1)
        if not b:
            raise PipeError("eof in header")
        if b == b"\n":
            return bytes(line)
        line += b
    raise PipeError("header too long")


def health(name: str) -> PipeResponse:
    """Unsigned health frame: returns the add-in's protocol version and pid."""
    with _open(pipe_path(name)) as f:
        pid = server_pid(f)
        f.write(frames.health_header())
        header = frames.parse_response_header(_read_header(f))
        raw = _read_exactly(f, header.length)
    return PipeResponse(json.loads(raw), raw, False, pid)


def call(
    name: str, secret: bytes, body: dict[str, Any], *, expected_pid: int | None = None
) -> PipeResponse:
    """One signed request/response. Raises ProofError when the response proof does not verify."""
    raw_body = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    if len(raw_body) > signing.MAX_BODY_BYTES:
        raise PipeError("PAYLOAD_TOO_LARGE")
    ts = int(time.time())
    nonce = signing.new_nonce()
    mac = signing.compute_mac(secret, ts, nonce, raw_body)
    header = frames.RequestHeader(ts, nonce, mac, len(raw_body))
    with _open(pipe_path(name)) as f:
        pid = server_pid(f)
        if expected_pid is not None and pid != expected_pid:
            raise PipeError("TARGET_CHANGED")
        f.write(header.encode() + raw_body)
        resp_header = frames.parse_response_header(_read_header(f))
        raw = _read_exactly(f, resp_header.length)
    if resp_header.proof is None or not signing.verify_proof(secret, mac, raw, resp_header.proof):
        raise ProofError("response proof mismatch")
    return PipeResponse(json.loads(raw), raw, True, pid)
