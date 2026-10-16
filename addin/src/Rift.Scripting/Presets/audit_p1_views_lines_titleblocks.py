# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
# RIFT audit preset p1 (ADR-024): view templates, legends, title blocks, line styles, line patterns.
# Port to IronPython 3.4 of the maintainer's extraction script p1. Parity rule: same keys, same value
# computations, same sort orders. Changes from the original: no file system (dump() hands the JSON text to
# the add-in's collector), every sort key goes through str(), bare except clauses are narrowed by the engine.
# Produces the document 01_vues_lignes_cartouches.
import json, datetime
from Autodesk.Revit.DB import *

def iid(x):
    try:
        return int(x.Value)
    except:
        return int(x.IntegerValue)

def col(cls):
    return FilteredElementCollector(doc).OfClass(cls)

def gname(el):
    try:
        return Element.Name.GetValue(el)
    except:
        try:
            return el.Name
        except:
            return None

def rgb(c):
    try:
        if c and c.IsValid:
            return "%d,%d,%d" % (c.Red, c.Green, c.Blue)
    except:
        pass
    return None

def dump(name, obj):
    rift_collector.Put(name, json.dumps(obj, ensure_ascii=False))
    return name

data = {}

report_progress(10, "view templates")
vt = []
for v in col(View):
    try:
        if v.IsTemplate:
            vt.append({"id": iid(v.Id), "name": v.Name})
    except:
        pass
data["gabarits_de_vue"] = sorted(vt, key=lambda x: str(x["name"]))

report_progress(25, "legends")
lg = []
for v in col(View):
    try:
        if v.ViewType == ViewType.Legend and not v.IsTemplate:
            lg.append({"id": iid(v.Id), "name": v.Name, "scale": v.Scale})
    except:
        pass
data["legendes"] = sorted(lg, key=lambda x: str(x["name"]))

report_progress(40, "title blocks")
tb = []
for ts in FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType():
    d = {"id": iid(ts.Id), "type": gname(ts)}
    try:
        d["family"] = ts.FamilyName
    except:
        pass
    try:
        w = ts.get_Parameter(BuiltInParameter.SHEET_WIDTH)
        h = ts.get_Parameter(BuiltInParameter.SHEET_HEIGHT)
        if w:
            d["width_mm"] = round(w.AsDouble() * 304.8, 1)
        if h:
            d["height_mm"] = round(h.AsDouble() * 304.8, 1)
    except:
        pass
    tb.append(d)
data["cartouches"] = sorted(tb, key=lambda x: (str(x.get("family", "")), str(x["type"])))

report_progress(60, "line styles")
ls = []
cat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines)
for sc in cat.SubCategories:
    d = {"name": sc.Name, "id": iid(sc.Id)}
    if d["id"] < 0:   # system subcategory: localised name (<Thin Lines>)
        try: d["bic"] = str(sc.BuiltInCategory)
        except: pass
    try:
        d["line_weight"] = sc.GetLineWeight(GraphicsStyleType.Projection)
    except:
        pass
    try:
        d["color"] = rgb(sc.LineColor)
    except:
        pass
    try:
        lpid = sc.GetLinePatternId(GraphicsStyleType.Projection)
        iv = iid(lpid)
        if iv == -3000010:
            d["line_pattern"] = "Solid"
        elif iv < 0:
            d["line_pattern"] = "<%d>" % iv
        else:
            lpe = doc.GetElement(lpid)
            d["line_pattern"] = gname(lpe) if lpe else None
    except:
        pass
    ls.append(d)
data["types_lignes"] = sorted(ls, key=lambda x: str(x["name"]))

report_progress(85, "line patterns")
data["motifs_lignes"] = sorted(
    [{"id": iid(e.Id), "name": e.Name} for e in col(LinePatternElement)],
    key=lambda x: str(x["name"]))

data["_document"] = doc.Title
data["_generated"] = str(datetime.datetime.now())
dump("01_vues_lignes_cartouches", data)
report_progress(100, "done")
for k in data:
    if not k.startswith("_"):
        print("%-24s %d" % (k, len(data[k])))
