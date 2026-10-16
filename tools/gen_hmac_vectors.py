# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Generates shared/schemas/test-vectors/hmac.json — the reference vectors for the signing scheme
of ADR-002 / docs/PROTOCOL.md, checked by both the xUnit suite (add-in) and the pytest suite (server).

Run from the repository root:  uv run --project server python tools/gen_hmac_vectors.py
Deterministic: secrets and nonces are fixed, so the file only changes when the scheme changes.
"""

from __future__ import annotations

import hashlib
import hmac
import json
from pathlib import Path

MAGIC = "RIFT/1"


def mac(secret: bytes, ts: int, nonce: str, body: bytes) -> tuple[str, str]:
    body_hash = hashlib.sha256(body).hexdigest()
    msg = f"{MAGIC}|{ts}|{nonce}|{body_hash}".encode("ascii")
    return body_hash, hmac.new(secret, msg, hashlib.sha256).hexdigest()


def proof(secret: bytes, request_mac: str, response_body: bytes) -> str:
    msg = f"{request_mac}|{hashlib.sha256(response_body).hexdigest()}".encode("ascii")
    return hmac.new(secret, msg, hashlib.sha256).hexdigest()


def main() -> None:
    cases = [
        ("empty body", bytes(range(32)), 1_700_000_000, "00" * 16, b"", b'{"v":"2.0","pid":1}'),
        (
            "context request",
            bytes.fromhex("a3" * 32),
            1_760_000_000,
            "0123456789abcdef0123456789abcdef",
            b'{"v":"2.0","id":"r1","op":"context","timeout_s":30,"args":{}}',
            b'{"v":"2.0","id":"r1","status":"ok","result":{"document":{"title":"Sample"}}}',
        ),
        (
            "utf-8 body",
            bytes.fromhex("ff" * 32),
            1_760_000_123,
            "fedcba9876543210fedcba9876543210",
            '{"v":"2.0","id":"r2","op":"run","args":{"title":"élévation \U0001f600"}}'.encode(),
            '{"v":"2.0","id":"r2","status":"error","error":{"code":"PAUSED","message":"en pause"}}'.encode(),
        ),
    ]
    vectors = []
    for name, secret, ts, nonce, body, response in cases:
        body_hash, m = mac(secret, ts, nonce, body)
        vectors.append(
            {
                "name": name,
                "secret_hex": secret.hex(),
                "ts": ts,
                "nonce": nonce,
                "body": body.decode("utf-8"),
                "body_sha256": body_hash,
                "mac": m,
                "response_body": response.decode("utf-8"),
                "proof": proof(secret, m, response),
            }
        )
    out = Path(__file__).resolve().parents[1] / "shared" / "schemas" / "test-vectors" / "hmac.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "scheme": "mac = HMAC-SHA256(secret, 'RIFT/1|' + ts + '|' + nonce + '|' + sha256_hex(body)); "
        "proof = HMAC-SHA256(secret, mac + '|' + sha256_hex(response_body))",
        "vectors": vectors,
    }
    out.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {out} ({len(vectors)} vectors)")


if __name__ == "__main__":
    main()
