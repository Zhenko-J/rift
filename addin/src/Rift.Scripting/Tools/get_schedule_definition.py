# rift-tool: read
# The definition of a schedule: fields (hidden, heading), filters, sorting and grouping. inputs: schedule
vs = find_one([s for s in collect(ViewSchedule) if not s.IsTemplate], arg("schedule", required=True), "schedule")
defn = vs.Definition
fields = []
for i in range(defn.GetFieldCount()):
    f = defn.GetField(i)
    fields.append({"name": f.GetName(), "heading": f.ColumnHeading, "hidden": f.IsHidden, "field_id": f.FieldId.IntegerValue if hasattr(f.FieldId, "IntegerValue") else str(f.FieldId)})
filters = []
for i in range(defn.GetFilterCount()):
    flt = defn.GetFilter(i)
    fd = {"field": defn.GetField(flt.FieldId).GetName(), "type": str(flt.FilterType)}
    try:
        fd["value"] = flt.GetStringValue() if flt.IsStringValue else (flt.GetDoubleValue() if flt.IsDoubleValue else flt.GetIntegerValue())
    except Exception:
        pass
    filters.append(fd)
sorts = []
for i in range(defn.GetSortGroupFieldCount()):
    s = defn.GetSortGroupField(i)
    sorts.append({"field": defn.GetField(s.FieldId).GetName(), "order": str(s.SortOrder), "header": s.ShowHeader, "footer": s.ShowFooter})
c = Category.GetCategory(doc, defn.CategoryId) if iid(defn.CategoryId) != -1 else None
result = {"schedule": vs.Name, "category": c.Name if c else None, "itemized": defn.IsItemized, "fields": fields, "filters": filters, "sort_group": sorts}
