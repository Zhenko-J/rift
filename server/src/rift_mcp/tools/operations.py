# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""M4 operations that the add-in runs outside the script guards: exports and the document life
cycle (``op: tool`` names ``export.*`` and ``document.*``), plus ``launch_revit``, served here.

Exports write files into a folder the user chose and never change the model. Saving, closing and
synchronising are always confirmed by the user in Revit, whatever the session mode and the
client's permission rules (ADR-033 §3): "no" answers ``USER_DECLINED``.
"""

from __future__ import annotations

import inspect
import os
import subprocess
from collections.abc import Awaitable, Callable
from pathlib import Path
from typing import Annotated, Any, Literal

from pydantic import Field

from ..errors import RiftError
from .run_query import JOB_MARGIN_S, MAX_TIMEOUT_S, PIPE_WAIT_S, Caller, expected_document

REQUIRED = inspect.Parameter.empty
Refs = list[int | str]

_FOLDER = ("folder", str, REQUIRED, "absolute folder the user chose (created when missing)")
_NAME = ("name", str | None, None, "file name without extension (default: the view or document)")
_OVERWRITE = ("overwrite", bool, False, "replace files of that name already in the folder")

#: MCP tool → (add-in operation, [(argument, type, default or REQUIRED, description)], timeout)
OPS: dict[str, tuple[str, list[tuple[str, Any, Any, str]], int]] = {
    "export_pdf": (
        "export.pdf",
        [
            _FOLDER,
            ("views", Refs | None, None, "view or sheet ids or names (default: the active view)"),
            _NAME,
            ("combine", bool, True, "one PDF for all the views"),
            _OVERWRITE,
        ],
        600,
    ),
    "export_dwg": (
        "export.dwg",
        [
            _FOLDER,
            ("views", Refs | None, None, "view or sheet ids or names (default: the active view)"),
            _NAME,
            ("setup", str | None, None, "name of a DWG export setup of the document"),
            _OVERWRITE,
        ],
        600,
    ),
    "export_ifc": (
        "export.ifc",
        [
            _FOLDER,
            _NAME,
            ("version", Literal["IFC2x3", "IFC4", "IFC4x3"], "IFC4", "IFC schema"),
            ("view", int | str | None, None, "export only what this view shows"),
            _OVERWRITE,
        ],
        1800,
    ),
    "export_nwc": (
        "export.nwc",
        [
            _FOLDER,
            _NAME,
            ("views", Refs | None, None, "one view to export (default: the whole model)"),
            _OVERWRITE,
        ],
        1800,
    ),
    "export_image": (
        "export.image",
        [
            _FOLDER,
            ("views", Refs | None, None, "view or sheet ids or names (default: the active view)"),
            _NAME,
            ("image_type", Literal["png", "jpg", "tif"], "png", "image format"),
            ("pixel_size", int, 2000, "width in pixels, 256-8192"),
            _OVERWRITE,
        ],
        600,
    ),
    "open_document": (
        "document.open",
        [("path", str, REQUIRED, "absolute path of a .rvt, .rte, .rfa or .rft file")],
        600,
    ),
    "save_document": (
        "document.save",
        [("document", str | None, None, "title or path of an open document (default: active)")],
        900,
    ),
    "close_document": (
        "document.close",
        [
            ("document", str, REQUIRED, "title or path of an open document that is not active"),
            ("save", bool, False, "save before closing"),
        ],
        900,
    ),
    "propose_baked_tool": (
        "baked.propose",
        [
            ("name", str, REQUIRED, "3-40 lower-case letters, digits or underscores"),
            ("description", str, REQUIRED, "what the command does, for the user who reviews it"),
            ("mode", Literal["query", "modify"], REQUIRED, "query reads only; modify changes"),
            ("script", str, REQUIRED, "IronPython 3.4 script; assign the answer to `result`"),
        ],
        60,
    ),
    "list_baked_tools": ("baked.list", [], 60),
    "run_baked_tool": (
        "baked.run",
        [
            ("name", str, REQUIRED, "name of an accepted command"),
            ("inputs", dict[str, Any] | None, None, "the command's inputs"),
            ("dry_run", bool, True, "modify commands: roll back and return the receipt"),
            ("policy", Literal["auto", "ask"], "auto", "ask: Keep/Discard in Revit"),
            ("allow_reference", bool, False, "allow a reference document (then Keep/Discard)"),
        ],
        120,
    ),
    "sync_with_central": (
        "document.sync",
        [
            ("document", str | None, None, "title or path of an open document (default: active)"),
            ("comment", str | None, None, "synchronisation comment"),
            ("relinquish", bool, True, "relinquish the elements and worksets borrowed"),
        ],
        1800,
    ),
}


async def call_op(client: Caller, tool: str, kwargs: dict[str, Any]) -> dict[str, Any]:
    op, _, _ = OPS[tool]
    kwargs = dict(kwargs)
    kwargs.pop("pid", None)
    timeout_s = int(kwargs.pop("timeout_s", OPS[tool][2]))
    document = kwargs.pop("expected_document", None)
    inputs = {k: v for k, v in kwargs.items() if v is not None}
    args: dict[str, Any] = {"name": op, "timeout_s": timeout_s}
    if op == "baked.run":
        # The write arguments travel beside the inputs, as for typed writes and run_modify.
        for key in ("dry_run", "policy", "allow_reference"):
            if key in inputs:
                args[key] = inputs.pop(key)
        if not (document or "").strip():
            raise RiftError(
                "EXPECTED_DOCUMENT_REQUIRED",
                "run_baked_tool may change the model: pass expected_document (the document's "
                "path or title)",
            )
    folder = inputs.get("folder")
    if folder is not None and not Path(folder).is_absolute():
        raise RiftError("BAD_REQUEST", "folder must be an absolute folder path")
    args["inputs"] = inputs
    response = await client.call(
        "tool",
        args,
        timeout_s=timeout_s + JOB_MARGIN_S,
        wait_s=min(timeout_s + 5, PIPE_WAIT_S),
        tool=tool,
        expected_document=expected_document(document),
    )
    out: dict[str, Any] = {"ok": True, "tool": tool, "result": response.get("result")}
    out["target"] = response.get("target")
    return out


def signature(tool: str) -> inspect.Signature:
    _, args, timeout = OPS[tool]
    params = [
        inspect.Parameter(
            name,
            inspect.Parameter.KEYWORD_ONLY,
            default=default,
            annotation=Annotated[kind, Field(description=description)],
        )
        for name, kind, default, description in args
    ]
    if tool.startswith("export_") or tool == "run_baked_tool":
        params.append(
            inspect.Parameter(
                "expected_document",
                inspect.Parameter.KEYWORD_ONLY,
                default=None,
                annotation=Annotated[
                    str | None, Field(description="refuse unless this document is active")
                ],
            )
        )
    params.append(
        inspect.Parameter(
            "timeout_s",
            inspect.Parameter.KEYWORD_ONLY,
            default=timeout,
            annotation=Annotated[int, Field(ge=1, le=MAX_TIMEOUT_S, description="deadline, s")],
        )
    )
    params.append(
        inspect.Parameter(
            "pid", inspect.Parameter.KEYWORD_ONLY, default=None, annotation=int | None
        )
    )
    return inspect.Signature(params, return_annotation=dict[str, Any])


Guarded = Callable[
    [int | None, Callable[[Any], Awaitable[dict[str, Any]]]], Awaitable[dict[str, Any]]
]


def make_tool(tool: str, guarded: Guarded) -> Callable[..., Awaitable[dict[str, Any]]]:
    async def run(**kwargs: Any) -> dict[str, Any]:
        return await guarded(kwargs.get("pid"), lambda c: call_op(c, tool, kwargs))

    run.__name__ = tool
    run.__signature__ = signature(tool)  # type: ignore[attr-defined]
    return run


# ── launch_revit ──────────────────────────────────────────────────────────────────────────────


def revit_installations(roots: list[Path] | None = None) -> dict[str, Path]:
    """Revit.exe of each ``Autodesk/Revit <year>`` folder under Program Files."""
    if roots is None:
        roots = [
            Path(
                os.environ.get("ProgramW6432")
                or os.environ.get("ProgramFiles")
                or r"C:\Program Files"
            )
        ]
    found: dict[str, Path] = {}
    for root in roots:
        base = root / "Autodesk"
        if not base.is_dir():
            continue
        for d in sorted(base.glob("Revit 20[0-9][0-9]")):
            exe = d / "Revit.exe"
            if exe.is_file():
                found[d.name.split()[-1]] = exe
    return found


def launch_revit(
    version: str | None,
    *,
    installations: dict[str, Path] | None = None,
    start: Callable[[list[str]], int] | None = None,
) -> dict[str, Any]:
    found = revit_installations() if installations is None else installations
    if not found:
        raise RiftError("NO_REVIT", "no Revit installation found under Program Files")
    year = version or max(found)
    if year not in found:
        raise RiftError("NO_REVIT", f"Revit {year} is not installed", {"installed": sorted(found)})
    pid = (start or _start)([str(found[year])])
    return {
        "ok": True,
        "version": year,
        "pid": pid,
        "note": "Revit starts in a minute or so; call get_context or list_targets once the "
        "RIFT add-in is loaded (answer its session window in Revit)",
    }


def _start(argv: list[str]) -> int:
    flags = getattr(subprocess, "DETACHED_PROCESS", 0) | getattr(
        subprocess, "CREATE_NEW_PROCESS_GROUP", 0
    )
    proc = subprocess.Popen(  # noqa: S603 - fixed executable found under Program Files
        argv,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        creationflags=flags,
        close_fds=True,
    )
    return proc.pid
