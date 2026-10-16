# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Claude Code permission rules for RIFT (P1 of the 2026-10-16 benchmark, ADR-030 §3).

``rift-mcp permissions --install`` merges the generated ``allow`` and ``ask`` lists into a
Claude Code settings file, only when asked: it adds the missing RIFT rules, moves a RIFT rule
that sits in the wrong list, never touches anything else, writes a timestamped backup first and
replaces the file atomically. ``audit`` tells ``doctor`` whether the tools that run code or
change the model ask before running.
"""

from __future__ import annotations

import json
import os
import tempfile
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from .install import SERVER_NAME

PREFIX = f"mcp__{SERVER_NAME}__"
SCOPES = ("user", "project", "local")

# Tools that must ask first: free code and the main write paths. Typed writes follow their risk.
MUST_ASK = ("run_query", "run_modify", "batch")

#: ``ask``: reads allowed, scripts and writes asked in Claude Code (the default).
#: ``session``: every RIFT tool allowed in Claude Code; changes are governed by the session the
#: user chooses in Revit at the start of each assistant session (ADR-033: sandbox or active
#: project, full auto or supervised with Keep/Discard in Revit).
PROFILES = ("ask", "session")


def profile_rules(rules: dict[str, list[str]], profile: str) -> dict[str, list[str]]:
    """The generated rules for ``profile``: ``session`` moves every ``ask`` rule to ``allow``."""
    if profile not in PROFILES:
        raise ValueError(f"profile must be one of {', '.join(PROFILES)}")
    if profile == "ask":
        return {"allow": list(rules.get("allow", [])), "ask": list(rules.get("ask", []))}
    return {"allow": [*rules.get("allow", []), *rules.get("ask", [])], "ask": []}


def settings_path(scope: str, home: Path | None = None, cwd: Path | None = None) -> Path:
    """The settings file of a scope.

    user: ~/.claude/settings.json; project: .claude/settings.json;
    local: .claude/settings.local.json."""
    if scope not in SCOPES:
        raise ValueError(f"scope must be one of {', '.join(SCOPES)}")
    if scope == "user":
        return (home or Path.home()) / ".claude" / "settings.json"
    base = (cwd or Path.cwd()) / ".claude"
    return base / ("settings.json" if scope == "project" else "settings.local.json")


@dataclass
class MergeReport:
    path: Path
    added_allow: list[str] = field(default_factory=list)
    added_ask: list[str] = field(default_factory=list)
    moved_to_ask: list[str] = field(default_factory=list)
    moved_to_allow: list[str] = field(default_factory=list)
    backup: Path | None = None
    written: bool = False

    @property
    def changed(self) -> bool:
        return bool(self.added_allow or self.added_ask or self.moved_to_ask or self.moved_to_allow)

    def to_dict(self) -> dict[str, Any]:
        return {
            "path": str(self.path),
            "written": self.written,
            "backup": str(self.backup) if self.backup else None,
            "added_allow": self.added_allow,
            "added_ask": self.added_ask,
            "moved_to_ask": self.moved_to_ask,
            "moved_to_allow": self.moved_to_allow,
        }


def _read(path: Path) -> dict[str, Any]:
    if not path.exists():
        return {}
    text = path.read_text(encoding="utf-8")
    if not text.strip():
        return {}
    data = json.loads(text)
    if not isinstance(data, dict):
        raise ValueError(f"{path} does not hold a JSON object")
    return data


def merge(
    settings: dict[str, Any], rules: dict[str, list[str]]
) -> tuple[dict[str, Any], MergeReport]:
    """Pure merge of ``rules`` ({"allow": [...], "ask": [...]}) into ``settings``.

    The report carries no path; ``install`` fills it."""
    out = json.loads(json.dumps(settings))  # deep copy, JSON types only
    perms = out.setdefault("permissions", {})
    if not isinstance(perms, dict):
        raise ValueError('"permissions" is not a JSON object')
    allow = perms.setdefault("allow", [])
    ask = perms.setdefault("ask", [])
    if not isinstance(allow, list) or not isinstance(ask, list):
        raise ValueError('"permissions.allow" and "permissions.ask" must be lists')
    report = MergeReport(Path())
    for rule in rules.get("ask", []):
        if rule in allow:
            allow.remove(rule)
            report.moved_to_ask.append(rule)
        if rule not in ask:
            ask.append(rule)
            if rule not in report.moved_to_ask:
                report.added_ask.append(rule)
    for rule in rules.get("allow", []):
        if rule in ask:
            ask.remove(rule)
            report.moved_to_allow.append(rule)
        if rule not in allow:
            allow.append(rule)
            if rule not in report.moved_to_allow:
                report.added_allow.append(rule)
    return out, report


def install(path: Path, rules: dict[str, list[str]], *, now: float | None = None) -> MergeReport:
    """Merges ``rules`` into the settings file at ``path``: backup, then atomic replace.

    Nothing is written when nothing changes."""
    current = _read(path)
    merged, report = merge(current, rules)
    report.path = path
    if not report.changed:
        return report
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        stamp = time.strftime("%Y%m%d-%H%M%S", time.localtime(now))
        report.backup = path.with_name(path.name + f".bak-{stamp}")
        report.backup.write_bytes(path.read_bytes())
    fd, tmp = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
            json.dump(merged, f, indent=2, ensure_ascii=False)
            f.write("\n")
        os.replace(tmp, path)
    except BaseException:
        Path(tmp).unlink(missing_ok=True)
        raise
    report.written = True
    return report


def audit(paths: list[Path]) -> dict[str, Any]:
    """Where RIFT's rules are, and whether the tools of ``MUST_ASK`` ask.

    A tool asks when it is in ``ask`` or ``deny`` and never in ``allow``."""
    found: dict[str, dict[str, list[str]]] = {}
    allowed: set[str] = set()
    asked: set[str] = set()
    for p in paths:
        try:
            data = _read(p)
        except (OSError, ValueError, json.JSONDecodeError) as exc:
            found[str(p)] = {"error": [str(exc)]}
            continue
        perms = data.get("permissions") or {}
        if not isinstance(perms, dict):
            continue
        entry: dict[str, list[str]] = {}
        for key in ("allow", "ask", "deny"):
            rules = [
                r for r in (perms.get(key) or []) if isinstance(r, str) and r.startswith(PREFIX)
            ]
            if rules:
                entry[key] = rules
                if key == "allow":
                    allowed.update(rules)
                else:
                    asked.update(rules)
        if entry:
            found[str(p)] = entry
    must = [PREFIX + t for t in MUST_ASK]
    missing = [r for r in must if r not in asked]
    allowed_anyway = [r for r in must if r in allowed]
    writes_ask = not missing and not allowed_anyway
    session = all(r in allowed and r not in asked for r in must)
    if writes_ask:
        profile, hint = "ask", None
    elif session:
        profile = "session"
        hint = (
            "RIFT's tools run without a Claude Code prompt: changes are governed by the session "
            "window in Revit (keep session_prompt on in config.json). To be asked in Claude Code "
            "again: rift-mcp permissions --install --profile ask"
        )
    else:
        profile = "none"
        hint = (
            "run_query, run_modify and batch run code or change the model without a "
            "confirmation in your Claude Code settings: rift-mcp permissions --install "
            "(user scope by default), or --profile session to rely on the session window in Revit"
        )
    return {
        "files": found,
        "profile": profile,
        "writes_ask": writes_ask,
        "not_asking": sorted(set(missing) | set(allowed_anyway)),
        "hint": hint,
    }
