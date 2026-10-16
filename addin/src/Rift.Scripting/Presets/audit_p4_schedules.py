# Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
# RIFT audit preset p4 (ADR-024): schedules with their fields, filters, sorting and the sheets that carry them.
# Port to IronPython 3.4 of the maintainer's extraction script p4. Parity rule: same keys, same value
# computations, same sort orders. Changes from the original: no file system (dump() hands the JSON text to
# the add-in's collector), every sort key goes through str(), bare except clauses are narrowed by the engine.
# Produces the document 04_nomenclatures.
import json, datetime
from Autodesk.Revit.DB import *

def iid(x):
    try: return int(x.Value)
    except: return int(x.IntegerValue)

def dump(name, obj):
    rift_collector.Put(name, json.dumps(obj, ensure_ascii=False))
    return name

report_progress(10, "schedules on sheets")
# sheet(s) carrying each schedule
sched_on_sheet = {}
for si in FilteredElementCollector(doc).OfClass(ScheduleSheetInstance):
    try:
        sid = iid(si.ScheduleId)
        sh = doc.GetElement(si.OwnerViewId)
        label = None
        if sh:
            try:
                label = "%s - %s" % (sh.SheetNumber, sh.Name)
            except:
                label = sh.Name
        sched_on_sheet.setdefault(sid, []).append(label)
    except:
        pass

report_progress(30, "schedules")
res = []
for vs in FilteredElementCollector(doc).OfClass(ViewSchedule):
    try:
        if vs.IsTemplate:
            continue
        d = {"id": iid(vs.Id), "name": vs.Name}
        try: d["revision_schedule"] = vs.IsTitleblockRevisionSchedule
        except: pass
        try:
            defn = vs.Definition
            cat = None
            try:
                cid = defn.CategoryId
                if cid and iid(cid) != -1:
                    cc = Category.GetCategory(doc, cid)
                    cat = cc.Name if cc else None      # localised name
                    if cc:
                        d["category_bic"] = str(cc.BuiltInCategory)   # stable key
            except:
                pass
            d["category"] = cat
            fields = []
            for i in range(defn.GetFieldCount()):
                try:
                    f = defn.GetField(i)
                    fields.append({"name": f.GetName(), "hidden": f.IsHidden})
                except:
                    pass
            d["fields"] = fields
            try: d["itemized"] = defn.IsItemized
            except: pass
            try: d["filter_count"] = defn.GetFilterCount()
            except: pass
            try: d["sort_group_count"] = defn.GetSortGroupFieldCount()
            except: pass
        except Exception as e:
            d["defn_error"] = str(e)
        d["on_sheets"] = sched_on_sheet.get(iid(vs.Id), [])
        res.append(d)
    except:
        pass
res = sorted(res, key=lambda x: str(x["name"]))

data = {"_document": doc.Title, "_generated": str(datetime.datetime.now()),
        "nomenclatures": res}
dump("04_nomenclatures", data)
report_progress(100, "done")
print("nomenclatures %d" % len(res))
