# rift-tool: read
# What a change would touch, grouped by relationship, before you make it (rvt-mcp's survey_change_impact). For each
# element: instances of a type, views using a view template, views and templates using a filter, elements hosted by it,
# dependent elements, the sheets a view is placed on. inputs: element_ids [..], limit=100 per group
limit = int(arg("limit", 100))
out = []
for i in arg("element_ids", required=True):
    el = element(i)
    d = summary(el)
    groups = {}
    if isinstance(el, ElementType):
        inst = [iid(x.Id) for x in FilteredElementCollector(doc).WhereElementIsNotElementType() if iid(x.GetTypeId()) == iid(el.Id)]
        groups["instances_of_type"] = inst
    if isinstance(el, View) and el.IsTemplate:
        groups["views_using_template"] = [iid(v.Id) for v in views(False) if iid(v.ViewTemplateId) == iid(el.Id)]
    if isinstance(el, FilterElement):
        groups["views_using_filter"] = [iid(v.Id) for v in collect(View) if el.Id in list(v.GetFilters())]
    if isinstance(el, View) and not el.IsTemplate:
        groups["sheets"] = [doc.GetElement(vp.SheetId).SheetNumber for vp in collect(Viewport) if iid(vp.ViewId) == iid(el.Id)]
    if isinstance(el, Material):
        groups["elements_using_material"] = [iid(x.Id) for x in FilteredElementCollector(doc).WhereElementIsNotElementType()
                                             if any(iid(m) == iid(el.Id) for m in x.GetMaterialIds(False))][:limit * 10]
    try:
        groups["hosted"] = [iid(x.Id) for x in collect(FamilyInstance) if x.Host is not None and iid(x.Host.Id) == iid(el.Id)]
    except Exception:
        pass
    try:
        deps = [iid(x) for x in el.GetDependentElements(None) if iid(x) != iid(el.Id)]
        groups["dependents"] = deps
    except Exception:
        pass
    d["groups"] = dict((k, {"count": len(v), "sample": v[:limit]}) for k, v in groups.items() if v)
    d["touches"] = sum(len(v) for v in groups.values())
    out.append(d)
result = out
