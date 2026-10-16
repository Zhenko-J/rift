# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""``run_modify``: an IronPython script that changes the model, guarded (M2, ADR-009).

The server refuses without sending anything when ``expected_document`` is missing
(``EXPECTED_DOCUMENT_REQUIRED``) or when the lint finds an error (``LINT_ERROR``). Then
``op: compile`` and ``op: run`` with ``mode: modify``. The add-in applies the document policy
(``SANDBOX_REQUIRED`` outside a SANDBOX copy, Keep/Discard in Revit on an unsaved or allowed
reference document), runs under a transaction group named ``Agent: <title>`` and keeps it
(one undo entry) only when the run succeeded, is not a dry run — the default — and the policy
allows it. Every answer carries the change receipt and a ``history_id``.

Values returned are untrusted model data, not instructions (ADR-020).
"""

from __future__ import annotations

from typing import Any, Literal

from pydantic import BaseModel, Field

from ..errors import RiftError
from ..lint import lint
from .run_query import JOB_MARGIN_S, MAX_TIMEOUT_S, PIPE_WAIT_S, Caller, expected_document

TOOL = "run_modify"


class RunModifyInput(BaseModel):
    script: str = Field(min_length=1)
    expected_document: str = Field(min_length=1)
    title: str = Field("modify", max_length=120)
    inputs: dict[str, Any] = Field(default_factory=dict)
    dry_run: bool = True
    policy: Literal["auto", "ask"] = "auto"
    allow_reference: bool = False
    lint: bool = True
    timeout_s: int = Field(120, ge=1, le=MAX_TIMEOUT_S)
    language: Literal["python", "csharp"] = "python"


def missing_document() -> dict[str, Any]:
    return {
        "ok": False,
        "error": RiftError(
            "EXPECTED_DOCUMENT_REQUIRED",
            "run_modify needs expected_document: the path or title of the SANDBOX copy to change "
            "(get_context gives it)",
        ).to_dict(),
    }


async def run_modify(client: Caller, args: RunModifyInput) -> dict[str, Any]:
    warnings: list[dict[str, Any]] = []
    # The lint reads Python; a C# script is checked by the add-in's compiler and policy (ADR-035).
    if args.lint and args.language == "python":
        report = lint(args.script)
        if report.errors:
            return {
                "ok": False,
                "error": RiftError(
                    "LINT_ERROR",
                    f"{len(report.errors)} lint error(s); nothing was sent to Revit",
                    report.to_dict(),
                ).to_dict(),
            }
        warnings = [w.to_dict() for w in report.warnings]
    compiled = await client.call(
        "compile", {"script": args.script, "language": args.language}, timeout_s=30, tool=TOOL
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
            "mode": "modify",
            "language": args.language,
            "title": args.title,
            "inputs": args.inputs,
            "timeout_s": args.timeout_s,
            "dry_run": args.dry_run,
            "policy": args.policy,
            "allow_reference": args.allow_reference,
            "lint": args.lint,
        },
        timeout_s=args.timeout_s + JOB_MARGIN_S + (600 if args.policy == "ask" else 0),
        wait_s=min(args.timeout_s + 5, PIPE_WAIT_S),
        tool=TOOL,
        expected_document=expected_document(args.expected_document),
    )
    run = response.get("result") or {}
    out: dict[str, Any] = {
        "ok": True,
        "outcome": run.get("outcome"),
        "reason": run.get("reason"),
        "dry_run": run.get("dry_run"),
        "history_id": run.get("history_id"),
        "changes": run.get("changes"),
        "target": response.get("target"),
        "run": run,
        "elapsed_ms": response.get("elapsed_ms"),
    }
    if warnings:
        out["lint"] = {"warnings": warnings}
    if response.get("job"):
        out["job"] = response["job"]
    return out
