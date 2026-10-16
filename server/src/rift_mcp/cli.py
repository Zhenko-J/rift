# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Command-line entry point (ADR-013, M1 tranche 6).

- ``serve`` — the MCP server over stdio (what the MCP client launches).
- ``install claude [--scope user|local|project] [--print]`` — register the server through
  ``claude mcp add``; prints the command and the config block for other clients.
- ``tools``, ``permissions``, ``schemas`` — listings generated from the registry: the README
  table and ``docs/pamphlet/tools.json``, the Claude Code permission rules
  (``docs/pamphlet/permissions.claude.json``), the input schemas of the shipped tools
  (``docs/pamphlet/schemas.json``). Each takes ``--check`` (CI) and ``--write``.
- ``doctor`` — which Revit instance the server would talk to, and why the others are ignored.
- ``selftest [--write] [--keep]`` — acceptance checks of M1-M3 against the open Revit (reads;
  write tools as dry runs on a SANDBOX copy with ``--write``; one kept change with ``--keep``).
- ``purge [--days N]`` — delete run journals older than N days (default: every journal).
- ``pause`` / ``resume`` — commanded from the Revit ribbon (ADR-029), not from here.
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
import time
from pathlib import Path

from . import PROTOCOL_VERSION, __version__

NOT_FROM_CLI = (
    "{name}: the server is paused and resumed from the RIFT panel of the Revit ribbon "
    "(ADR-029); nothing to do from the command line"
)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="rift-mcp")
    parser.add_argument(
        "--version",
        action="version",
        version=f"rift-mcp {__version__} (protocol {PROTOCOL_VERSION})",
    )
    sub = parser.add_subparsers(dest="command")

    sub.add_parser("serve", help="run the MCP server over stdio")

    install = sub.add_parser("install", help="register the server in an MCP client")
    install.add_argument("target", nargs="?", default="claude", choices=("claude",))
    install.add_argument("--scope", default="user", choices=("user", "local", "project"))
    install.add_argument(
        "--print", dest="print_only", action="store_true", help="print the command, do not run it"
    )

    for name, help_text in (
        ("tools", "the tool catalogue (README table, docs/pamphlet/tools.json)"),
        ("permissions", "Claude Code permission rules of the shipped tools"),
        ("schemas", "input schemas of the shipped tools"),
    ):
        p = sub.add_parser(name, help=help_text)
        p.add_argument("--format", choices=("json", "md", "claude"), default=None)
        p.add_argument("--check", action="store_true", help="fail when the committed file is stale")
        p.add_argument("--write", action="store_true", help="update the committed file in place")

    sub.add_parser("doctor", help="list the Revit instances seen and the one that would be used")

    selftest = sub.add_parser("selftest", help="acceptance checks against the open Revit")
    selftest.add_argument("--write", action="store_true", help="also dry-run write tools (SANDBOX)")
    selftest.add_argument("--keep", action="store_true", help="keep one change (one undo entry)")
    selftest.add_argument("--pid", type=int, default=None)

    purge = sub.add_parser("purge", help="delete run journals under the data folder")
    purge.add_argument("--days", type=int, default=0, help="keep journals younger than N days")

    sub.add_parser("pause")
    sub.add_parser("resume")
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    if args.command is None:
        parser.print_help()
        return 0
    if args.command == "serve":
        from .server import main_serve

        return main_serve()
    if args.command == "install":
        return install_command(args.scope, print_only=args.print_only)
    if args.command == "tools":
        return listing_command("tools", args.format or "md", check=args.check, write=args.write)
    if args.command == "permissions":
        fmt = args.format or "claude"
        if fmt != "claude":
            print("permissions: only --format claude is supported", file=sys.stderr)
            return 2
        return listing_command("permissions", fmt, check=args.check, write=args.write)
    if args.command == "schemas":
        return listing_command("schemas", args.format or "json", check=args.check, write=args.write)
    if args.command == "doctor":
        return doctor()
    if args.command == "selftest":
        return selftest_command(write=args.write, keep=args.keep, pid=args.pid)
    if args.command == "purge":
        return purge(args.days)
    print(NOT_FROM_CLI.format(name=args.command), file=sys.stderr)
    return 2


def install_command(scope: str, *, print_only: bool) -> int:
    from .install import install_claude, render

    report = install_claude(scope, print_only=print_only)
    sys.stdout.write(render(report))
    return 0 if report.ok else 1


