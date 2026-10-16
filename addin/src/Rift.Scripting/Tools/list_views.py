# rift-tool: read
# Views by type, with template, scale and the sheet they are on. inputs: view_type (e.g. FloorPlan, Section; optional)
on_sheet = {}
for vp in collect(Viewport):
    try:
        sh = doc.GetElement(vp.SheetId)
        on_sheet[iid(vp.ViewId)] = sh.SheetNumber
    except Exception:
        pass
wanted = arg("view_type")
out = []
for v in views(False):
    if isinstance(v, ViewSheet) or (wanted and str(v.ViewType) != wanted):
        continue
    d = {"id": iid(v.Id), "name": v.Name, "view_type": str(v.ViewType), "sheet": on_sheet.get(iid(v.Id))}
    try:
        d["scale"] = v.Scale
    except Exception:
        pass
    t = doc.GetElement(v.ViewTemplateId) if iid(v.ViewTemplateId) > 0 else None
    d["template"] = t.Name if t is not None else None
    out.append(d)
result = sorted(out, key=lambda x: (x["view_type"], str(x["name"])))
