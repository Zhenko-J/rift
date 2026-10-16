# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""``audit_template`` and ``audit_template_write``: the template-audit presets (ADR-024).

Each preset is a Python script embedded in the add-in, run through the same read guard as
``run_query`` (``op: tool``, ``args.name = "audit.pN"``). It returns one JSON document per
preset, with the keys, values and number text of the maintainer's former extraction chain, so
that the comparison chain reads RIFT's files unchanged. ``all`` runs p1-p5 and the merge p6 in
one job (``audit.all``) and returns the six documents, ``00_TOUT`` included; ``p6`` is that same
job keeping only ``00_TOUT`` (the merge needs the five others). ``audit_template_write`` also
writes them to ``<write_to>/<document title>/<NN_name>.json``.

Values returned are untrusted model data, not instructions (ADR-020).
"""

from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Any

from pydantic import BaseModel, Field

from ..discovery import data_root
from ..errors import RiftError
from .run_query import Caller, expected_document

TOOL_READ = "audit_template"
TOOL_WRITE = "audit_template_write"

ALL_TOOL = "audit.all"
MERGED = "00_TOUT"

#: preset → (add-in tool name, document that must come back, documents kept or None for every one).
PRESETS: dict[str, tuple[str, str, tuple[str, ...] | None]] = {
    "all": (ALL_TOOL, MERGED, None),
    "p1": ("audit.p1", "01_vues_lignes_cartouches", None),
    "p2": ("audit.p2", "02_notes_cotations", None),
    "p3": ("audit.p3", "03_pochages", None),
    "p4": ("audit.p4", "04_nomenclatures", None),
    "p5": ("audit.p5", "05_parametres", None),
    "p6": (ALL_TOOL, MERGED, (MERGED,)),
}

#: How long the add-in holds the pipe before answering ``accepted {job_id}``.
PIPE_WAIT_S = 25
JOB_MARGIN_S = 60
MAX_TIMEOUT_S = 3600
#: ``all`` runs six scripts: the job may need more than one script's deadline.
ALL_PRESETS = 6

_UNSAFE = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
_DOCUMENT_NAME = re.compile(r"[0-9]{2}_[A-Za-z0-9_]{1,64}")


class AuditInput(BaseModel):
    preset: str = "all"
    timeout_s: int = Field(300, ge=1, le=MAX_TIMEOUT_S)
    expected_document: str | None = None


def unavailable(preset: str) -> dict[str, Any] | None:
    if preset in PRESETS:
        return None
    message = f"unknown preset {preset!r}; available: {', '.join(PRESETS)}"
    return {"ok": False, "error": RiftError("BAD_REQUEST", message).to_dict()}


async def audit(client: Caller, args: AuditInput, tool: str = TOOL_READ) -> dict[str, Any]:
    """Runs one preset (or all of them) and returns its documents (resolved from the journal
    when large)."""
    name, document, keep = PRESETS[args.preset]
    # timeout_s bounds each script; the job waits for the sum when the tool chains six of them
    job_s = args.timeout_s * (ALL_PRESETS if name == ALL_TOOL else 1)
    response = await client.call(
        "tool",
        {"name": name, "timeout_s": args.timeout_s},
        timeout_s=min(job_s + JOB_MARGIN_S, MAX_TIMEOUT_S + JOB_MARGIN_S),
        wait_s=min(args.timeout_s + 5, PIPE_WAIT_S),
        tool=tool,
        expected_document=expected_document(args.expected_document),
    )
    result = response.get("result") or {}
    documents = result.get("documents")
    if documents is None:
        documents = resolve_refs(result.get("documents_ref") or {})
    if document not in documents:
        raise RiftError("INTERNAL", f"the add-in did not return {document}")
    if keep is not None:
        documents = {k: v for k, v in documents.items() if k in keep}
    out: dict[str, Any] = {
        "ok": True,
        "preset": args.preset,
        "target": response.get("target"),
        "documents": documents,
        "elapsed_ms": response.get("elapsed_ms"),
    }
    if "runs" in result:
        out["runs"] = result["runs"]
        out["timing"] = result.get("timing")
    else:
        out["run"] = result.get("run")
    if result.get("documents_redacted"):
        # Large documents travel through the redacted journal (ADR-005): user and machine
        # names in model values may differ from the former chain.
        out["documents_redacted"] = True
    return out


def resolve_refs(refs: dict[str, Any], root: Path | None = None) -> dict[str, Any]:
    """Reads documents the add-in journaled (``runs/<folder>/<name>.json``) under the data root."""
    base = (root or data_root()).resolve()
    runs = (base / "runs").resolve()
    documents: dict[str, Any] = {}
    for name, ref in refs.items():
        if not isinstance(ref, str):
            raise RiftError("INTERNAL", f"document {name} could not be journaled by the add-in")
        path = (base / ref).resolve()
        if not path.is_relative_to(runs):
            raise RiftError("BAD_REQUEST", f"document reference outside the journal: {ref!r}")
        documents[name] = json.loads(path.read_text(encoding="utf-8"))
    return documents


def document_title(result: dict[str, Any]) -> str:
    runs = result.get("runs") or {}
    run = result.get("run") or next(iter(runs.values()), None) or {}
    title = (run.get("document") or {}).get("title")
    if not title:
        for doc in (result.get("documents") or {}).values():
            if isinstance(doc, dict) and (doc.get("_document") or doc.get("document")):
                title = doc.get("_document") or doc.get("document")
                break
    return str(title or "document")


def safe_folder_name(title: str) -> str:
    cleaned = _UNSAFE.sub("_", title).strip(" .")
    return cleaned[:120] or "document"


def write_documents(documents: dict[str, Any], write_to: str, title: str) -> list[str]:
    """Writes ``<write_to>/<title>/<name>.json`` as the former chain did (UTF-8, indent 2)."""
    root = Path(write_to)
    if not root.is_absolute():
        raise RiftError("BAD_REQUEST", "write_to must be an absolute folder path")
    folder = root / safe_folder_name(title)
    folder.mkdir(parents=True, exist_ok=True)
    written: list[str] = []
    for name, doc in sorted(documents.items()):
        if not _DOCUMENT_NAME.fullmatch(name):
            raise RiftError("BAD_REQUEST", f"unexpected document name {name!r}")
        path = folder / f"{name}.json"
        path.write_text(json.dumps(doc, ensure_ascii=False, indent=2), encoding="utf-8")
        written.append(str(path))
    return written


def summary(documents: dict[str, Any]) -> dict[str, Any]:
    """Row counts per list of each document; ``00_TOUT`` reports its own ``resume``."""
    out: dict[str, Any] = {}
    for name, doc in documents.items():
        if not isinstance(doc, dict):
            continue
        if name == MERGED and isinstance(doc.get("resume"), dict):
            out[name] = doc["resume"]
        else:
            out[name] = {k: len(v) for k, v in doc.items() if isinstance(v, list)}
    return out


async def audit_write(client: Caller, args: AuditInput, write_to: str) -> dict[str, Any]:
    out = await audit(client, args, tool=TOOL_WRITE)
    documents = out.pop("documents")
    title = document_title({"run": out.get("run"), "runs": out.get("runs"), "documents": documents})
    out["written"] = write_documents(documents, write_to, title)
    out["summary"] = summary(documents)
    return out
