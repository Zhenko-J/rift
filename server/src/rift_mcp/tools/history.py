# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""``record_change``, ``get_change_records`` and ``cancel_job`` (M2).

The history lives on the add-in side (``%LOCALAPPDATA%\\rift\\history``), one file per document,
one change receipt per ``run_modify`` (dry runs included). Nothing here touches the model.
"""

from __future__ import annotations

from typing import Any

from .run_query import Caller

TOOL_RECORD = "record_change"
TOOL_LIST = "get_change_records"
TOOL_CANCEL = "cancel_job"


async def record_change(
    client: Caller, reason: str, history_id: str | None = None
) -> dict[str, Any]:
    args: dict[str, Any] = {"name": "history.record", "reason": reason}
    if history_id:
        args["history_id"] = history_id
    response = await client.call("tool", args, timeout_s=30, tool=TOOL_RECORD)
    result = response.get("result") or {}
    return {"ok": True, "document": result.get("document"), "record": result.get("record")}


async def get_change_records(client: Caller, limit: int = 20) -> dict[str, Any]:
    response = await client.call(
        "tool", {"name": "history.list", "limit": limit}, timeout_s=30, tool=TOOL_LIST
    )
    result = response.get("result") or {}
    return {"ok": True, "document": result.get("document"), "records": result.get("records", [])}


async def cancel_job(client: Caller, job_id: str) -> dict[str, Any]:
    response = await client.call(
        "job", {"job_id": job_id, "cancel": True}, timeout_s=15, tool=TOOL_CANCEL
    )
    result = response.get("result") or {}
    return {
        "ok": True,
        "job_id": result.get("job_id", job_id),
        "cancel": result.get("cancel"),
        "state": result.get("state"),
    }
