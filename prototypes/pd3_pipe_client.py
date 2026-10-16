# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""P-D3 — the real CPython client against the PipeProbe server (ADR-001/002).

Checks: open(r'\\.\pipe\<name>', 'r+b'), GetNamedPipeServerProcessId, health frame, signed request with
proof verification, UTF-8 body, 1 MiB body, ERROR_PIPE_BUSY handling, invalid mac → UNAUTHORIZED.
Usage: python prototypes/pd3_pipe_client.py <pipeName> <secretHex> [expected_pid]
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import hashlib
import hmac
import json
import msvcrt
import os
import secrets
import sys
import time

MAGIC = b"RIFT/1"
ERROR_PIPE_BUSY = 231
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.GetNamedPipeServerProcessId.argtypes = [wt.HANDLE, ctypes.POINTER(wt.DWORD)]
k32.GetNamedPipeServerProcessId.restype = wt.BOOL
k32.WaitNamedPipeW.argtypes = [wt.LPCWSTR, wt.DWORD]
k32.WaitNamedPipeW.restype = wt.BOOL


def open_pipe(path: str, wait_ms: int = 2000):
    deadline = time.time() + wait_ms / 1000
    while True:
        try:
            return open(path, "r+b", buffering=0)
        except OSError as ex:
            if getattr(ex, "winerror", None) == ERROR_PIPE_BUSY and time.time() < deadline:
                k32.WaitNamedPipeW(path, 500)
                continue
            raise


def server_pid(f) -> int:
    pid = wt.DWORD()
    h = wt.HANDLE(msvcrt.get_osfhandle(f.fileno()))
    if not k32.GetNamedPipeServerProcessId(h, ctypes.byref(pid)):
        raise ctypes.WinError(ctypes.get_last_error())
    return pid.value


def read_exactly(f, n: int) -> bytes:
    buf = bytearray()
    while len(buf) < n:
        chunk = f.read(n - len(buf))
        if not chunk:
            raise IOError("short read")
        buf += chunk
    return bytes(buf)


def read_header(f) -> dict:
    line = bytearray()
    while len(line) < 256:
        b = f.read(1)
        if not b:
            raise IOError("eof in header")
        if b == b"\n":
            break
        line += b
    parts = line.split(b" ")
    assert parts[0] == MAGIC, line
    fields = {}
    for p in parts[1:]:
        k, _, v = p.partition(b"=")
        fields[k.decode()] = v.decode()
    return fields


def mac_for(secret: bytes, ts: int, nonce: str, body: bytes) -> str:
    msg = f"RIFT/1|{ts}|{nonce}|{hashlib.sha256(body).hexdigest()}".encode("ascii")
    return hmac.new(secret, msg, hashlib.sha256).hexdigest()


def proof_for(secret: bytes, mac: str, body: bytes) -> str:
    return hmac.new(secret, f"{mac}|{hashlib.sha256(body).hexdigest()}".encode("ascii"), hashlib.sha256).hexdigest()


def call(path: str, secret: bytes, body: bytes, *, bad_mac: bool = False) -> tuple[dict, bytes]:
    ts = int(time.time())
    nonce = secrets.token_hex(16)
    mac = mac_for(secret, ts, nonce, body)
    if bad_mac:
        mac = "0" * 64
    with open_pipe(path) as f:
        f.write(f"RIFT/1 ts={ts} nonce={nonce} mac={mac} len={len(body)}\n".encode("ascii") + body)
        hdr = read_header(f)
        resp = read_exactly(f, int(hdr["len"]))
    if hdr.get("proof") != "-":
        assert hmac.compare_digest(hdr["proof"], proof_for(secret, mac, resp)), "proof mismatch"
    return hdr, resp


def main() -> int:
    name, secret_hex = sys.argv[1], sys.argv[2]
    expected_pid = int(sys.argv[3]) if len(sys.argv) > 3 else None
    path = r"\\.\pipe\\" + name
    secret = bytes.fromhex(secret_hex)
    results = {}

    with open_pipe(path) as f:
        pid = server_pid(f)
        results["server_pid"] = pid
        results["pid_matches"] = (expected_pid is None) or (pid == expected_pid)
        f.write(b"RIFT/1 health len=0\n")
        hdr = read_header(f)
        health = json.loads(read_exactly(f, int(hdr["len"])))
        results["health"] = health

    body = json.dumps({"v": "2.0", "id": "r1", "op": "context", "args": {"text": "élévation 😀"}}, ensure_ascii=False).encode()
    hdr, resp = call(path, secret, body)
    r1 = json.loads(resp)
    results["signed_ok"] = r1["status"] == "ok" and r1["result"]["utf8_ok"] is True

    big = json.dumps({"v": "2.0", "id": "r2", "op": "run", "args": {"blob": "x" * (1024 * 1024 - 200)}}).encode()
    t0 = time.time()
    hdr, resp = call(path, secret, big)
    results["one_mib_ok"] = json.loads(resp)["result"]["body_bytes"] == len(big)
    results["one_mib_ms"] = int((time.time() - t0) * 1000)

    hdr, resp = call(path, secret, body, bad_mac=True)
    results["bad_mac_rejected"] = json.loads(resp)["status"] == "error" and json.loads(resp)["error"]["code"] == "UNAUTHORIZED"

    call(path, secret, json.dumps({"v": "2.0", "id": "r9", "op": "quit"}).encode())
    results["P-D3"] = "ok" if all(v is True for k, v in results.items() if k.endswith("_ok") or k.endswith("_rejected") or k == "pid_matches") else "FAIL"
    print(json.dumps(results, ensure_ascii=False, indent=2))
    return 0 if results["P-D3"] == "ok" else 1


if __name__ == "__main__":
    sys.exit(main())
