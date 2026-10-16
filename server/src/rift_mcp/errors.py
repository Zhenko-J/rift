# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Server-side error codes (docs/PROTOCOL.md § Error codes) and the exception that carries them."""

from __future__ import annotations

from typing import Any

SERVER_CODES = frozenset(
    {
        "NO_TARGET",
        "AMBIGUOUS_TARGET",
        "TARGET_CHANGED",
        "TARGET_LOST",
        "TARGET_BUSY",
        "TARGET_INTERRUPTED",
        "READ_ONLY_MODE",
        "LINT_ERROR",
        "WRITE_FAILED",
    }
)


class RiftError(Exception):
    """An error with a protocol code, surfaced to the MCP caller as typed data."""

    def __init__(self, code: str, message: str = "", details: dict[str, Any] | None = None) -> None:
        super().__init__(message or code)
        self.code = code
        self.message = message or code
        self.details = details

    def to_dict(self) -> dict[str, Any]:
        out: dict[str, Any] = {"code": self.code, "message": self.message}
        if self.details:
            out["details"] = self.details
        return out
