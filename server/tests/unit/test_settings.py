# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""P1 of the 2026-10-16 benchmark: permission rules merged on request, audited by doctor."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from rift_mcp import cli
from rift_mcp.settings import PREFIX, audit, install, merge, settings_path

RULES = {
    "allow": [PREFIX + "get_context", PREFIX + "list_views"],
    "ask": [PREFIX + "run_query", PREFIX + "run_modify", PREFIX + "batch"],
}


def test_merge_adds_missing_rules_and_keeps_everything_else() -> None:
    settings = {
        "model": "x",
        "permissions": {"allow": ["Bash(git status)"], "deny": ["Read(.env)"]},
    }
    merged, report = merge(settings, RULES)
    assert merged["model"] == "x"
    assert merged["permissions"]["deny"] == ["Read(.env)"]
    assert merged["permissions"]["allow"][0] == "Bash(git status)"
    assert set(RULES["allow"]) <= set(merged["permissions"]["allow"])
    assert merged["permissions"]["ask"] == RULES["ask"]
    assert report.added_ask == RULES["ask"]
    assert settings["permissions"] == {"allow": ["Bash(git status)"], "deny": ["Read(.env)"]}


def test_merge_moves_a_rift_rule_that_sits_in_the_wrong_list() -> None:
    settings = {"permissions": {"allow": [PREFIX + "run_query"], "ask": [PREFIX + "get_context"]}}
    merged, report = merge(settings, RULES)
    assert PREFIX + "run_query" not in merged["permissions"]["allow"]
    assert PREFIX + "run_query" in merged["permissions"]["ask"]
    assert PREFIX + "get_context" in merged["permissions"]["allow"]
    assert report.moved_to_ask == [PREFIX + "run_query"]
    assert report.moved_to_allow == [PREFIX + "get_context"]


def test_install_writes_a_backup_and_is_idempotent(tmp_path: Path) -> None:
    path = tmp_path / ".claude" / "settings.json"
    path.parent.mkdir()
    path.write_text(json.dumps({"permissions": {"allow": ["Bash(ls)"]}}), encoding="utf-8")
    first = install(path, RULES, now=0)
    assert first.written
    assert first.backup is not None
    assert json.loads(first.backup.read_text(encoding="utf-8")) == {
        "permissions": {"allow": ["Bash(ls)"]}
    }
    saved = json.loads(path.read_text(encoding="utf-8"))
    assert "Bash(ls)" in saved["permissions"]["allow"]
    again = install(path, RULES)
    assert not again.written
    assert not again.changed
    assert not list(path.parent.glob("*.tmp"))


def test_install_creates_a_missing_settings_file(tmp_path: Path) -> None:
    path = tmp_path / "new" / ".claude" / "settings.local.json"
    report = install(path, RULES)
    assert report.written
    assert report.backup is None
    assert json.loads(path.read_text(encoding="utf-8"))["permissions"]["ask"] == RULES["ask"]


def test_install_refuses_a_file_that_is_not_a_json_object(tmp_path: Path) -> None:
    path = tmp_path / "settings.json"
    path.write_text("[1, 2]", encoding="utf-8")
    with pytest.raises(ValueError):
        install(path, RULES)
    assert path.read_text(encoding="utf-8") == "[1, 2]"


def test_audit_says_whether_code_and_writes_ask(tmp_path: Path) -> None:
    missing = tmp_path / "none.json"
    assert audit([missing])["writes_ask"] is False
    good = tmp_path / "good.json"
    good.write_text(json.dumps({"permissions": RULES}), encoding="utf-8")
    ok = audit([missing, good])
    assert ok["writes_ask"] is True
    assert ok["hint"] is None
    bad = tmp_path / "bad.json"
    bad.write_text(json.dumps({"permissions": {"allow": [PREFIX + "run_query"]}}), encoding="utf-8")
    worse = audit([good, bad])
    assert worse["writes_ask"] is False
    assert PREFIX + "run_query" in worse["not_asking"]


def test_settings_paths_by_scope(tmp_path: Path) -> None:
    assert settings_path("user", home=tmp_path) == tmp_path / ".claude" / "settings.json"
    assert settings_path("project", cwd=tmp_path) == tmp_path / ".claude" / "settings.json"
    assert settings_path("local", cwd=tmp_path) == tmp_path / ".claude" / "settings.local.json"
    with pytest.raises(ValueError):
        settings_path("global")


def test_generated_rules_ask_for_code_and_writes() -> None:
    rules = cli.permission_rules()
    for tool in ("run_query", "run_modify", "batch"):
        assert PREFIX + tool in rules["ask"]
        assert PREFIX + tool not in rules["allow"]


def test_permissions_install_from_the_cli(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(Path, "home", classmethod(lambda cls: tmp_path))
    assert cli.main(["permissions", "--install"]) == 0
    saved = json.loads((tmp_path / ".claude" / "settings.json").read_text(encoding="utf-8"))
    assert PREFIX + "run_modify" in saved["permissions"]["ask"]