def listing_command(kind: str, fmt: str, *, check: bool, write: bool) -> int:
    from . import catalog
    from .registry import RegistryError

    try:
        if check:
            stale = catalog.check(kind)
            for item in stale:
                print(f"stale: {item}", file=sys.stderr)
            return 1 if stale else 0
        if write:
            print(f"written: {catalog.write(kind, fmt)}")
            return 0
        sys.stdout.write(catalog.render(kind, fmt))
    except RegistryError as exc:
        print(f"registry: {exc}", file=sys.stderr)
        return 1
    return 0


def selftest_command(*, write: bool, keep: bool, pid: int | None) -> int:
    """Runs the acceptance checks against the live Revit and prints the report (JSON)."""
    import anyio

    from .errors import RiftError
    from .selftest import selftest
    from .server import default_client_factory

    try:
        client = default_client_factory(pid)
    except RiftError as exc:
        print(json.dumps({"error": exc.to_dict()}, indent=2))
        return 1
    client.client_name = "rift-selftest"
    report = anyio.run(lambda: selftest(client, write=write, keep=keep))
    print(json.dumps(report.to_dict(), indent=2, ensure_ascii=False))
    return 1 if report.failed else 0


def doctor() -> int:
    """Lists the discovery files, probes each pipe with an unsigned health frame (no secret
    involved) and says which instance ``get_context`` would use — or why none."""
    from .discovery import choose_target, instances_dir, list_instances, verify_alive
    from .errors import RiftError
    from .transport import pipe

    report: dict[str, object] = {
        "server": {"version": __version__, "protocol": PROTOCOL_VERSION},
        "instances_dir": str(instances_dir()),
        "instances": [],
    }
    instances = list_instances()
    entries: list[dict[str, object]] = []
    live = []
    for inst in instances:
        alive = verify_alive(inst)
        entry: dict[str, object] = {
            "pid": inst.pid,
            "pipe": inst.pipe,
            "revit": inst.revit_version,
            "build": inst.build,
            "addin": inst.addin_version,
            "protocol": inst.protocol,
            "generation": inst.generation,
            "capabilities": list(inst.capabilities),
            # pid alive and created when the discovery file says (a reused pid is not a target)
            "alive": alive,
        }
        problems: list[str] = []
        if not alive:
            problems.append("process gone or not the one that wrote the record (stale file)")
        if inst.protocol.split(".")[0] != PROTOCOL_VERSION.split(".")[0]:
            problems.append(f"protocol {inst.protocol} differs from {PROTOCOL_VERSION}")
        if not inst.key_path.is_file():
            problems.append("secret file missing")
        try:
            health = pipe.health(inst.pipe)
            entry["health"] = health.body
            entry["pipe_pid_matches"] = health.server_pid == inst.pid
            if health.server_pid != inst.pid:
                problems.append("the pipe is served by another process")
        except OSError as exc:
            entry["health_error"] = str(exc)
            problems.append("pipe unreachable")
        entry["problems"] = problems
        entries.append(entry)
        if alive and not problems:
            live.append(inst)
    report["instances"] = entries
    try:
        report["target"] = {"pid": choose_target(live).pid, "reason": "the only live instance"}
    except RiftError as exc:
        report["target"] = None
        report["error"] = exc.to_dict()
        if exc.code == "NO_TARGET" and not instances:
            report["hint"] = (
                "no discovery file: is Revit running with the RIFT add-in installed "
                "(install.ps1) and loaded (NORN tab, RIFT panel)?"
            )
    claude = shutil.which("claude")
    report["claude_cli"] = claude or "not on PATH (rift-mcp install claude --print)"
    print(json.dumps(report, indent=2))
    return 0 if report["target"] else 1


def purge(days: int) -> int:
    """Deletes ``runs/<folder>`` journals under the data folder older than ``days`` days."""
    from .discovery import data_root

    runs = data_root() / "runs"
    if not runs.is_dir():
        print(f"nothing to purge under {runs}")
        return 0
    cutoff = time.time() - days * 86400
    deleted = 0
    for folder in sorted(runs.iterdir()):
        if not folder.is_dir():
            continue
        try:
            if folder.stat().st_mtime <= cutoff:
                shutil.rmtree(folder)
                deleted += 1
        except OSError as exc:
            print(f"skipped {folder.name}: {exc}", file=sys.stderr)
    index = runs / "runs-index.jsonl"
    if days == 0 and index.is_file():
        Path(index).unlink(missing_ok=True)
    print(f"purged {deleted} run folder(s) under {runs}")
    return 0


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
