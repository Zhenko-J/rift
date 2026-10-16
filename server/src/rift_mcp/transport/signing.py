# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Request/response signing (ADR-002, docs/PROTOCOL.md).

mac   = HMAC-SHA256(secret, "RIFT/1|" + ts + "|" + nonce + "|" + sha256_hex(body))
proof = HMAC-SHA256(secret, mac + "|" + sha256_hex(response_body))

Reference vectors: shared/schemas/test-vectors/hmac.json (also checked by the add-in's xUnit suite).
"""

from __future__ import annotations

import hashlib
import hmac
import secrets

FRAME_MAGIC = "RIFT/1"
MAX_HEADER_BYTES = 256
MAX_BODY_BYTES = 1024 * 1024
MAX_TIMESTAMP_SKEW_S = 120


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def compute_mac(secret: bytes, ts: int, nonce_hex: str, body: bytes) -> str:
    message = f"{FRAME_MAGIC}|{ts}|{nonce_hex}|{sha256_hex(body)}".encode("ascii")
    return hmac.new(secret, message, hashlib.sha256).hexdigest()


def compute_proof(secret: bytes, request_mac: str, response_body: bytes) -> str:
    message = f"{request_mac}|{sha256_hex(response_body)}".encode("ascii")
    return hmac.new(secret, message, hashlib.sha256).hexdigest()


def verify_proof(secret: bytes, request_mac: str, response_body: bytes, proof: str) -> bool:
    return hmac.compare_digest(compute_proof(secret, request_mac, response_body), proof)


def new_nonce() -> str:
    return secrets.token_hex(16)
