# rift-tool: read
# View templates with their view type, how many parameters they control, and their filters.
out = []
for v in views(True):
    d = {"id": iid(v.Id), "name": v.Name, "view_type": str(v.ViewType)}
    try:
        d["controlled_parameters"] = v.GetTemplateParameterIds().Count - v.GetNonControlledTemplateParameterIds().Count
    except Exception:
        pass
    try:
        d["filters"] = sorted([name_of(doc.GetElement(f)) for f in v.GetFilters()], key=str)
    except Exception:
        d["filters"] = []
    out.append(d)
used = {}
for v in views(False):
    try:
        t = v.ViewTemplateId
        if t is not None and iid(t) > 0:
            used[iid(t)] = used.get(iid(t), 0) + 1
    except Exception:
        pass
for d in out:
    d["views_using"] = used.get(d["id"], 0)
result = sorted(out, key=lambda x: str(x["name"]))
