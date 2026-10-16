# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""The server's view of one Revit instance.

Before the first request the target is verified (pid alive and created at the time written in
the discovery file, ADR-002), then the lease is taken (ADR-004). The add-in renews the lease on
every signed request; the client takes it again when it stayed idle past the lease. Protocol
errors are raised as :class:`RiftError`.

After ten consecutive authentication failures the add-in rotates its secret and increments the
generation in the discovery file (ADR-002/003). A client that still signs with the old secret
gets ``UNAUTHORIZED`` signed with the new one, which its proof check rejects: in both cases the
client re-reads the discovery and key files once and retries when the secret changed.

The synchronous pipe client (transport/pipe.py, proven by P-D3) is run in a worker thread
so that the asynchronous MCP loop keeps serving stdio.
"""

from __future__ import annotations

import os
import secrets as _secrets
import time
from collections.abc import Callable
from typing import Any

import anyio

from . import PROTOCOL_VERSION, __version__
from . import discovery as _discovery
from .discovery import Instance
from .errors import RiftError
from .transport import pipe

CallFn = Callable[..., pipe.PipeResponse]
ReloadFn = Callable[[Instance], tuple[Instance, bytes] | None]

#: Lease duration granted by the add-in (ADR-004) and the margin under which the client re-leases.
LEASE_S = 60.0
LEASE_MARGIN_S = 10.0


def reload_from_disk(instance: Instance) -> tuple[Instance, bytes] | None:
    """Re-reads the discovery record and the key file of ``instance`` (None when it vanished)."""
    fresh = _discovery.reload(instance)
    if fresh is None:
        return None
    try:
        return fresh, _discovery.read_secret(fresh)
    except RiftError:
        return None


class RevitClient:
    def __init__(
        self,
        instance: Instance,
        secret: bytes,
        *,
        client_name: str = "rift-mcp",
        client_version: str = __version__,
        call_fn: CallFn = pipe.call,
        alive_fn: Callable[[Instance], bool] = _discovery.verify_alive,
        reload_fn: ReloadFn | None = reload_from_disk,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self.instance = instance
        self._secret = secret
        self._call_fn = call_fn
        self._alive_fn = alive_fn
        self._reload_fn = reload_fn
        self._clock = clock
        self.client_name = client_name
        self.client_version = client_version
        self.client_id = f"{client_name}-{os.getpid()}-{int(time.time())}-{_secrets.token_hex(2)}"
        self._verified = False
        self._lease_until: str | None = None
        self._lease_deadline = 0.0
        self.holder: dict[str, Any] | None = None
        self.rotations_seen = 0
        self._seq = 0

    @property
    def leased(self) -> bool:
        return self._lease_until is not None and self._clock() < self._lease_deadline

    @property
    def generation(self) -> int:
        return self.instance.generation

    def _next_id(self) -> str:
        self._seq += 1
        return f"{self.client_id}-{self._seq}"

    # -- target ---------------------------------------------------------------------------

    def verify_target(self) -> None:
        """TARGET_CHANGED unless the pid of the discovery file is the process that wrote it."""
        if self._verified:
            return
        if not self._alive_fn(self.instance):
            raise RiftError(
                "TARGET_CHANGED",
                f"pid {self.instance.pid} is gone or is not the Revit of the discovery file",
                {"pid": self.instance.pid},
            )
        self._verified = True

    def _reload_secret(self) -> bool:
        """Re-reads discovery + key file; True when the secret changed (rotation happened)."""
        if self._reload_fn is None:
            return False
        fresh = self._reload_fn(self.instance)
        if fresh is None:
            return False
        instance, secret = fresh
        changed = secret != self._secret
        self.instance = instance
        self._secret = secret
        if changed:
            self.rotations_seen += 1
        return changed

    # -- transport ------------------------------------------------------------------------

    def _send(self, body: dict[str, Any]) -> dict[str, Any]:
        try:
            resp = self._call_fn(
                self.instance.pipe, self._secret, body, expected_pid=self.instance.pid
            )
        except pipe.ProofError as exc:
            raise _StaleSecret("TARGET_CHANGED", "response proof did not verify") from exc
        except pipe.PipeError as exc:
            code = str(exc)
            if code in ("TARGET_BUSY", "TARGET_CHANGED", "PAYLOAD_TOO_LARGE"):
                raise RiftError(code) from exc
            raise RiftError("TARGET_LOST", f"pipe error: {exc}") from exc
        except OSError as exc:
            raise RiftError("TARGET_LOST", f"cannot reach the add-in: {exc}") from exc
        if (resp.body.get("error") or {}).get("code") == "UNAUTHORIZED":
            raise _StaleSecret("UNAUTHORIZED", str(resp.body["error"].get("message") or ""))
        return resp.body

    def _raw_call(self, body: dict[str, Any]) -> dict[str, Any]:
        """One signed exchange; after a rotation, one retry with the re-read secret."""
        self.verify_target()
        try:
            return self._send(body)
        except _StaleSecret as stale:
            if not self._reload_secret():
                raise RiftError(stale.code, stale.message) from None
        # The add-in refused the frame before running anything: a fresh nonce makes it new.
        try:
            return self._send({**body, "id": self._next_id()})
        except _StaleSecret as stale:
            raise RiftError(stale.code, stale.message) from None

    def _envelope(
        self,
        op: str,
        args: dict[str, Any] | None,
        timeout_s: int,
        tool: str | None = None,
        expected_document: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        # session_id is the lease client id: the add-in renews the lease of its holder with it
        # and refuses LEASE_HELD to anyone else; name/version label a holder taking a free lease.
        client: dict[str, Any] = {
            "session_id": self.client_id,
            "name": self.client_name,
            "version": self.client_version,
        }
        if tool:
            # MCP tool name, shown by the in-Revit activity card instead of the op (ADR-023).
            client["tool"] = tool
        envelope: dict[str, Any] = {
            "v": PROTOCOL_VERSION,
            "id": self._next_id(),
            "op": op,
            "timeout_s": timeout_s,
            "client": client,
            "args": args or {},
        }
        if expected_document:
            envelope["expected_document"] = expected_document
        return envelope

    def _check(self, response: dict[str, Any]) -> dict[str, Any]:
        status = response.get("status")
        if status in ("ok", "accepted", "running"):
            # Every signed request renewed the lease on the add-in side.
            if self._lease_until is not None:
                self._lease_deadline = self._clock() + LEASE_S - LEASE_MARGIN_S
            return response
        err = response.get("error") or {}
        code = str(err.get("code", "INTERNAL"))
        details = response.get("result") if isinstance(response.get("result"), dict) else None
        if code == "LEASE_HELD":
            self._lease_until = None
            self.holder = (details or {}).get("holder")
        elif code == "PAUSED":
            # The user paused the server in Revit (ADR-029 §2): the add-in revoked the lease.
            # No retry and no automatic re-lease: the next tool call leases again and gets
            # PAUSED again while the pause holds.
            self._lease_until = None
        raise RiftError(code, str(err.get("message") or code), details)

    # -- lease ----------------------------------------------------------------------------

    def ensure_lease_sync(self) -> dict[str, Any]:
        """Takes the lease on first use and again when the client stayed idle past it."""
        if self.leased:
            return {"lease_until_utc": self._lease_until, "holder": self.holder}
        body = self._envelope(
            "lease",
            {
                "client_id": self.client_id,
                "client": {"name": self.client_name, "version": self.client_version},
            },
            10,
        )
        response = self._check(self._raw_call(body))
        result = response.get("result") or {}
        self._lease_until = result.get("lease_until_utc")
        self.holder = result.get("holder")
        self._lease_deadline = self._clock() + LEASE_S - LEASE_MARGIN_S
        return result

    # -- calls ----------------------------------------------------------------------------

    def call_sync(
        self,
        op: str,
        args: dict[str, Any] | None = None,
        *,
        timeout_s: int = 60,
        tool: str | None = None,
        wait_s: int | None = None,
        expected_document: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        """One request; ``accepted`` is followed by ``job`` polling until ``timeout_s``.

        ``wait_s`` (default ``timeout_s``) is how long the add-in holds the pipe before
        answering ``accepted {job_id}``: keep it short for long scripts so that the pipe
        is not held for minutes.
        """
        self.ensure_lease_sync()
        envelope = self._envelope(
            op, args, wait_s if wait_s is not None else timeout_s, tool, expected_document
        )
        response = self._check(self._raw_call(envelope))
        if response.get("status") == "accepted":
            job_id = (response.get("result") or {}).get("job_id")
            return self.wait_job_sync(str(job_id), timeout_s, tool=tool)
        return response

    def wait_job_sync(
        self, job_id: str, timeout_s: int, *, tool: str | None = None
    ) -> dict[str, Any]:
        deadline = time.monotonic() + max(timeout_s, 1)
        while True:
            remaining = int(max(1, min(30, deadline - time.monotonic())))
            response = self._check(
                self._raw_call(
                    self._envelope(
                        "job", {"job_id": job_id, "wait_s": remaining}, remaining + 5, tool
                    )
                )
            )
            result = response.get("result") or {}
            if result.get("state") == "done":
                return {**response, "result": result.get("result"), "job": {"job_id": job_id}}
            if time.monotonic() >= deadline:
                raise RiftError("TARGET_BUSY", f"job {job_id} still running", {"job_id": job_id})

    async def ensure_lease(self) -> dict[str, Any]:
        return await anyio.to_thread.run_sync(self.ensure_lease_sync)

    async def call(
        self,
        op: str,
        args: dict[str, Any] | None = None,
        *,
        timeout_s: int = 60,
        tool: str | None = None,
        wait_s: int | None = None,
        expected_document: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        return await anyio.to_thread.run_sync(
            lambda: self.call_sync(
                op,
                args,
                timeout_s=timeout_s,
                tool=tool,
                wait_s=wait_s,
                expected_document=expected_document,
            )
        )


class _StaleSecret(Exception):
    """UNAUTHORIZED or a failed proof: maybe the add-in rotated its secret."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message or code)
        self.code = code
        self.message = message or code
