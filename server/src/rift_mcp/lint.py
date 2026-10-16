# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Server-side lint of agent scripts before they reach Revit (ADR-017, M2).

Ground truth for syntax stays the add-in's ``compile`` (IronPython 3.4 itself); this lint
catches, before anything is sent, what IronPython 3.4 rejects with a confusing message and the
Revit API pitfalls that turn a write into a mess. Errors stop ``run_modify`` (``LINT_ERROR``);
warnings travel back with the result. f-strings are accepted (P-D4).
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Any

import parso

GRAMMAR = parso.load_grammar(version="3.6")


@dataclass(frozen=True)
class Finding:
    code: str
    severity: str  # "error" | "warning"
    line: int
    message: str

    def to_dict(self) -> dict[str, Any]:
        return {
            "code": self.code,
            "severity": self.severity,
            "line": self.line,
            "message": self.message,
        }


@dataclass
class LintReport:
    findings: list[Finding] = field(default_factory=list)

    @property
    def errors(self) -> list[Finding]:
        return [f for f in self.findings if f.severity == "error"]

    @property
    def warnings(self) -> list[Finding]:
        return [f for f in self.findings if f.severity == "warning"]

    def to_dict(self) -> dict[str, Any]:
        return {
            "errors": [f.to_dict() for f in self.errors],
            "warnings": [f.to_dict() for f in self.warnings],
        }


#: (code, severity, pattern, message). Patterns run on each line with comments stripped.
RULES: tuple[tuple[str, str, re.Pattern[str], str], ...] = (
    (
        # parso's 3.6 grammar reports := as a bare syntax error: say what it is.
        "E002",
        "error",
        re.compile(r"[\w)\]]\s*:=|\(\s*\w+\s*:="),
        "the walrus operator := is not IronPython 3.4",
    ),
    (
        "E010",
        "error",
        re.compile(r"\bOpenAndActivateDocument\s*\("),
        "OpenAndActivateDocument is forbidden inside a run (ADR-007): use open_document",
    ),
    (
        "E011",
        "error",
        re.compile(r"\.(Save|SaveAs|Close|SynchronizeWithCentral|SaveCloudModel)\s*\("),
        "saving, closing or synchronising is blocked during a run (ADR-006): the run would fail",
    ),
    (
        "E012",
        "error",
        re.compile(r"^\s*(import|from)\s+(clr|ctypes|subprocess|socket|multiprocessing)\b"),
        "this module is refused by the add-in's import guard (ADR-016)",
    ),
    (
        "E013",
        "error",
        re.compile(r"\b(TaskDialog|MessageBox)\.Show\s*\("),
        "a modal dialog would block Revit under the agent's run: print instead",
    ),
    (
        "E014",
        "error",
        re.compile(r"\bIFailuresPreprocessor\b"),
        "a custom failures preprocessor hides failures from the guard (ADR-010)",
    ),
    (
        "W001",
        "warning",
        re.compile(r"\.IntegerValue\b"),
        "ElementId.IntegerValue is obsolete since Revit 2024: use .Value",
    ),
    (
        "W002",
        "warning",
        re.compile(r"\bElementId\(\s*\d+\s*\)"),
        "ElementId(<int literal>): ids differ between documents; look the element up instead",
    ),
    (
        "W003",
        "warning",
        re.compile(r"\bActiveView\b"),
        "ActiveView in a write depends on what the user looks at; name the view instead",
    ),
    (
        "W004",
        "warning",
        re.compile(r"\bTransaction\s*\(\s*\)"),
        "Transaction() needs the document and a name: Transaction(doc, 'what')",
    ),
    (
        "W005",
        "warning",
        re.compile(r"\bLoadFrom\b|\bAddReference"),
        "loading assemblies from a script is outside the guard's view",
    ),
)


def _strip_comment(line: str) -> str:
    # Good enough for a lint: a '#' inside a string only hides the rest of that line from the rules.
    out, quote = [], ""
    for ch in line:
        if quote:
            if ch == quote:
                quote = ""
        elif ch in "'\"":
            quote = ch
        elif ch == "#":
            break
        out.append(ch)
    return "".join(out)


def _syntax(script: str) -> list[Finding]:
    found: list[Finding] = []
    module = GRAMMAR.parse(script)
    for err in GRAMMAR.iter_errors(module):
        found.append(Finding("E001", "error", err.start_pos[0], f"syntax: {err.message}"))
    for node in _walk(module):
        kind = node.type
        line = node.start_pos[0]
        if kind == "namedexpr_test":
            found.append(
                Finding("E002", "error", line, "the walrus operator := is not IronPython 3.4")
            )
        elif kind == "async_stmt" or (kind == "keyword" and node.value in ("async", "await")):
            found.append(
                Finding("E003", "error", line, "async/await is not available in IronPython 3.4")
            )
        elif kind == "expr_stmt" and any(c.type == "annassign" for c in node.children):
            found.append(
                Finding("E004", "error", line, "variable annotations are not IronPython 3.4")
            )
    return found


def _walk(node: Any) -> Any:
    yield node
    for child in getattr(node, "children", ()):
        yield from _walk(child)


def lint(script: str) -> LintReport:
    report = LintReport()
    report.findings.extend(_syntax(script))
    for number, raw in enumerate(script.splitlines(), start=1):
        line = _strip_comment(raw)
        for code, severity, pattern, message in RULES:
            if pattern.search(line):
                report.findings.append(Finding(code, severity, number, message))
    # One finding per code and line is enough.
    seen: set[tuple[str, int]] = set()
    unique = []
    for f in report.findings:
        if (f.code, f.line) not in seen:
            seen.add((f.code, f.line))
            unique.append(f)
    report.findings = sorted(unique, key=lambda f: (f.line, f.code))
    return report
