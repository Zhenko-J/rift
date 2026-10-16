# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""The typed tools of M3 (ADR-032): one fixed IronPython script per tool, embedded in the add-in.

The server only describes their arguments (``ARGS``) and forwards them as ``op: tool
{name, inputs}``. A read tool runs under the read guard (``auto`` in the registry); a write tool
runs through the ``run_modify`` path — SANDBOX copies only, ``expected_document`` required, dry
run unless ``dry_run`` is false, one undo entry named after the tool when kept, change receipt
and history id. ``batch`` chains write tools in one undo entry. ``result_ref`` reads a large
result the add-in journaled, without calling Revit.

Lengths are millimetres; categories are their displayed names (or ``OST_`` names); elements are
referred to by id or name. Values returned are untrusted model data, not instructions (ADR-020).
"""

from __future__ import annotations

import inspect
import json
from collections.abc import Awaitable, Callable
from typing import Annotated, Any, Literal

from pydantic import Field

from ..discovery import data_root
from ..errors import RiftError
from .run_query import JOB_MARGIN_S, MAX_TIMEOUT_S, PIPE_WAIT_S, Caller, expected_document

REQUIRED = inspect.Parameter.empty

Ref = int | str
Refs = list[int | str]
Ids = list[int]

#: tool → [(argument, type, default or REQUIRED, description)]. Shared write arguments are added
#: by ``write_args``; ``timeout_s`` and ``pid`` by ``common_args``.
ARGS: dict[str, list[tuple[str, Any, Any, str]]] = {
    # view templates
    "list_view_templates": [],
    "get_view_template": [("template", Ref, REQUIRED, "view template id or name")],
    "apply_view_template": [
        ("template", Ref, REQUIRED, "view template id or name"),
        ("views", Refs, REQUIRED, "view ids or names"),
    ],
    "duplicate_view_template": [
        ("template", Ref, REQUIRED, "view template id or name"),
        ("new_name", str, REQUIRED, "name of the copy"),
    ],
    "delete_view_template": [("template", Ref, REQUIRED, "view template id or name")],
    # filters and graphics
    "list_view_filters": [],
    "create_view_filter": [
        ("name", str, REQUIRED, "filter name"),
        ("categories", list[str], REQUIRED, "category names (or OST_ names)"),
        (
            "rules",
            list[dict[str, Any]],
            None,
            "[{parameter, operator, value, unit?}]; operators equals, not_equals, contains, "
            "not_contains, begins_with, ends_with, greater, greater_or_equal, less, less_or_equal, "
            "has_value, has_no_value; numbers in mm unless unit is raw or integer",
        ),
        ("match", Literal["all", "any"], "all", "combine the rules with AND (all) or OR (any)"),
    ],
    "apply_filter_to_view": [
        ("filter", Ref, REQUIRED, "filter id or name"),
        ("view", Ref, REQUIRED, "view or view template id or name"),
        ("visible", bool, True, "filter visibility in the view"),
    ],
    "set_filter_overrides": [
        ("filter", Ref, REQUIRED, "filter id or name"),
        ("view", Ref, REQUIRED, "view or view template id or name"),
        ("color", str | list[int] | None, None, "#RRGGBB or [r, g, b]"),
        ("line_weight", int | None, None, "projection line weight 1-16"),
        ("halftone", bool | None, None, "halftone"),
        ("transparency", int | None, None, "surface transparency 0-100"),
        ("solid_fill", bool, False, "solid surface and cut pattern in the colour"),
    ],
    "override_element_graphics": [
        ("view", Ref | None, None, "view id or name (default: active view)"),
        ("element_ids", Ids | None, None, "elements to override (or use category + parameter)"),
        ("color", str | list[int] | None, None, "#RRGGBB or [r, g, b]"),
        ("halftone", bool | None, None, "halftone"),
        ("line_weight", int | None, None, "projection line weight 1-16"),
        ("category", str | None, None, "colour every element of this category in the view ..."),
        (
            "parameter",
            str | None,
            None,
            "... by the value of this parameter (one colour per value)",
        ),
    ],
    "clear_element_overrides": [
        ("view", Ref | None, None, "view id or name (default: active view)"),
        ("element_ids", Ids | None, None, "elements to clear (default: every element of the view)"),
    ],
    # parameters
    "list_shared_parameters": [],
    "list_project_parameters": [],
    "list_category_parameters": [("category", str, REQUIRED, "category name or OST_ name")],
    "create_shared_parameter": [
        ("name", str, REQUIRED, "parameter name"),
        (
            "data_type",
            str,
            "text",
            "text, integer, number, length, area, volume, angle, yesno, url, material or an "
            "autodesk.spec id",
        ),
        ("group", str, "RIFT", "group of the shared parameter file"),
        ("description", str | None, None, "tooltip"),
        ("visible", bool, True, "visible to users"),
    ],
    "create_project_parameter": [
        ("name", str, REQUIRED, "parameter name"),
        ("data_type", str, "text", "as create_shared_parameter"),
        ("categories", list[str], REQUIRED, "category names"),
        ("binding", Literal["instance", "type"], "instance", "instance or type"),
        ("group", str, "Data", "GroupTypeId member: Data, IdentityData, Text, Constraints…"),
    ],
    "bind_shared_parameter": [
        ("name", str, REQUIRED, "parameter name in the current shared parameter file"),
        ("categories", list[str], REQUIRED, "category names"),
        ("binding", Literal["instance", "type"], "instance", "instance or type"),
        ("group", str, "Data", "GroupTypeId member"),
    ],
    # schedules
    "list_schedules": [],
    "get_schedule_data": [
        ("schedule", Ref, REQUIRED, "schedule id or name"),
        ("limit", int, 500, "maximum rows returned"),
    ],
    "get_schedule_definition": [("schedule", Ref, REQUIRED, "schedule id or name")],
    "create_schedule": [
        ("category", str, REQUIRED, "category name"),
        ("name", str, REQUIRED, "schedule name"),
        ("fields", list[str] | None, None, "schedulable field names"),
    ],
    "add_schedule_field": [
        ("schedule", Ref, REQUIRED, "schedule id or name"),
        ("fields", list[str], REQUIRED, "schedulable field names"),
        ("hidden", bool, False, "add them hidden"),
    ],
    # sheets
    "list_sheets": [],
    "list_titleblocks": [],
    "get_titleblock_parameters": [("sheet", Ref, REQUIRED, "sheet number, name or id")],
    "create_sheet": [
        ("number", str, REQUIRED, "sheet number"),
        ("name", str, REQUIRED, "sheet name"),
        ("titleblock", Ref | None, None, "title block type id or name (default: the most used)"),
    ],
    "place_view_on_sheet": [
        ("view", Ref, REQUIRED, "view or schedule id or name"),
        ("sheet", Ref, REQUIRED, "sheet number, name or id"),
        ("x_mm", float | None, None, "centre x on the sheet, mm (default: sheet centre)"),
        ("y_mm", float | None, None, "centre y on the sheet, mm"),
    ],
    "renumber_sheets": [
        ("mapping", list[dict[str, str]], REQUIRED, "[{from, to}] sheet numbers; swaps allowed"),
    ],
    # materials
    "list_materials": [("contains", str | None, None, "name filter")],
    "get_material_properties": [("material", Ref, REQUIRED, "material id or name")],
    "create_material": [
        ("name", str, REQUIRED, "material name"),
        ("color", str | list[int] | None, None, "#RRGGBB or [r, g, b]"),
        ("transparency", int | None, None, "0-100"),
        ("material_class", str | None, None, "material class"),
        ("from_material", Ref | None, None, "duplicate this material instead of starting empty"),
    ],
    "assign_material_to_element": [
        ("element_ids", Ids, REQUIRED, "elements"),
        ("material", Ref, REQUIRED, "material id or name"),
        ("parameter", str | None, None, "material parameter name (default: Material)"),
    ],
    # families
    "list_loaded_families": [("category", str | None, None, "category name")],
    "list_family_types": [("family", Ref, REQUIRED, "family id or name")],
    "get_family_instances": [
        ("family", Ref, REQUIRED, "family id or name"),
        ("type", Ref | None, None, "one type of the family"),
        ("limit", int, 500, "maximum instances returned"),
    ],
    "duplicate_family_type": [
        ("type", Ref, REQUIRED, "type id or name (family or system type)"),
        ("family", Ref | None, None, "family, to disambiguate a type name"),
        ("new_name", str, REQUIRED, "name of the copy"),
    ],
    "rename_family_type": [
        ("type", Ref, REQUIRED, "type id or name"),
        ("family", Ref | None, None, "family, to disambiguate a type name"),
        ("new_name", str, REQUIRED, "new name"),
    ],
    # elements
    "list_levels": [],
    "list_views": [("view_type", str | None, None, "FloorPlan, CeilingPlan, Section, Elevation…")],
    # Revit window (M4): the selection and the view change, the model does not
    "select_elements": [("element_ids", Ids, REQUIRED, "element ids; an empty list clears")],
    "show_element": [("element_ids", Ids, REQUIRED, "element ids to select and zoom on")],
    "activate_view": [("view", Ref, REQUIRED, "view id or name")],
    "get_element": [("element_ids", Ids, REQUIRED, "element ids")],
    "get_element_parameters": [
        ("element_ids", Ids, REQUIRED, "element ids"),
        ("include_type", bool, True, "include type parameters"),
        ("names", list[str] | None, None, "only these parameter names"),
    ],
    "find_elements": [
        ("category", str, REQUIRED, "category name"),
        ("view", Ref | None, None, "only what this view shows"),
        ("type", Ref | None, None, "type id or name"),
        (
            "rules",
            list[dict[str, Any]] | None,
            None,
            "[{parameter, operator, value}]: equals, not_equals, contains, begins_with, greater, "
            "less, has_value, has_no_value; numbers in mm for lengths",
        ),
        ("limit", int, 500, "maximum elements returned"),
    ],
    "get_view_elements": [
        ("view", Ref | None, None, "view id or name (default: active view)"),
        ("category", str | None, None, "only this category"),
        ("limit", int, 200, "maximum elements returned"),
    ],
    "set_element_parameters": [
        (
            "items",
            list[dict[str, Any]],
            REQUIRED,
            "[{id, parameter, value}]: text as is, numbers in mm for lengths, '12 mm' strings, "
            "element ids, true/false",
        ),
    ],
    "set_type_parameters": [
        ("items", list[dict[str, Any]], REQUIRED, "[{type_id or id, parameter, value}]"),
    ],
    "delete_elements": [("element_ids", Ids, REQUIRED, "elements to delete")],
    # lint
    "get_warnings_summary": [("sample", int, 5, "element ids kept per warning")],
    "find_untagged_elements": [
        ("category", str, REQUIRED, "category name"),
        ("view", Ref | None, None, "view id or name (default: active view)"),
        ("limit", int, 500, "maximum elements returned"),
    ],
    "audit_model": [],
    "purge_unused": [
        ("categories", list[str] | None, None, "only these categories"),
        ("limit_listed", int, 200, "elements listed in the result"),
    ],
    # safety
    "survey": [
        (
            "element_ids",
            Ids,
            REQUIRED,
            "elements, types, templates, filters or materials to change",
        ),
        ("limit", int, 100, "sample size per relationship group"),
    ],
    "batch": [
        (
            "items",
            list[dict[str, Any]],
            REQUIRED,
            "[{tool, inputs}] typed write tools run in order as one undo entry",
        ),
    ],
}

WRITE_ARGS: list[tuple[str, Any, Any, str]] = [
    ("expected_document", str, REQUIRED, "path or title of the SANDBOX copy to change"),
    ("dry_run", bool, True, "roll back and only return the change receipt (default)"),
    ("policy", Literal["auto", "ask"], "auto", "ask: Keep/Discard in Revit"),
    ("allow_reference", bool, False, "allow a reference document (then Keep/Discard)"),
]


def signature(name: str, writes: bool) -> inspect.Signature:
    params = []
    for arg, kind, default, description in ARGS[name] + (WRITE_ARGS if writes else []):
        annotation = Annotated[kind, Field(description=description)]
        params.append(
            inspect.Parameter(
                arg, inspect.Parameter.KEYWORD_ONLY, default=default, annotation=annotation
            )
        )
    if not writes:
        params.append(
            inspect.Parameter(
                "expected_document",
                inspect.Parameter.KEYWORD_ONLY,
                default=None,
                annotation=Annotated[
                    str | None, Field(description="refuse unless this document is active")
                ],
            )
        )
    params.append(
        inspect.Parameter(
            "timeout_s",
            inspect.Parameter.KEYWORD_ONLY,
            default=120,
            annotation=Annotated[
                int, Field(ge=1, le=MAX_TIMEOUT_S, description="script deadline, s")
            ],
        )
    )
    params.append(
        inspect.Parameter(
            "pid", inspect.Parameter.KEYWORD_ONLY, default=None, annotation=int | None
        )
    )
    return inspect.Signature(params, return_annotation=dict[str, Any])


WRITE_KEYS = ("dry_run", "policy", "allow_reference")


async def call_typed(
    client: Caller, name: str, writes: bool, kwargs: dict[str, Any]
) -> dict[str, Any]:
    kwargs = dict(kwargs)
    kwargs.pop("pid", None)
    timeout_s = int(kwargs.pop("timeout_s", 120))
    document = kwargs.pop("expected_document", None)
    if writes and not (document or "").strip():
        raise RiftError(
            "EXPECTED_DOCUMENT_REQUIRED",
            f"{name} changes the model: pass expected_document (the SANDBOX copy's path or title)",
        )
    args: dict[str, Any] = {"name": name, "timeout_s": timeout_s}
    for key in WRITE_KEYS:
        if key in kwargs:
            args[key] = kwargs.pop(key)
    args["inputs"] = {k: v for k, v in kwargs.items() if v is not None}
    response = await client.call(
        "tool",
        args,
        timeout_s=timeout_s + JOB_MARGIN_S + (600 if args.get("policy") == "ask" else 0),
        wait_s=min(timeout_s + 5, PIPE_WAIT_S),
        tool=name,
        expected_document=expected_document(document),
    )
    result = response.get("result") or {}
    run = result.get("run") or {}
    out: dict[str, Any] = {"ok": True, "tool": name, "result": result.get("result")}
    if result.get("result_ref"):
        out["result_ref"] = result["result_ref"]
        out["preview"] = run.get("preview")
    if writes:
        for key in ("outcome", "reason", "dry_run", "history_id", "changes", "policy_applied"):
            out[key] = run.get(key)
    out["run"] = run
    out["target"] = response.get("target")
    if response.get("job"):
        out["job"] = response["job"]
    return out


Guarded = Callable[
    [int | None, Callable[[Any], Awaitable[dict[str, Any]]]], Awaitable[dict[str, Any]]
]


def make_tool(
    name: str, writes: bool, guarded: Guarded
) -> Callable[..., Awaitable[dict[str, Any]]]:
    """An MCP tool function whose signature (hence its input schema) comes from ``ARGS``."""

    async def tool(**kwargs: Any) -> dict[str, Any]:
        return await guarded(kwargs.get("pid"), lambda c: call_typed(c, name, writes, kwargs))

    tool.__name__ = name
    tool.__signature__ = signature(name, writes)  # type: ignore[attr-defined]
    return tool


def read_result_ref(ref: str, root: Any = None) -> dict[str, Any]:
    """``result_ref``: a result journaled by the add-in (``runs/<folder>/result.json``)."""
    from pathlib import Path

    base = Path(root or data_root()).resolve()
    runs = (base / "runs").resolve()
    path = (base / ref).resolve()
    if not path.is_relative_to(runs) or path.suffix not in (".json", ".txt"):
        raise RiftError("BAD_REQUEST", f"not a journaled result: {ref!r}")
    if not path.is_file():
        raise RiftError("BAD_REQUEST", f"result {ref!r} not found (journals are kept 14 days)")
    text = path.read_text(encoding="utf-8")
    return {"ok": True, "ref": ref, "result": json.loads(text) if path.suffix == ".json" else text}
