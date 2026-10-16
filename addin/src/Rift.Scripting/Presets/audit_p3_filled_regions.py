# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
# RIFT audit preset p3 (ADR-024): filled region types and fill patterns.
# Port to IronPython 3.4 of the maintainer's extraction script p3. Parity rule: same keys, same value
# computations, same sort orders. Changes from the original: no file system (dump() hands the JSON text to
# the add-in's collector), every sort key goes through str(), bare except clauses are narrowed by the engine.
# Produces the document 03_pochages.
import json, datetime
from Autodesk.Revit.DB import *

def iid(x):
    try: return int(x.Value)
    except: return int(x.IntegerValue)

def col(cls):
    return FilteredElementCollector(doc).OfClass(cls)

def gname(el):
    try: return Element.Name.GetValue(el)
    except:
        try: return el.Name
        except: return None

def rgb(c):
    try:
        if c and c.IsValid:
            return "%d,%d,%d" % (c.Red, c.Green, c.Blue)
    except: pass
    return None

def dump(name, obj):
    rift_collector.Put(name, json.dumps(obj, ensure_ascii=False))
    return name

data = {}

report_progress(10, "filled region types")
fr = []
for t in col(FilledRegionType):
    d = {"id": iid(t.Id), "name": gname(t)}
    try: d["is_masking"] = t.IsMasking
    except: pass
    try: d["line_weight"] = t.LineWeight
    except: pass
    try: d["foreground_color"] = rgb(t.ForegroundPatternColor)
    except: pass
    try:
        fp = doc.GetElement(t.ForegroundPatternId)
        d["foreground_pattern"] = gname(fp) if fp else None
    except: pass
    try: d["background_color"] = rgb(t.BackgroundPatternColor)
    except: pass
    try:
        bp = doc.GetElement(t.BackgroundPatternId)
        d["background_pattern"] = gname(bp) if bp else None
    except: pass
    fr.append(d)
data["types_pochage"] = sorted(fr, key=lambda x: str(x["name"]))

report_progress(60, "fill patterns")
fpe = []
for e in col(FillPatternElement):
    try:
        fp = e.GetFillPattern()
        fpe.append({"id": iid(e.Id), "name": e.Name,
                    "target": str(fp.Target), "is_solid": fp.IsSolidFill})
    except:
        pass
data["motifs_remplissage"] = sorted(fpe, key=lambda x: (str(x["target"]), str(x["name"])))

data["_document"] = doc.Title
data["_generated"] = str(datetime.datetime.now())
dump("03_pochages", data)
report_progress(100, "done")
for k in data:
    if not k.startswith("_"):
        print("%-24s %d" % (k, len(data[k])))
