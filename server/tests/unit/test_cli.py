# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Plan tests 15d (hashes: CI), 17 (generated listings) and 20 (install): the CLI."""

from __future__ import annotations

import json
import os
import time
from pathlib import Path

import pytest

from rift_mcp import catalog
from rift_mcp.cli import main


def test_schemas_and_permissions_checks_pass_on_a_clean_tree() -> None:
    assert main(["schemas", "--check"]) == 0
    assert main(["permissions", "--check"]) == 0


def test_permissions_claude_allows_only_shipped_auto_tools(
    capsys: pytest.CaptureFixture[str],
) -> None:
    assert main(["permissions", "--format", "claude"]) == 0
    rules = json.loads(capsys.readouterr().out)["permissions"]
    assert rules["allow"][:3] == [
        "mcp__rift__get_context",
        "mcp__rift__audit_template",
        "mcp__rift__get_change_records",
    ]
    # every allowed tool is a fixed read; every typed write stays behind a confirmation
    from rift_mcp.registry import get

    assert all(get(r.removeprefix("mcp__rift__")).risk == "auto" for r in rules["allow"])
    assert "mcp__rift__delete_elements" in rules["ask"]
    assert "mcp__rift__list_sheets" in rules["allow"]
    assert "mcp__rift__audit_template_write" in rules["ask"]
    # never_auto (run_modify) is never allowed: it stays behind the client's confirmation
    assert "mcp__rift__run_modify" in rules["ask"]
    assert "mcp__rift__run_modify" not in rules["allow"]
    assert all(r.startswith("mcp__rift__") for r in rules["ask"])
    assert main(["permissions", "--format", "json"]) == 2


def test_schemas_lists_the_shipped_tools_with_their_input_schema(
    capsys: pytest.CaptureFixture[str],
) -> None:
    assert main(["schemas"]) == 0
    doc = json.loads(capsys.readouterr().out)
    names = [t["name"] for t in doc["tools"]]
    assert names[:8] == [
        "get_context",
        "run_query",
        "audit_template",
        "audit_template_write",
        "run_modify",
        "record_change",
        "get_change_records",
        "cancel_job",
    ]
    modify = next(t for t in doc["tools"] if t["name"] == "run_modify")
    assert modify["inputSchema"]["properties"]["dry_run"]["default"] is True
    assert modify["annotations"]["destructiveHint"] is True
    run_query = next(t for t in doc["tools"] if t["name"] == "run_query")
    assert "script" in run_query["inputSchema"]["properties"]
    assert run_query["inputSchema"]["required"] == ["script"]
    assert run_query["risk"] == "ask"
    write = next(t for t in doc["tools"] if t["name"] == "audit_template_write")
    assert write["inputSchema"]["required"] == ["write_to"]
    assert write["annotations"]["readOnlyHint"] is False


def test_generated_artefacts_are_current() -> None:
    assert main(["tools", "--check"]) == 0
    assert catalog.check("tools") == []
    assert catalog.check("permissions") == []
    assert catalog.check("schemas") == []


def test_a_stale_artefact_is_named_with_the_command_to_regenerate_it(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    monkeypatch.setattr(catalog, "permissions_path", lambda: tmp_path / "p.json")
    monkeypatch.setattr(catalog, "repo_root", lambda: tmp_path)
    stale = catalog.check("permissions")
    assert stale == ["p.json (run: rift-mcp permissions --format claude --write)"]
    assert main(["permissions", "--check"]) == 1
    assert main(["permissions", "--write"]) == 0
    assert catalog.check("permissions") == []


def test_pause_and_resume_are_ribbon_commands(capsys: pytest.CaptureFixture[str]) -> None:
    assert main(["pause"]) == 2
    assert "ribbon" in capsys.readouterr().err
    assert main(["resume"]) == 2


def test_purge_deletes_old_run_folders_only(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    monkeypatch.setenv("RIFT_HOME", str(tmp_path))
    runs = tmp_path / "runs"
    old = runs / "20260901T000000Z-aa"
    new = runs / "20261013T000000Z-bb"
    old.mkdir(parents=True)
    new.mkdir()
    (old / "request.json").write_text("{}", encoding="utf-8")
    stamp = time.time() - 20 * 86400
    os.utime(old, (stamp, stamp))
    assert main(["purge", "--days", "14"]) == 0
    assert "purged 1 run folder(s)" in capsys.readouterr().out
    assert not old.exists() and new.exists()
    assert main(["purge"]) == 0
    assert not new.exists()


def test_doctor_without_any_instance_explains_and_exits_1(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    monkeypatch.setenv("RIFT_HOME", str(tmp_path))
    assert main(["doctor"]) == 1
    report = json.loads(capsys.readouterr().out)
    assert report["target"] is None
    assert report["error"]["code"] == "NO_TARGET"
    assert "install.ps1" in report["hint"]
    assert report["instances"] == []


def test_doctor_names_why_a_record_is_not_a_target(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    monkeypatch.setenv("RIFT_HOME", str(tmp_path))
    instances = tmp_path / "instances"
    instances.mkdir()
    (instances / "revit-999999.json").write_text(
        json.dumps(
            {
                "pid": 999999,
                "process_start_utc": "2026-10-09T08:00:00+00:00",
                "transport": "pipe",
                "pipe": "rift-revit-999999",
                "protocol": "1.0",
                "capabilities": ["context"],
            }
        ),
        encoding="utf-8",
    )
    assert main(["doctor"]) == 1
    report = json.loads(capsys.readouterr().out)
    entry = report["instances"][0]
    assert entry["alive"] is False
    assert any("stale" in p for p in entry["problems"])
    assert any("protocol 1.0" in p for p in entry["problems"])
    assert "secret file missing" in entry["problems"]
    assert "pipe unreachable" in entry["problems"]
    assert report["target"] is None
