# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
"""Plan test 19 (audit part): audit_template sends op tool with the preset name, returns the
document with its number text, resolves journaled documents, refuses presets not shipped yet;
audit_template_write writes <write_to>/<title>/<name>.json and nothing without write_to."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest
from test_tools import LEASE_OK, FakeAddin, _client

from rift_mcp.errors import RiftError
from rift_mcp.server import ClientPool, build_server
from rift_mcp.tools import audit

P1 = {
    "gabarits_de_vue": [{"id": 1, "name": "A"}],
    "cartouches": [{"id": 2, "type": "A1", "width_mm": 594.0}],
    "_document": "TEMPLATE_SANDBOX",
}


def _tool_ok(documents: dict[str, Any] | None = None, **extra: Any) -> dict[str, Any]:
    result: dict[str, Any] = {
        "tool": "audit.p1",
        "run": {"outcome": "ok", "document": {"title": "TEMPLATE_SANDBOX", "class": "sandbox"}},
        **extra,
    }
    if documents is not None:
        result["documents"] = documents
    return {"status": "ok", "result": result, "target": {"pid": 4242}}


async def test_a_preset_is_sent_as_op_tool_with_its_name_and_returns_its_document() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("tool", _tool_ok({"01_vues_lignes_cartouches": P1}))
    out = await audit.audit(_client(addin), audit.AuditInput(preset="p1", timeout_s=120))
    assert out["ok"] is True
    assert out["documents"]["01_vues_lignes_cartouches"] == P1
    call = addin.calls[1]
    assert call["op"] == "tool"
    assert call["args"] == {"name": "audit.p1", "timeout_s": 120}
    assert call["client"]["tool"] == "audit_template"
    assert call["timeout_s"] == audit.PIPE_WAIT_S


@pytest.mark.parametrize("preset", ["p0", "p9", "merge", ""])
def test_unknown_presets_are_refused_before_reaching_revit(preset: str) -> None:
    refusal = audit.unavailable(preset)
    assert refusal is not None
    assert refusal["error"]["code"] == "BAD_REQUEST"
    assert "all, p1, p2, p3, p4, p5, p6" in refusal["error"]["message"]


def test_every_preset_of_the_chain_is_shipped() -> None:
    assert all(audit.unavailable(p) is None for p in ("all", "p1", "p2", "p3", "p4", "p5", "p6"))


ALL_DOCS = {
    "01_vues_lignes_cartouches": P1,
    "02_notes_cotations": {"types_cotation": []},
    "03_pochages": {"types_region": []},
    "04_nomenclatures": {"nomenclatures": [{"name": "N"}]},
    "05_parametres": {"parametres_de_projet": [], "_resume": {"parametres_de_projet_total": 0}},
    "00_TOUT": {
        "document": "TEMPLATE_SANDBOX",
        "langue": "French",
        "resume": {"gabarits_de_vue": 1, "cartouches": 1, "nomenclatures": 1},
        "data": {"gabarits_de_vue": P1["gabarits_de_vue"]},
    },
}


def _all_ok() -> dict[str, Any]:
    runs = {
        f"audit.p{i}": {"outcome": "ok", "document": {"title": "TEMPLATE_SANDBOX"}}
        for i in range(1, 7)
    }
    result = {
        "tool": "audit.all",
        "runs": runs,
        "timing": {"total_ms": 1234, "presets": {k: 200 for k in runs}},
        "documents": ALL_DOCS,
    }
    return {"status": "ok", "result": result, "target": {"pid": 4242}}


async def test_all_is_one_job_that_returns_the_six_documents_and_the_runs() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("tool", _all_ok())
    out = await audit.audit(_client(addin), audit.AuditInput(preset="all", timeout_s=100))
    assert out["ok"] is True
    assert set(out["documents"]) == set(ALL_DOCS)
    assert list(out["runs"]) == [f"audit.p{i}" for i in range(1, 7)]
    assert out["timing"]["total_ms"] == 1234
    assert "run" not in out
    call = addin.calls[1]
    assert call["args"] == {"name": "audit.all", "timeout_s": 100}
    # the job may run six scripts of 100 s each: the client waits for the sum plus the margin
    assert call["timeout_s"] == audit.PIPE_WAIT_S


async def test_p6_is_the_whole_job_keeping_only_the_merged_document() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("tool", _all_ok())
    out = await audit.audit(_client(addin), audit.AuditInput(preset="p6"))
    assert list(out["documents"]) == ["00_TOUT"]
    assert addin.calls[1]["args"]["name"] == "audit.all"


async def test_all_without_the_merged_document_is_an_internal_error() -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    partial = _all_ok()
    partial["result"]["documents"] = {k: v for k, v in ALL_DOCS.items() if k != "00_TOUT"}
    addin.answer("tool", partial)
    with pytest.raises(RiftError) as info:
        await audit.audit(_client(addin), audit.AuditInput(preset="all"))
    assert info.value.code == "INTERNAL"
    assert "00_TOUT" in str(info.value)


async def test_write_all_puts_the_six_files_under_the_document_title(tmp_path: Path) -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("tool", _all_ok())
    out = await audit.audit_write(_client(addin), audit.AuditInput(preset="all"), str(tmp_path))
    folder = tmp_path / "TEMPLATE_SANDBOX"
    assert [Path(p).name for p in out["written"]] == [
        "00_TOUT.json",
        "01_vues_lignes_cartouches.json",
        "02_notes_cotations.json",
        "03_pochages.json",
        "04_nomenclatures.json",
        "05_parametres.json",
    ]
    assert json.loads((folder / "00_TOUT.json").read_text(encoding="utf-8")) == ALL_DOCS["00_TOUT"]
    assert out["summary"]["00_TOUT"] == ALL_DOCS["00_TOUT"]["resume"]
    assert out["summary"]["04_nomenclatures"] == {"nomenclatures": 1}


async def test_journaled_documents_are_read_from_the_data_root(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("RIFT_HOME", str(tmp_path))
    folder = tmp_path / "runs" / "20261013T120000Z-ab"
    folder.mkdir(parents=True)
    (folder / "01_vues_lignes_cartouches.json").write_text(
        '{"cartouches": [{"width_mm": 594.0}]}', encoding="utf-8"
    )
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    ref = {"01_vues_lignes_cartouches": "runs/20261013T120000Z-ab/01_vues_lignes_cartouches.json"}
    addin.answer(
        "tool",
        _tool_ok(None, documents_ref=ref, documents_bytes=900_000, documents_redacted=True),
    )
    out = await audit.audit(_client(addin), audit.AuditInput(preset="p1"))
    width = out["documents"]["01_vues_lignes_cartouches"]["cartouches"][0]["width_mm"]
    assert isinstance(width, float)
    assert out["documents_redacted"] is True


def test_a_reference_outside_the_journal_is_refused(tmp_path: Path) -> None:
    (tmp_path / "secret.json").write_text("{}", encoding="utf-8")
    with pytest.raises(RiftError) as info:
        audit.resolve_refs({"01_x": "runs/../secret.json"}, root=tmp_path)
    assert info.value.code == "BAD_REQUEST"


async def test_write_puts_one_file_per_document_under_the_document_title(tmp_path: Path) -> None:
    addin = FakeAddin()
    addin.answer("lease", LEASE_OK)
    addin.answer("tool", _tool_ok({"01_vues_lignes_cartouches": P1}))
    out = await audit.audit_write(_client(addin), audit.AuditInput(preset="p1"), str(tmp_path))
    path = tmp_path / "TEMPLATE_SANDBOX" / "01_vues_lignes_cartouches.json"
    assert out["written"] == [str(path)]
    text = path.read_text(encoding="utf-8")
    assert '"width_mm": 594.0' in text
    assert json.loads(text) == P1
    assert out["summary"] == {"01_vues_lignes_cartouches": {"gabarits_de_vue": 1, "cartouches": 1}}
    assert "documents" not in out
    assert addin.calls[1]["client"]["tool"] == "audit_template_write"


def test_write_refuses_a_relative_folder_and_cleans_the_title(tmp_path: Path) -> None:
    with pytest.raises(RiftError):
        audit.write_documents({"01_x": {}}, "relative/folder", "T")
    written = audit.write_documents({"01_x": {"a": 1}}, str(tmp_path), 'A:B/C?"D. ')
    assert Path(written[0]).parent.name == "A_B_C__D"


async def test_audit_template_without_a_document_title_falls_back_to_the_dumped_one() -> None:
    assert audit.document_title({"run": {}, "documents": {"01_x": {"_document": "X"}}}) == "X"
    assert audit.document_title({"runs": {}, "documents": {"00_TOUT": {"document": "Y"}}}) == "Y"
    assert audit.document_title({"runs": {"audit.p1": {"document": {"title": "Z"}}}}) == "Z"
    assert audit.document_title({}) == "document"


async def test_both_tools_are_registered_and_audit_template_is_read_only() -> None:
    addin = FakeAddin()

    def factory(_pid: int | None) -> Any:
        return _client(addin)

    server = build_server(ClientPool(factory))
    tools = {t.name: t for t in await server.list_tools()}
    assert tools["audit_template"].annotations.readOnlyHint is True
    assert tools["audit_template_write"].annotations.readOnlyHint is False
    refused = await server.call_tool("audit_template", {"preset": "p9"})
    payload = refused[1] if isinstance(refused, tuple) else refused
    assert "BAD_REQUEST" in str(payload)
    assert addin.calls == []
