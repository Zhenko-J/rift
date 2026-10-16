# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
# RIFT audit preset p2 (ADR-024): text note types and dimension types with their parameters.
# Port to IronPython 3.4 of the maintainer's extraction script p2. Parity rule: same keys, same value
# computations, same sort orders. Changes from the original: no file system (dump() hands the JSON text to
# the add-in's collector), every sort key goes through str(), bare except clauses are narrowed by the engine.
# Produces the document 02_notes_cotations.
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

def dump(name, obj):
    rift_collector.Put(name, json.dumps(obj, ensure_ascii=False))
    return name

# params     : {displayed label -> value}   label localised (Revit's language)
# params_bip : {BuiltInParameter -> value}  stable key, used for lookups
def params(el):
    d, b = {}, {}
    for p in el.Parameters:
        try:
            nm = p.Definition.Name
            if p.StorageType == StorageType.String:
                v = p.AsString()
            else:
                v = p.AsValueString()
            if v in (None, ""):
                continue
            d[nm] = v
            try:
                bip = p.Definition.BuiltInParameter
                if bip != BuiltInParameter.INVALID:
                    b[str(bip)] = v
            except:
                pass
        except:
            pass
    return d, b

data = {}

report_progress(10, "text note types")
tnt = []
for t in col(TextNoteType):
    pr, pb = params(t)
    tnt.append({"id": iid(t.Id), "name": gname(t), "params": pr, "params_bip": pb})
data["types_note_textuelle"] = sorted(tnt, key=lambda x: str(x["name"]))

report_progress(50, "dimension types")
dt = []
for t in col(DimensionType):
    nm = gname(t)
    if not nm:
        continue
    d = {"id": iid(t.Id), "name": nm}
    try:
        d["style"] = str(t.StyleType)
    except:
        pass
    d["params"], d["params_bip"] = params(t)
    dt.append(d)
data["types_cotation"] = sorted(dt, key=lambda x: (str(x.get("style", "")), str(x["name"])))

data["_document"] = doc.Title
data["_langue"] = str(doc.Application.Language)
data["_generated"] = str(datetime.datetime.now())
dump("02_notes_cotations", data)
report_progress(100, "done")
for k in data:
    if not k.startswith("_"):
        print("%-24s %d" % (k, len(data[k])))
