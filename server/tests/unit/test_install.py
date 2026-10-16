# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Plan test 20: ``install claude`` builds ``claude mcp add --scope user rift -- <server>``,
touches no file, and gives a readable hint when the claude CLI is not found."""

from __future__ import annotations

import subprocess
from pathlib import Path
from typing import Any

import pytest

from rift_mcp import install
from rift_mcp.cli import main


def _which_with(available: dict[str, str]) -> Any:
    return lambda name: available.get(name)


def test_the_server_command_is_the_installed_script_or_the_running_interpreter() -> None:
    assert install.server_command(_which_with({"rift-mcp": r"C:\venv\rift-mcp.exe"})) == [
        r"C:\venv\rift-mcp.exe",
        "serve",
    ]
    assert install.server_command(_which_with({}), executable=r"C:\py\python.exe") == [
        r"C:\py\python.exe",
        "-m",
        "rift_mcp.cli",
        "serve",
    ]


def test_claude_mcp_add_names_the_server_and_separates_its_command() -> None:
    add = install.claude_mcp_add([r"C:\venv\rift-mcp.exe", "serve"], "user", r"C:\bin\claude.cmd")
    assert add == [
        r"C:\bin\claude.cmd",
        "mcp",
        "add",
        "--transport",
        "stdio",
        "--scope",
        "user",
        "rift",
        "--",
        r"C:\venv\rift-mcp.exe",
        "serve",
    ]
    with pytest.raises(ValueError):
        install.claude_mcp_add(["x"], "global")


def test_install_runs_claude_mcp_add_and_writes_nothing(tmp_path: Path) -> None:
    calls: list[list[str]] = []

    def run(cmd: list[str], **_kw: Any) -> subprocess.CompletedProcess[str]:
        calls.append(cmd)
        return subprocess.CompletedProcess(cmd, 0, "Added stdio MCP server rift", "")

    before = sorted(tmp_path.iterdir())
    report = install.install_claude(
        "local",
        run=run,
        which=_which_with({"claude": "claude.cmd", "rift-mcp": "rift-mcp.exe"}),
    )
    assert report.ok is True
    assert calls == [report.command]
    assert report.command[:3] == ["claude.cmd", "mcp", "add"]
    assert "--scope" in report.command and "local" in report.command
    assert report.command[-2:] == ["rift-mcp.exe", "serve"]
    assert "registered" in report.hint
    assert report.config_block == {
        "mcpServers": {"rift": {"command": "rift-mcp.exe", "args": ["serve"], "transport": "stdio"}}
    }
    assert sorted(tmp_path.iterdir()) == before


def test_install_without_the_claude_cli_fails_with_a_hint_and_the_command_to_run() -> None:
    def run(cmd: list[str], **_kw: Any) -> subprocess.CompletedProcess[str]:
        raise AssertionError("must not run")

    report = install.install_claude("user", run=run, which=_which_with({}))
    assert report.ok is False
    assert "not found on the PATH" in report.hint
    assert report.command[0] == "claude"
    text = install.render(report)
    assert "failed: claude mcp add" in text
    assert '"mcpServers"' in text


def test_install_reports_a_failed_claude_mcp_add() -> None:
    def run(cmd: list[str], **_kw: Any) -> subprocess.CompletedProcess[str]:
        return subprocess.CompletedProcess(cmd, 1, "", "MCP server rift already exists")

    report = install.install_claude("user", run=run, which=_which_with({"claude": "claude"}))
    assert report.ok is False
    assert "already exists" in report.stderr
    assert "claude mcp remove rift" in report.hint


def test_print_only_never_runs_anything(capsys: pytest.CaptureFixture[str]) -> None:
    assert main(["install", "claude", "--print"]) == 0
    out = capsys.readouterr().out
    assert "mcp add --transport stdio --scope user rift --" in out
    assert "not run (--print)" in out
