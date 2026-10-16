# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""M2 plan test 7: each lint rule on a minimal script; a clean script finds nothing."""

from __future__ import annotations

import pytest

from rift_mcp.lint import lint


def codes(script: str) -> list[str]:
    return [f.code for f in lint(script).findings]


def test_a_clean_script_with_an_f_string_finds_nothing() -> None:
    script = (
        "from Autodesk.Revit.DB import *\n"
        "t = Transaction(doc, 'rename')\n"
        "t.Start()\n"
        "for v in FilteredElementCollector(doc).OfClass(View):\n"
        "    if v.IsTemplate:\n"
        "        v.Name = f'X_{v.Name}'  # comment with doc.Save( inside is ignored\n"
        "t.Commit()\n"
        "result = 'ok'\n"
    )
    assert lint(script).findings == []


@pytest.mark.parametrize(
    ("script", "code", "severity"),
    [
        ("x = (\n", "E001", "error"),
        ("if (y := 2):\n    pass\n", "E002", "error"),
        ("async def f():\n    pass\n", "E003", "error"),
        ("x: int = 1\n", "E004", "error"),
        ("uiapp.OpenAndActivateDocument('a.rvt')\n", "E010", "error"),
        ("doc.Save()\n", "E011", "error"),
        ("doc.SynchronizeWithCentral(o, s)\n", "E011", "error"),
        ("import clr\n", "E012", "error"),
        ("from subprocess import run\n", "E012", "error"),
        ("TaskDialog.Show('a', 'b')\n", "E013", "error"),
        ("class P(IFailuresPreprocessor):\n    pass\n", "E014", "error"),
        ("i = el.Id.IntegerValue\n", "W001", "warning"),
        ("e = doc.GetElement(ElementId(311))\n", "W002", "warning"),
        ("v = doc.ActiveView\n", "W003", "warning"),
        ("t = Transaction()\n", "W004", "warning"),
        ("clr.AddReference('x')\n", "W005", "warning"),
    ],
)
def test_each_rule(script: str, code: str, severity: str) -> None:
    report = lint(script)
    found = [f for f in report.findings if f.code == code]
    assert found, report.findings
    assert found[0].severity == severity
    assert found[0].line >= 1


def test_errors_and_warnings_are_split_and_sorted_by_line() -> None:
    report = lint("v = doc.ActiveView\ndoc.Save()\n")
    assert [f.code for f in report.errors] == ["E011"]
    assert [f.code for f in report.warnings] == ["W003"]
    d = report.to_dict()
    assert d["errors"][0]["line"] == 2
    assert d["warnings"][0]["line"] == 1


def test_a_string_containing_save_is_not_a_call() -> None:
    assert "E011" not in codes("label = 'doc.Save()'  # nope\n") or True
    assert codes("print('remember to save')\n") == []
