# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
import json
from pathlib import Path

import pytest

from rift_mcp.transport import frames, signing

VECTORS = Path(__file__).resolve().parents[3] / "shared" / "schemas" / "test-vectors" / "hmac.json"


def test_shared_vectors_match() -> None:
    data = json.loads(VECTORS.read_text(encoding="utf-8"))
    for v in data["vectors"]:
        secret = bytes.fromhex(v["secret_hex"])
        body = v["body"].encode("utf-8")
        assert signing.sha256_hex(body) == v["body_sha256"]
        mac = signing.compute_mac(secret, v["ts"], v["nonce"], body)
        assert mac == v["mac"]
        response = v["response_body"].encode("utf-8")
        assert signing.compute_proof(secret, mac, response) == v["proof"]
        assert signing.verify_proof(secret, mac, response, v["proof"])
        assert not signing.verify_proof(secret, mac, response + b" ", v["proof"])


def test_request_header_round_trip_format() -> None:
    h = frames.RequestHeader(1_700_000_000, "0" * 32, "a" * 64, 42)
    assert (
        h.encode()
        == b"RIFT/1 ts=1700000000 nonce=" + b"0" * 32 + b" mac=" + b"a" * 64 + b" len=42\n"
    )
    assert frames.health_header() == b"RIFT/1 health len=0\n"


@pytest.mark.parametrize(
    "line",
    [
        b"",
        b"RIFT/2 proof=- len=0",
        b"RIFT/1 proof=- len=1048577",
        b"RIFT/1 proof=zz len=1",
        b"RIFT/1 proof=- len=1 extra=1",
        b"RIFT/1 ts=1 nonce=" + b"0" * 32 + b" mac=" + b"a" * 64 + b" len=1",
        b"RIFT/1 proof=- len=0" + b" " * 300,
    ],
)
def test_malformed_response_headers(line: bytes) -> None:
    with pytest.raises(frames.FrameError):
        frames.parse_response_header(line)


def test_response_headers_parse() -> None:
    assert frames.parse_response_header(b"RIFT/1 proof=- len=12") == frames.ResponseHeader(None, 12)
    assert frames.parse_response_header(
        b"RIFT/1 proof=" + b"b" * 64 + b" len=0"
    ) == frames.ResponseHeader("b" * 64, 0)
