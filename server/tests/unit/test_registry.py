# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Plan test 17: the registry enforces ADR-012 and drives every generated listing."""

from __future__ import annotations

import dataclasses

import pytest

from rift_mcp import catalog, registry
from rift_mcp.registry import TOOLS, RegistryError, get, implemented, validate


def test_registry_is_valid_and_names_are_unique() -> None:
    validate()
    assert len({t.name for t in TOOLS}) == len(TOOLS)


def test_m1_to_m3_are_shipped_and_m4_in_part() -> None:
    shipped = [t.name for t in implemented()]
    assert shipped[:8] == [
        "get_context",
        "run_query",
        "audit_template",
        "audit_template_write",
        "run_modify",
        "record_change",
        "get_change_records",
        "cancel_job",
    ]
    assert {t.wave for t in implemented()} == {"M1", "M2", "M3", "M4"}
    m4 = {t.name for t in implemented() if t.wave == "M4"}
    assert m4 == {
        "list_targets",
        "switch_target",
        "export_schedule_csv",
        "show_element",
        "select_elements",
        "activate_view",
    }
    assert len([t for t in TOOLS if t.wave == "M3"]) == len(shipped) - 8 - len(m4)
    run_modify = get("run_modify")
    assert (run_modify.risk, run_modify.destructive, run_modify.remote_op) == (
        "never_auto",
        True,
        "run",
    )


@pytest.mark.parametrize(
    ("change", "message"),
    [
        ({"remote_op": "run"}, "not a fixed read op"),
        ({"read_only": False}, "must be read-only"),
        ({"risk": "ask", "destructive": True}, "never_auto"),
    ],
)
def test_adr012_rules_are_mechanical(change: dict[str, object], message: str) -> None:
    broken = dataclasses.replace(get("list_sheets"), **change)  # type: ignore[arg-type]
    with pytest.raises(RegistryError, match=message):
        validate((broken,))


def test_free_code_is_never_read_only() -> None:
    broken = dataclasses.replace(get("run_query"), read_only=True)
    with pytest.raises(RegistryError, match="free code"):
        validate((broken,))


def test_document_life_cycle_from_pyrevit_is_never_auto() -> None:
    for name in ("save_document", "close_document", "sync_with_central"):
        assert get(name).risk == "never_auto"
    for name in ("open_document", "launch_revit", "run_query"):
        assert get(name).risk == "ask"


def test_markdown_has_one_row_per_tool_prompt_and_resource() -> None:
    from rift_mcp.registry import PROMPTS, RESOURCES

    md = catalog.render_markdown(catalog.to_document())
    rows = [line for line in md.splitlines() if line.startswith("| `")]
    surfaces = [r for r in rows if "| prompt |" in r or "| resource |" in r]
    assert len(rows) - len(surfaces) == len(TOOLS)
    assert len(surfaces) == len(PROMPTS) + len(RESOURCES)


def test_readme_markers_are_required() -> None:
    with pytest.raises(ValueError, match="markers"):
        catalog.readme_with_table("# no markers\n", "table")


def test_prompts_and_resources_of_rvt_mcp_have_a_fate() -> None:
    """Coverage decision of 2026-10-13: every rvt-mcp prompt and resource is either planned
    or excluded on purpose (docs/plans/tool-catalogue/01-couverture-2026-10-13.md)."""
    from rift_mcp.registry import EXCLUDED, PROMPTS, RESOURCES, validate_surfaces

    validate_surfaces()
    planned = {o for item in (*PROMPTS, *RESOURCES) for o in item.origin}
    decided = planned | set(EXCLUDED)
    rvt_mcp_surfaces = {
        "rvt-mcp:revit_getting_started",
        "rvt-mcp:revit_pre_issue_check",
        "rvt-mcp:revit_model_audit",
        "rvt-mcp:revit_stairs",
        "rvt-mcp:revit_change",
        "rvt-mcp:Session Context",
    }
    # revit_drawing_layout waits for the RIFT / NORN FORGE split: the only one left undecided.
    assert rvt_mcp_surfaces <= decided
    assert "pyrevit:place_family" in EXCLUDED
    assert all(r.uri.startswith("rift://") for r in RESOURCES)


def test_a_prompt_cannot_take_over_an_excluded_counterpart(monkeypatch: pytest.MonkeyPatch) -> None:
    from rift_mcp import registry
    from rift_mcp.registry import PROMPTS, PromptSpec, validate_surfaces

    stairs = PromptSpec("stairs", "M4", "x", ("rvt-mcp:revit_stairs",))
    with pytest.raises(RegistryError, match="excluded"):
        validate_surfaces((*PROMPTS, stairs))
    # getting_started (M4) shipped while M3 was the current wave.
    monkeypatch.setattr(registry, "CURRENT_WAVE", "M3")
    shipped_early = dataclasses.replace(PROMPTS[3], status="implemented")
    with pytest.raises(RegistryError, match="not started"):
        validate_surfaces((shipped_early,))


def test_the_catalogue_lists_prompts_resources_and_exclusions() -> None:
    doc = catalog.to_document()
    assert [p["name"] for p in doc["prompts"]] == [
        "change",
        "model_audit",
        "pre_issue_check",
        "getting_started",
    ]
    assert doc["resources"][0]["uri"] == "rift://session/context"
    md = catalog.render_markdown(doc)
    assert "**MCP prompts and resources**" in md
    assert "`pyrevit:place_family`" in md


def test_writes_that_overwrite_are_annotated_destructive() -> None:
    """F-A1: the 11 typed writes that overwrite existing state carry destructiveHint."""
    by_name = {t.name: t for t in registry.TOOLS}
    assert len(registry.OVERWRITING_WRITES) == 11
    for name in registry.OVERWRITING_WRITES:
        spec = by_name[name]
        assert spec.destructive, name
        assert spec.risk == "never_auto", name
