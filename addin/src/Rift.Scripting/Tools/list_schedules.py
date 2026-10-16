# rift-tool: read
# Schedules with category, fields, filters, sorting and the sheets that carry them.
on_sheet = {}
for si in collect(ScheduleSheetInstance):
    try:
        sh = doc.GetElement(si.OwnerViewId)
        on_sheet.setdefault(iid(si.ScheduleId), []).append("%s - %s" % (sh.SheetNumber, sh.Name))
    except Exception:
        pass
out = []
for vs in collect(ViewSchedule):
    if vs.IsTemplate:
        continue
    d = {"id": iid(vs.Id), "name": vs.Name}
    try:
        d["revision_schedule"] = vs.IsTitleblockRevisionSchedule
    except Exception:
        pass
    try:
        defn = vs.Definition
        c = Category.GetCategory(doc, defn.CategoryId) if iid(defn.CategoryId) != -1 else None
        d["category"] = c.Name if c is not None else None
        d["fields"] = [defn.GetField(i).GetName() for i in range(defn.GetFieldCount())]
        d["itemized"] = defn.IsItemized
        d["filters"] = defn.GetFilterCount()
        d["sort_group"] = defn.GetSortGroupFieldCount()
    except Exception as e:
        d["definition_error"] = str(e)
    d["on_sheets"] = on_sheet.get(d["id"], [])
    out.append(d)
result = sorted(out, key=lambda x: str(x["name"]))
