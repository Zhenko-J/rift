# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Frame header encoding/decoding (docs/PROTOCOL.md § Framing).

Mirrors Rift.Core.Transport.FrameHeader on the add-in side.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

from .signing import FRAME_MAGIC, MAX_BODY_BYTES, MAX_HEADER_BYTES

_HEX32 = re.compile(r"^[0-9a-f]{32}$")
_HEX64 = re.compile(r"^[0-9a-f]{64}$")


class FrameError(ValueError):
    """Malformed or oversized header: the connection must be closed."""


@dataclass(frozen=True, slots=True)
class RequestHeader:
    ts: int
    nonce: str
    mac: str
    length: int

    def encode(self) -> bytes:
        line = f"{FRAME_MAGIC} ts={self.ts} nonce={self.nonce} mac={self.mac} len={self.length}\n"
        return line.encode("ascii")


@dataclass(frozen=True, slots=True)
class ResponseHeader:
    proof: str | None  # None for the unsigned health answer ("proof=-")
    length: int


def health_header() -> bytes:
    return f"{FRAME_MAGIC} health len=0\n".encode("ascii")


def parse_response_header(line: bytes) -> ResponseHeader:
    if len(line) > MAX_HEADER_BYTES:
        raise FrameError("header too long")
    try:
        text = line.decode("ascii")
    except UnicodeDecodeError as exc:
        raise FrameError("header is not ASCII") from exc
    parts = text.split(" ")
    if len(parts) != 3 or parts[0] != FRAME_MAGIC:
        raise FrameError(f"bad header: {text!r}")
    fields = {}
    for part in parts[1:]:
        key, sep, value = part.partition("=")
        if not sep:
            raise FrameError(f"bad field: {part!r}")
        fields[key] = value
    if set(fields) != {"proof", "len"}:
        raise FrameError(f"unexpected fields: {sorted(fields)}")
    if not fields["len"].isdigit():
        raise FrameError("bad len")
    length = int(fields["len"])
    if length > MAX_BODY_BYTES:
        raise FrameError("body too large")
    proof = fields["proof"]
    if proof == "-":
        return ResponseHeader(None, length)
    if not _HEX64.match(proof):
        raise FrameError("bad proof")
    return ResponseHeader(proof, length)
