# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
# RIFT audit preset p6 (ADR-024): merges the documents 01..05 into 00_TOUT and adds, for each legend, the sheets
# that carry it. Port to IronPython 3.4 of the maintainer's merge script p6. Parity rule: same keys, same value
# computations, same sort orders. Changes from the original: the five documents are read from the add-in's
# collector (rift_collector.Text(name), filled by the presets that ran before in the same audit.all job) instead
# of files, dump() hands the JSON text back to the collector, bare except clauses are narrowed by the engine.
# Produces the document 00_TOUT. Runs only after p1-p5 (audit.all); on its own it merges nothing.
import json, datetime
from Autodesk.Revit.DB import *

def iid(x):
    try: return int(x.Value)
    except: return int(x.IntegerValue)

def dump(name, obj):
    rift_collector.Put(name, json.dumps(obj, ensure_ascii=False))
    return name

report_progress(10, "merge")
merged = {}
for fn in ["01_vues_lignes_cartouches", "02_notes_cotations", "03_pochages",
           "04_nomenclatures", "05_parametres"]:
    text = rift_collector.Text(fn)
    if text is None:
        continue
    d = json.loads(text)
    for k, v in d.items():
        if not k.startswith("_"):
            merged[k] = v

report_progress(50, "legends on sheets")
# legends -> sheets (through Viewport)
leg_sheets = {}
for vp in FilteredElementCollector(doc).OfClass(Viewport):
    try:
        v = doc.GetElement(vp.ViewId)
        if v and v.ViewType == ViewType.Legend:
            sh = doc.GetElement(vp.SheetId)
            leg_sheets.setdefault(v.Name, []).append("%s - %s" % (sh.SheetNumber, sh.Name))
    except:
        pass
for lg in merged.get("legendes", []):
    lg["on_sheets"] = leg_sheets.get(lg["name"], [])

data = {"document": doc.Title, "generated": str(datetime.datetime.now()),
        "langue": str(doc.Application.Language),
        "resume": dict((k, len(v)) for k, v in merged.items() if isinstance(v, list)),
        "data": merged}
dump("00_TOUT", data)
report_progress(100, "done")
print(json.dumps(data["resume"], indent=2, ensure_ascii=False))
