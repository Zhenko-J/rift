# rift-tool: read
# One view template: settings, controlled parameters, filters with visibility and overrides, views using it.
t = template(arg("template", required=True))
d = {"id": iid(t.Id), "name": t.Name, "view_type": str(t.ViewType)}
for attr in ("Scale", "DetailLevel", "Discipline", "DisplayStyle"):
    try:
        d[attr.lower() if attr == "Scale" else re.sub(r"(?<!^)(?=[A-Z])", "_", attr).lower()] = str(getattr(t, attr)) if attr != "Scale" else t.Scale
    except Exception:
        pass
controlled = []
try:
    non = set(iid(i) for i in t.GetNonControlledTemplateParameterIds())
    for pid in t.GetTemplateParameterIds():
        if iid(pid) in non:
            continue
        label = None
        try:
            label = LabelUtils.GetLabelFor(System.Enum.ToObject(BuiltInParameter, iid(pid))) if iid(pid) < 0 else name_of(doc.GetElement(pid))
        except Exception:
            label = str(iid(pid))
        controlled.append(label)
except Exception:
    pass
d["controlled_parameters"] = sorted([c for c in controlled if c], key=str)
filters = []
try:
    for fid in t.GetFilters():
        f = doc.GetElement(fid)
        o = t.GetFilterOverrides(fid)
        fd = {"id": iid(fid), "name": name_of(f), "visible": t.GetFilterVisibility(fid)}
        try:
            fd["enabled"] = t.GetIsFilterEnabled(fid)
        except Exception:
            pass
        fd["halftone"] = o.Halftone
        fd["transparency"] = o.Transparency
        fd["projection_line_color"] = rgb(o.ProjectionLineColor)
        fd["surface_foreground_color"] = rgb(o.SurfaceForegroundPatternColor)
        filters.append(fd)
except Exception:
    pass
d["filters"] = filters
d["views_using"] = sorted([v.Name for v in views(False) if iid(v.ViewTemplateId) == iid(t.Id)], key=str)
result = d
