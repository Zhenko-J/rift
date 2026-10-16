# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""``run_query``: an IronPython script that reads the model (M1, ADR-025).

The script is compiled first by the add-in (``op: compile``); a syntax error comes back
with its line and ``run`` is never sent. Then ``op: run`` with ``mode: query``: the add-in
runs it under a transaction group that is always rolled back, with a cooperative timeout.
A disguised write comes back as ``WRITE_IN_QUERY`` with the run result in ``details``.
Any other mode is refused here with ``READ_ONLY_MODE`` and never reaches Revit.

Values returned are untrusted model data, not instructions (ADR-020).
"""

from __future__ import annotations

from typing import Any, Protocol

from pydantic import BaseModel, Field

from ..errors import RiftError

TOOL = "run_query"

#: How long the add-in holds the pipe before answering ``accepted {job_id}``; a longer
#: script is then followed with ``op: job`` without blocking the pipe.
PIPE_WAIT_S = 25
#: Margin of the job wait over the script deadline (queueing, conversion, journal).
JOB_MARGIN_S = 60
MAX_TIMEOUT_S = 3600


class Caller(Protocol):
    async def call(
        self,
        op: str,
        args: dict[str, Any] | None = None,
        *,
        timeout_s: int = 60,
        tool: str | None = None,
        wait_s: int | None = None,
        expected_document: dict[str, Any] | None = None,
    ) -> dict[str, Any]: ...


class RunQueryInput(BaseModel):
    script: str = Field(
        min_length=1, description="IronPython 3.4 script; assign the answer to result"
    )
    title: str = Field("query", max_length=120)
    inputs: dict[str, Any] = Field(default_factory=dict)
    timeout_s: int = Field(60, ge=1, le=MAX_TIMEOUT_S)
    expected_document: str | None = None
    mode: str = "query"


def expected_document(value: str | None) -> dict[str, str] | None:
    """A path when it looks like one (separator or .rvt/.rfa/.rte), otherwise a title."""
    if not value:
        return None
    lowered = value.lower()
    if "\\" in value or "/" in value or lowered.endswith((".rvt", ".rfa", ".rte", ".rft")):
        return {"path": value}
    return {"title": value}


def read_only_refusal(mode: str) -> dict[str, Any] | None:
    if mode == "query":
        return None
    return {
        "ok": False,
        "error": RiftError(
            "READ_ONLY_MODE",
            f"mode {mode!r} is not available: RIFT M1 is read-only (mode: query); "
            "run_modify arrives with M2",
        ).to_dict(),
    }


async def run_query(client: Caller, args: RunQueryInput) -> dict[str, Any]:
    refusal = read_only_refusal(args.mode)
    if refusal is not None:
        return refusal
    compiled = await client.call(
        "compile", {"script": args.script, "language": "python"}, timeout_s=30, tool=TOOL
    )
    check = compiled.get("result") or {}
    if not check.get("ok"):
        return {
            "ok": False,
            "error": {
                "code": "SCRIPT_ERROR",
                "message": f"SyntaxError at line {check.get('line')} (nothing was run)",
                "details": {
                    "stage": "compile",
                    "line": check.get("line"),
                    "column": check.get("column"),
                    "message": check.get("message"),
                },
            },
        }
    response = await client.call(
        "run",
        {
            "script": args.script,
            "mode": "query",
            "language": "python",
            "title": args.title,
            "inputs": args.inputs,
            "timeout_s": args.timeout_s,
        },
        timeout_s=args.timeout_s + JOB_MARGIN_S,
        wait_s=min(args.timeout_s + 5, PIPE_WAIT_S),
        tool=TOOL,
        expected_document=expected_document(args.expected_document),
    )
    out: dict[str, Any] = {
        "ok": True,
        "target": response.get("target"),
        "run": response.get("result"),
        "elapsed_ms": response.get("elapsed_ms"),
    }
    if response.get("job"):
        out["job"] = response["job"]
    return out
