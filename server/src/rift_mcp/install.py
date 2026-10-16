# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""``rift-mcp install claude``: register the server in Claude Code (M1 tranche 6).

The registration goes through the ``claude`` CLI (``claude mcp add``), which owns its own
configuration files: this module never writes a file itself. When ``claude`` is not on the
PATH, or when ``--print`` is asked, it prints the exact command and the JSON block to paste
into another MCP client (Claude Desktop, Cursor…).

The server command registered is ``rift-mcp serve`` when that script is on the PATH (the
package was installed), otherwise the running interpreter with ``-m rift_mcp.cli serve``
(the server runs from its virtual environment).
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
from collections.abc import Callable
from dataclasses import dataclass, field

SERVER_NAME = "rift"
SCOPES = ("user", "local", "project")


@dataclass(frozen=True)
class InstallReport:
    command: list[str]
    ok: bool
    stdout: str = ""
    stderr: str = ""
    hint: str = ""
    config_block: dict[str, object] = field(default_factory=dict)

    def to_dict(self) -> dict[str, object]:
        return {
            "ok": self.ok,
            "command": self.command,
            "stdout": self.stdout,
            "stderr": self.stderr,
            "hint": self.hint,
            "config": self.config_block,
        }


def server_command(
    which: Callable[[str], str | None] = shutil.which, executable: str = sys.executable
) -> list[str]:
    """``rift-mcp serve`` when installed on the PATH, else ``<python> -m rift_mcp.cli serve``."""
    script = which("rift-mcp")
    if script:
        return [script, "serve"]
    return [executable, "-m", "rift_mcp.cli", "serve"]


def config_block(command: list[str]) -> dict[str, object]:
    """The ``mcpServers`` entry other clients take (Claude Desktop, Cursor)."""
    return {
        "mcpServers": {
            SERVER_NAME: {"command": command[0], "args": command[1:], "transport": "stdio"}
        }
    }


def claude_mcp_add(command: list[str], scope: str = "user", claude: str = "claude") -> list[str]:
    if scope not in SCOPES:
        raise ValueError(f"scope must be one of {', '.join(SCOPES)}")
    return [
        claude,
        "mcp",
        "add",
        "--transport",
        "stdio",
        "--scope",
        scope,
        SERVER_NAME,
        "--",
        *command,
    ]


def install_claude(
    scope: str = "user",
    *,
    run: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run,
    which: Callable[[str], str | None] = shutil.which,
    print_only: bool = False,
) -> InstallReport:
    """Registers the server with ``claude mcp add``; never touches a file itself."""
    command = server_command(which)
    block = config_block(command)
    claude = which("claude")
    add = claude_mcp_add(command, scope, claude or "claude")
    if print_only:
        return InstallReport(add, True, hint="not run (--print)", config_block=block)
    if claude is None:
        return InstallReport(
            add,
            False,
            hint=(
                "the claude CLI was not found on the PATH: run the command above from a "
                "terminal where it is available, or paste the config block into your MCP "
                "client's settings"
            ),
            config_block=block,
        )
    try:
        done = run(add, capture_output=True, text=True, check=False, timeout=60)  # noqa: S603
    except (OSError, subprocess.TimeoutExpired) as exc:
        return InstallReport(
            add, False, stderr=str(exc), hint="claude mcp add could not run", config_block=block
        )
    ok = done.returncode == 0
    hint = (
        "registered; restart Claude Code or run `claude mcp list` to see it"
        if ok
        else "claude mcp add failed; if the server already exists run `claude mcp remove rift`"
        " first, or paste the config block by hand"
    )
    return InstallReport(add, ok, done.stdout or "", done.stderr or "", hint, block)


def render(report: InstallReport) -> str:
    lines = [
        ("ok" if report.ok else "failed") + ": " + " ".join(report.command),
    ]
    if report.stdout.strip():
        lines.append(report.stdout.strip())
    if report.stderr.strip():
        lines.append(report.stderr.strip())
    if report.hint:
        lines.append(report.hint)
    lines.append("config block for other MCP clients:")
    lines.append(json.dumps(report.config_block, indent=2))
    return "\n".join(lines) + "\n"
